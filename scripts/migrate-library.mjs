import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { ROOT } from '../server.mjs';
import { libraryRoot, songPaths } from '../lib/library.mjs';
import { inside } from '../lib/music.mjs';
import { stemsReady } from '../lib/stems.mjs';

const hash = file => createHash('sha256').update(fs.readFileSync(file)).digest('hex');
const atomicJson = (file, value) => {
  fs.writeFileSync(file + '.tmp', JSON.stringify(value, null, 2) + '\n');
  fs.renameSync(file + '.tmp', file);
};

export function planMigration({ root = ROOT, source = path.join(root, 'downloads'), destination = libraryRoot(root) } = {}) {
  source = path.resolve(source); destination = path.resolve(destination);
  const queueFile = path.join(root, 'data', 'queue.json');
  const state = JSON.parse(fs.readFileSync(queueFile, 'utf8'));
  if (state.jobs.some(job => ['downloading', 'separating', 'analyzing'].includes(job.status))) throw new Error('Close StemKit before migrating an active queue.');
  const plans = [], reserved = new Set();
  for (const job of state.jobs.filter(job => job.filename && job.libraryLayout !== 2)) {
    const audio = path.resolve(source, job.filename);
    if (!inside(source, audio) || path.extname(audio).toLowerCase() !== '.mp3') throw new Error('Invalid source MP3 path.');
    const oldFolder = path.dirname(audio);
    const metadata = JSON.parse(fs.readFileSync(path.join(oldFolder, 'metadata.json'), 'utf8'));
    if (metadata.id !== job.videoId) throw new Error('Source metadata does not belong to the queued song.');
    const names = { artist: metadata.stemkit?.artist || metadata.artist || job.artist, song: metadata.stemkit?.song || metadata.track || job.title };
    const target = songPaths(destination, names, job.videoId, reserved);
    reserved.add(target.audio); reserved.add(target.folder);
    const files = [[audio, target.audio], [path.join(oldFolder, 'cover.jpg'), path.join(target.folder, 'cover.jpg')]];
    const oldStems = path.join(oldFolder, 'stems');
    if (job.stemStatus === 'completed' && !stemsReady(audio, oldStems)) throw new Error('The existing completed stems failed validation. Migration stopped.');
    for (const name of ['drums.wav', 'bass.wav', 'other.wav', 'vocals.wav', 'separation.json']) {
      if (fs.existsSync(path.join(oldStems, name))) files.push([path.join(oldStems, name), path.join(target.folder, name)]);
    }
    for (const [from, to] of files) {
      if (!inside(source, from) || !inside(destination, to)) throw new Error('Migration path leaves the requested library.');
      if (!fs.statSync(from).isFile()) throw new Error('Source is not a regular file.');
      if (fs.existsSync(to) && hash(from) !== hash(to)) throw new Error(`A different file exists at ${to}. Migration stopped.`);
    }
    plans.push({ job, metadata, metadataHash: hash(path.join(oldFolder, 'metadata.json')), audio, oldFolder, oldStems, target, files });
  }
  return { root, source, destination, queueFile, state, plans };
}

export function applyMigration(plan) {
  const { source, destination, queueFile, state, plans } = plan;
  // Verify every copied byte before changing queue paths or removing original files.
  for (const entry of plans) {
    fs.mkdirSync(entry.target.folder, { recursive: true });
    fs.mkdirSync(path.dirname(entry.target.audio), { recursive: true });
    const metadataFile = path.join(entry.target.folder, 'metadata.json');
    if (fs.existsSync(metadataFile) && JSON.parse(fs.readFileSync(metadataFile, 'utf8')).id !== entry.job.videoId) throw new Error('Destination metadata belongs to another song.');
    atomicJson(metadataFile, entry.metadata);
    for (const [from, to] of entry.files) {
      if (!fs.existsSync(to)) fs.copyFileSync(from, to, fs.constants.COPYFILE_EXCL);
      if (hash(from) !== hash(to)) throw new Error(`Copy verification failed for ${to}; original files are preserved.`);
    }
    const metadata = entry.metadata;
    metadata.stemkit ||= {};
    metadata.stemkit.audioFile = path.relative(destination, entry.target.audio);
    metadata.stemkit.stemsFolder = path.relative(destination, entry.target.folder);
    if (metadata.stemkit.stems) metadata.stemkit.stems.folder = '.';
    atomicJson(metadataFile, metadata);
    if (entry.job.stemStatus === 'completed' && !stemsReady(entry.target.audio, entry.target.folder)) throw new Error('Migrated stems failed validation; original files are preserved.');
    Object.assign(entry.job, { filename: path.relative(destination, entry.target.audio), stemsFolder: path.relative(destination, entry.target.folder), libraryLayout: 2 });
  }
  if (!plans.length) return;
  const backup = queueFile + '.before-library-move.json';
  if (!fs.existsSync(backup)) fs.copyFileSync(queueFile, backup, fs.constants.COPYFILE_EXCL);
  state.libraryRoot = destination;
  state.libraryLayout = 2;
  atomicJson(queueFile, state);
  for (const entry of plans) {
    for (const [from, to] of entry.files) {
      if (!inside(source, from) || !inside(destination, to) || hash(from) !== hash(to)) throw new Error('Refusing to remove an unverified source file.');
      fs.unlinkSync(from);
    }
    const metadataFile = path.join(entry.oldFolder, 'metadata.json');
    if (!inside(source, metadataFile) || hash(metadataFile) !== entry.metadataHash) throw new Error('Source metadata changed; it is preserved.');
    fs.unlinkSync(metadataFile);
    for (const folder of [entry.oldStems, entry.oldFolder, path.dirname(entry.oldFolder)]) {
      if (inside(source, folder) && fs.existsSync(folder) && fs.readdirSync(folder).length === 0) fs.rmdirSync(folder);
    }
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    if (process.argv.includes('--apply')) {
      let running = false;
      try { running = (await fetch(`http://127.0.0.1:${process.env.PORT || 4317}/api/state`, { signal: AbortSignal.timeout(1500) })).ok; } catch { }
      if (running) throw new Error('Close StemKit before moving the library.');
    }
    const plan = planMigration();
    console.log(JSON.stringify({ source: plan.source, destination: plan.destination, songs: plan.plans.map(entry => ({ song: entry.job.title, mp3: entry.target.audio, stems: entry.target.folder, files: entry.files.length + 1 })) }, null, 2));
    if (process.argv.includes('--apply')) { applyMigration(plan); console.log(`Moved and verified ${plan.plans.length} songs.`); }
    else console.log('Preview only. Run with --apply to move these files.');
  } catch (error) { console.error(error.message); process.exitCode = 1; }
}
