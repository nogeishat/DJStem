import fs from 'node:fs';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
import { inside } from './music.mjs';
import { isSongId, normalizeLink } from './links.mjs';

const identity = file => process.platform === 'win32' ? path.resolve(file).toLowerCase() : path.resolve(file);
const same = (a, b) => typeof a === 'string' && identity(a) === identity(b);

function safePath(root, file, tree = false) {
  if (!inside(root, file)) throw new Error('The song path is outside the music library.');
  for (let current = file; identity(current) !== identity(root); current = path.dirname(current)) {
    if (fs.existsSync(current) && fs.lstatSync(current).isSymbolicLink()) throw new Error('Songs containing links to other locations cannot be deleted.');
  }
  if (tree && fs.existsSync(file) && fs.lstatSync(file).isDirectory()) for (const name of fs.readdirSync(file)) safePath(root, path.join(file, name), true);
}

function atomicWrite(file, content) {
  const temporary = file + '.' + randomUUID() + '.tmp';
  try { fs.writeFileSync(temporary, content); fs.renameSync(temporary, file); }
  finally { if (fs.existsSync(temporary)) fs.unlinkSync(temporary); }
}

// Move the two song artifacts out of the library before committing records.
// Any move/write failure restores both the files and their saved references.
export function deleteSong({ root, dataDir, audio, state }) {
  root = path.resolve(root); audio = path.resolve(audio); dataDir = path.resolve(dataDir);
  const relative = path.relative(path.join(root, 'mp3'), audio).split(path.sep);
  if (relative.length !== 2 || relative.some(part => !part || part === '.' || part === '..') || path.extname(audio).toLowerCase() !== '.mp3' || !inside(path.join(root, 'mp3'), audio)) throw new Error('Choose a song in an MP3 artist folder.');
  const basename = path.basename(relative[1], path.extname(relative[1]));
  if (!basename || /^[. ]+$/.test(basename) || /[. ]$/.test(basename)) throw new Error('This filename does not identify a safe stems song folder.');
  const folder = path.join(root, 'stems', relative[0], basename);
  safePath(root, audio); safePath(root, folder, true);
  if (!fs.existsSync(audio) || !fs.statSync(audio).isFile()) throw new Error('This song no longer exists. Refresh the library.');
  const queueFile = path.join(dataDir, 'queue.json');
  state ??= fs.existsSync(queueFile) ? JSON.parse(fs.readFileSync(queueFile, 'utf8')) : { version: 1, paused: false, jobs: [] };
  if (state.version !== 1 || !Array.isArray(state.jobs)) throw new Error('The downloader queue is invalid.');
  let metadata = {};
  try { metadata = JSON.parse(fs.readFileSync(path.join(folder, 'metadata.json'), 'utf8')); } catch { }
  const ids = new Set(), urls = new Set();
  const rememberUrl = url => { if (typeof url !== 'string') return; urls.add(url); const link = normalizeLink(url); if (link) ids.add(link.id); };
  if (isSongId(metadata.id)) ids.add(metadata.id);
  rememberUrl(metadata.sourceUrl);
  const matches = job => job.filename && same(path.resolve(root, job.filename), audio) || ids.has(job.videoId) || (job.linkIds || []).some(id => ids.has(id)) || urls.has(job.url) || ids.has(normalizeLink(job.url)?.id);
  const removed = new Set();
  for (let changed = true; changed;) {
    changed = false;
    for (const job of state.jobs) if (!removed.has(job) && matches(job)) {
      removed.add(job); if (typeof job.videoId === 'string') ids.add(job.videoId); for (const id of job.linkIds || []) ids.add(id); rememberUrl(job.url); rememberUrl(job.sourceUrl); changed = true;
    }
  }
  if ([...removed].some(job => ['downloading', 'analyzing', 'separating'].includes(job.status))) throw new Error('Wait for this song to finish download and stem processing before deleting it.');
  const next = { ...state, jobs: state.jobs.filter(job => !removed.has(job)) };
  const archiveFile = path.join(dataDir, 'mp3-download-archive.txt'), linksFile = path.join(dataDir, 'links.txt'), preferencesFile = path.join(dataDir, 'player-settings.json');
  const archive = fs.existsSync(archiveFile) ? fs.readFileSync(archiveFile, 'utf8').split(/\r?\n/).filter(line => {
    const match = /^(youtube|soundcloud)\s+([A-Za-z0-9_-]+)$/i.exec(line.trim());
    return !match || !ids.has(match[1].toLowerCase() === 'soundcloud' ? 'soundcloud-' + match[2] : match[2]);
  }).join('\n') : null;
  const links = fs.existsSync(linksFile) ? fs.readFileSync(linksFile, 'utf8').split(/\r?\n/).filter(line => !urls.has(line.trim()) && !ids.has(normalizeLink(line.trim())?.id)).join('\n') : next.jobs.map(job => job.url).join('\n') + (next.jobs.length ? '\n' : '');
  let preferences, deletedMixIds = [];
  if (fs.existsSync(preferencesFile)) {
    preferences = JSON.parse(fs.readFileSync(preferencesFile, 'utf8'));
    const depends = mix => same(mix.OriginalAudio, audio) || same(mix.AddedAudio, audio) || (mix.Sources || []).some(file => same(file, audio)) || (mix.Banks || []).some(bank => same(bank.Audio, audio));
    deletedMixIds = (preferences.savedMixes || []).filter(depends).map(mix => mix.Id);
    preferences.savedMixes = (preferences.savedMixes || []).filter(mix => !depends(mix));
    for (const playlist of preferences.playlists || []) playlist.Items = (playlist.Items || []).filter(item => !same(item, audio) && !deletedMixIds.some(id => item === 'mix:' + id));
    if (same(preferences.audio, audio)) preferences.audio = null;
  }
  const writes = [[queueFile, JSON.stringify(next, null, 2) + '\n'], [linksFile, links]];
  if (archive !== null) writes.push([archiveFile, archive]);
  if (preferences) writes.push([preferencesFile, JSON.stringify(preferences, null, 2) + '\n']);
  const backups = writes.map(([file]) => [file, fs.existsSync(file) ? fs.readFileSync(file) : null]);
  const staging = path.join(root, '.song-delete-' + randomUUID()); safePath(root, staging);
  const moved = []; let committed = false;
  fs.mkdirSync(dataDir, { recursive: true }); fs.mkdirSync(staging);
  try {
    for (const source of [audio, folder]) if (fs.existsSync(source)) {
      const target = path.join(staging, source === audio ? 'song.mp3' : 'stems'); safePath(root, target);
      fs.renameSync(source, target); moved.push([source, target]);
    }
    for (const [file, content] of writes) atomicWrite(file, content);
    state.jobs = next.jobs; committed = true;
  } catch (error) {
    const recoveryErrors = [];
    for (const [file, content] of backups) {
      try {
        if (content === null) { if (fs.existsSync(file)) fs.unlinkSync(file); }
        else if (!fs.existsSync(file) || !fs.readFileSync(file).equals(content)) atomicWrite(file, content);
      } catch (recoveryError) { recoveryErrors.push(recoveryError.message); }
    }
    for (const [source, target] of moved.reverse()) {
      try { if (fs.existsSync(target)) fs.renameSync(target, source); }
      catch (recoveryError) { recoveryErrors.push(recoveryError.message); }
    }
    if (recoveryErrors.length) throw new Error(`${error.message} Recovery could not finish: ${recoveryErrors.join('; ')}. Any remaining song files are preserved in ${staging}.`);
    throw error;
  } finally {
    // Never remove staged song files if a rollback itself encounters a lock.
    if (!committed && fs.existsSync(staging) && fs.readdirSync(staging).length === 0) fs.rmdirSync(staging);
  }
  let cleanupPending = false;
  try { safePath(root, staging, true); fs.rmSync(staging, { recursive: true }); }
  catch { cleanupPending = true; }
  return { audio, folder, removedJobs: removed.size, deletedMixIds, cleanupPending, ...(cleanupPending ? { remainingFiles: staging } : {}) };
}
