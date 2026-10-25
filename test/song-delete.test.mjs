import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import net from 'node:net';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { deleteSong } from '../lib/song-delete.mjs';
import { Inbox } from '../lib/inbox.mjs';
import { createApp } from '../server.mjs';
import { normalizeLink } from '../lib/links.mjs';

const youtube = 'BaW_jenozKc', other = 'YE7VzlLtp-4';
const url = id => `https://www.youtube.com/watch?v=${id}`;
function fixture(t) {
  const project = fs.mkdtempSync(path.join(os.tmpdir(), 'stemkit-delete-'));
  const root = path.join(project, 'library'), dataDir = path.join(project, 'data');
  fs.mkdirSync(root); fs.mkdirSync(dataDir);
  t.after(() => { assert.ok(project.startsWith(path.join(os.tmpdir(), 'stemkit-delete-'))); fs.rmSync(project, { recursive: true, force: true }); });
  return { project, root, dataDir };
}
function song(root, id = youtube, name = 'Song', sourceUrl = url(id)) {
  const audio = path.join(root, 'mp3', 'Artist', name + '.mp3'), folder = path.join(root, 'stems', 'Artist', name);
  fs.mkdirSync(path.dirname(audio), { recursive: true }); fs.mkdirSync(folder, { recursive: true }); fs.writeFileSync(audio, id);
  fs.writeFileSync(path.join(folder, 'metadata.json'), JSON.stringify({ id, title: name, sourceUrl }));
  for (const file of ['cover.jpg', 'separation.json', 'vocals.wav', 'drums.wav', 'bass.wav', 'other.wav']) fs.writeFileSync(path.join(folder, file), id);
  fs.mkdirSync(path.join(folder, '.work')); fs.writeFileSync(path.join(folder, '.work', 'partial.wav'), id);
  return { audio, folder, job: { id: 'job-' + id, videoId: id, url: sourceUrl, filename: path.relative(root, audio), stemsFolder: path.relative(root, folder), status: 'completed', outputType: 'mp3' } };
}
function store(dataDir, jobs) {
  const inbox = new Inbox(dataDir); inbox.state.jobs = jobs; inbox.save();
  fs.writeFileSync(inbox.archivePath, jobs.map(job => job.videoId.startsWith('soundcloud-') ? 'soundcloud ' + job.videoId.slice(11) : 'youtube ' + job.videoId).join('\n') + '\n');
  return inbox;
}
function unchanged(root, dataDir, target, before) {
  assert.equal(fs.readFileSync(target.audio, 'utf8'), youtube); assert.ok(fs.existsSync(path.join(target.folder, 'vocals.wav')));
  assert.equal(fs.readFileSync(path.join(dataDir, 'queue.json'), 'utf8'), before.queue);
  assert.equal(fs.readFileSync(path.join(dataDir, 'links.txt'), 'utf8'), before.links);
  assert.equal(fs.readFileSync(path.join(dataDir, 'mp3-download-archive.txt'), 'utf8'), before.archive);
  assert.ok(!fs.readdirSync(root).some(name => name.startsWith('.song-delete-')));
}
const records = dataDir => Object.fromEntries(['queue', 'links', 'archive'].map((key, i) => [key, fs.readFileSync(path.join(dataDir, ['queue.json', 'links.txt', 'mp3-download-archive.txt'][i]), 'utf8')]));

test('delete removes a song, all stems/artwork, link, archive ID and dependent mixes while keeping other songs', t => {
  const { root, dataDir } = fixture(t), target = song(root), retained = song(root, other, 'Other');
  const inbox = store(dataDir, [target.job, retained.job]);
  fs.writeFileSync(path.join(dataDir, 'player-settings.json'), JSON.stringify({ audio: target.audio, volume: 52, savedMixes: [
    { Id: 'original', OriginalAudio: target.audio }, { Id: 'donor', OriginalAudio: retained.audio, AddedAudio: target.audio },
    { Id: 'earlier', OriginalAudio: retained.audio, Sources: [target.audio], Banks: [{ Audio: target.audio }] }, { Id: 'keep', OriginalAudio: retained.audio }
  ], playlists: [{ Id: 'playlist', Name: 'Keep playlist', Items: [target.audio, retained.audio, 'mix:original', 'mix:donor', 'mix:earlier', 'mix:keep'] }] }));
  const result = deleteSong({ root, dataDir, audio: target.audio, state: inbox.state });
  assert.equal(result.cleanupPending, false); assert.equal(result.removedJobs, 1); assert.equal(fs.existsSync(target.audio), false); assert.equal(fs.existsSync(target.folder), false);
  assert.ok(fs.existsSync(retained.audio)); assert.ok(fs.existsSync(retained.folder)); assert.deepEqual(inbox.state.jobs, [retained.job]);
  assert.equal(fs.readFileSync(path.join(dataDir, 'links.txt'), 'utf8'), url(other) + '\n'); assert.equal(fs.readFileSync(inbox.archivePath, 'utf8'), 'youtube ' + other + '\n');
  const preferences = JSON.parse(fs.readFileSync(path.join(dataDir, 'player-settings.json')));
  assert.equal(preferences.audio, null); assert.equal(preferences.volume, 52); assert.deepEqual(preferences.savedMixes.map(mix => mix.Id), ['keep']);
  assert.deepEqual(preferences.playlists[0].Items, [retained.audio, 'mix:keep']);
  assert.deepEqual(fs.readdirSync(root).sort(), ['mp3', 'stems']);
  assert.equal(inbox.add(url(youtube)).added, 1); assert.equal(new Inbox(dataDir).state.jobs.length, 2);
});

test('SoundCloud deletion removes numeric ID, permalink/private/share aliases and allows a fresh add', t => {
  const { root, dataDir } = fixture(t), permalink = 'https://soundcloud.com/artist/track/s-Secret', share = 'https://on.soundcloud.com/AbCd';
  const target = song(root, 'soundcloud-12345', 'SoundCloud song', 'https://soundcloud.com/artist/track');
  target.job.url = share; target.job.linkIds = [normalizeLink(share).id, normalizeLink(permalink).id];
  const inbox = store(dataDir, [target.job]); fs.writeFileSync(path.join(dataDir, 'links.txt'), [share, permalink, 'https://api.soundcloud.com/tracks/12345', url(other)].join('\n') + '\n');
  deleteSong({ root, dataDir, audio: target.audio, state: inbox.state });
  assert.equal(inbox.archiveIds().size, 0); assert.equal(fs.readFileSync(path.join(dataDir, 'links.txt'), 'utf8'), url(other) + '\n');
  assert.equal(inbox.add(permalink).added, 1); assert.equal(inbox.add(share).added, 1);
});

test('songs without stems/metadata or a downloader record can be deleted', t => {
  const { root, dataDir } = fixture(t), target = song(root); fs.rmSync(target.folder, { recursive: true });
  const result = deleteSong({ root, dataDir, audio: target.audio });
  assert.equal(result.removedJobs, 0); assert.equal(result.cleanupPending, false); assert.equal(fs.existsSync(target.audio), false);
});

test('delete validates exact song paths, rejects traversal and linked folders, and protects actively processed songs', t => {
  const { root, dataDir, project } = fixture(t), target = song(root), inbox = store(dataDir, [target.job]); const before = records(dataDir);
  for (const audio of [root, path.join(root, 'mp3'), path.join(root, 'mp3', 'Artist'), path.join(root, 'stems', 'Artist', 'Song', 'vocals.wav'), path.join(project, 'outside.mp3'), path.join(root, 'mp3', 'Artist', '..', '..', 'outside.mp3')]) assert.throws(() => deleteSong({ root, dataDir, audio, state: inbox.state }));
  for (const name of ['.mp3', '....mp3']) {
    const audio = path.join(root, 'mp3', 'Artist', name); fs.writeFileSync(audio, 'keep');
    assert.throws(() => deleteSong({ root, dataDir, audio, state: inbox.state }), /safe stems|MP3 artist/); assert.ok(fs.existsSync(target.folder));
  }
  inbox.state.jobs[0].status = 'separating'; assert.throws(() => deleteSong({ root, dataDir, audio: target.audio, state: inbox.state }), /this song to finish/); inbox.state.jobs[0].status = 'completed';
  const outside = path.join(project, 'outside'); fs.mkdirSync(outside); fs.writeFileSync(path.join(outside, 'keep.txt'), 'keep');
  fs.symlinkSync(outside, path.join(target.folder, 'linked'), process.platform === 'win32' ? 'junction' : 'dir');
  assert.throws(() => deleteSong({ root, dataDir, audio: target.audio, state: inbox.state }), /links/); assert.equal(fs.readFileSync(path.join(outside, 'keep.txt'), 'utf8'), 'keep');
  unchanged(root, dataDir, target, before);
});

test('a locked stem move rolls back the MP3 move and all records', t => {
  const { root, dataDir } = fixture(t), target = song(root), inbox = store(dataDir, [target.job]), before = records(dataDir), rename = fs.renameSync;
  fs.renameSync = (from, to) => { if (from === target.folder) throw new Error('Simulated locked stems'); return rename(from, to); };
  try { assert.throws(() => deleteSong({ root, dataDir, audio: target.audio, state: inbox.state }), /locked stems/); }
  finally { fs.renameSync = rename; }
  unchanged(root, dataDir, target, before); assert.equal(inbox.state.jobs.length, 1);
});

test('failed record writes restore the song, links, archive, queue and live state', t => {
  const { root, dataDir } = fixture(t), target = song(root), inbox = store(dataDir, [target.job]), before = records(dataDir), rename = fs.renameSync;
  fs.renameSync = (from, to) => { if (to === inbox.archivePath) throw new Error('Simulated failed archive write'); return rename(from, to); };
  try { assert.throws(() => deleteSong({ root, dataDir, audio: target.audio, state: inbox.state }), /archive write/); }
  finally { fs.renameSync = rename; }
  unchanged(root, dataDir, target, before); assert.equal(inbox.state.jobs.length, 1);
});

test('a locked rollback preserves staged song files for recovery', t => {
  const { root, dataDir } = fixture(t), target = song(root), inbox = store(dataDir, [target.job]), before = records(dataDir), rename = fs.renameSync;
  fs.renameSync = (from, to) => {
    if (from === target.folder) throw new Error('Simulated locked stems');
    if (to === target.audio) throw new Error('Simulated locked rollback');
    return rename(from, to);
  };
  try { assert.throws(() => deleteSong({ root, dataDir, audio: target.audio, state: inbox.state }), /Recovery could not finish.*preserved/); }
  finally { fs.renameSync = rename; }
  const staging = fs.readdirSync(root).find(name => name.startsWith('.song-delete-'));
  assert.ok(staging); assert.equal(fs.readFileSync(path.join(root, staging, 'song.mp3'), 'utf8'), youtube);
  assert.equal(fs.readFileSync(path.join(target.folder, 'vocals.wav'), 'utf8'), youtube);
  assert.deepEqual(records(dataDir), before); assert.equal(inbox.state.jobs.length, 1);
});

test('live deletion updates the service in memory, preserves another running job and rejects foreign origins', async t => {
  const { root, dataDir, project } = fixture(t), target = song(root), retained = song(root, other, 'Other');
  const worker = { running: true, stop() {}, kick() {} };
  const app = createApp({ root: project, dataDir, downloadsDir: root, command: '', downloaderVersion: '', ffmpeg: false, separator: null, analyzer: null, workerFactory: () => worker });
  app.inbox.state.jobs.push(target.job, retained.job); app.inbox.save(); retained.job.status = 'downloading';
  await new Promise(resolve => app.server.listen(0, '127.0.0.1', resolve)); t.after(() => new Promise(resolve => app.server.close(resolve)));
  const address = `http://127.0.0.1:${app.server.address().port}`;
  const post = (headers = {}) => fetch(address + '/api/songs/delete', { method: 'POST', headers: { 'Content-Type': 'application/json', ...headers }, body: JSON.stringify({ audio: target.audio }) });
  assert.equal((await post({ Origin: 'https://example.com' })).status, 403);
  assert.equal((await (await fetch(address + '/api/state')).json()).songDeletion, true);
  assert.equal((await post()).status, 200); assert.deepEqual(app.inbox.state.jobs, [retained.job]);
  app.inbox.update(retained.job, { status: 'completed' }); assert.equal(new Inbox(dataDir).state.jobs.length, 1);
  assert.equal(app.inbox.add(url(youtube)).added, 1); assert.equal(fs.existsSync(target.audio), false);
});

test('offline player helper deletes records without opening a second service', async t => {
  const { project, root, dataDir } = fixture(t), target = song(root); store(dataDir, [target.job]);
  for (const directory of ['scripts', 'lib']) fs.mkdirSync(path.join(project, directory));
  for (const file of ['scripts/manage-song.mjs', 'lib/song-delete.mjs', 'lib/music.mjs', 'lib/links.mjs', 'lib/paths.mjs']) fs.copyFileSync(fileURLToPath(new URL('../' + file, import.meta.url)), path.join(project, file));
  const listener = net.createServer(); await new Promise(resolve => listener.listen(0, '127.0.0.1', resolve)); const port = listener.address().port; await new Promise(resolve => listener.close(resolve));
  const input = Buffer.from(JSON.stringify({ operation: 'delete', root, audio: target.audio })).toString('base64');
  const result = spawnSync(process.execPath, [path.join(project, 'scripts', 'manage-song.mjs')], { input, encoding: 'utf8', env: { ...process.env, PORT: String(port) }, windowsHide: true, timeout: 10000 });
  assert.equal(result.status, 0, result.stderr); assert.equal(JSON.parse(result.stdout).cleanupPending, false); assert.equal(fs.existsSync(target.audio), false); assert.equal(new Inbox(dataDir).state.jobs.length, 0);
});
