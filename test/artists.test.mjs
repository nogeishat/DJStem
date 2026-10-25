import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import net from 'node:net';
import { artistSettings, renameArtist, setArtistCover } from '../lib/artists.mjs';
import { songPaths } from '../lib/library.mjs';
import { createApp } from '../server.mjs';

function temporary(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'stemkit-artists-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  return root;
}
function song(root, artist, title, id, content = id) {
  const audio = path.join(root, 'mp3', artist, title + '.mp3'), folder = path.join(root, 'stems', artist, title);
  fs.mkdirSync(path.dirname(audio), { recursive: true }); fs.mkdirSync(folder, { recursive: true });
  fs.writeFileSync(audio, content);
  fs.writeFileSync(path.join(folder, 'metadata.json'), JSON.stringify({ id, artist, title, bpm: 120 }));
  for (const file of ['cover.jpg', 'vocals.wav', 'drums.wav', 'bass.wav', 'other.wav', 'separation.json']) fs.writeFileSync(path.join(folder, file), content);
  return { artist, filename: path.relative(root, audio), stemsFolder: path.relative(root, folder), status: 'completed', videoId: id };
}
const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Zl1sAAAAASUVORK5CYII=', 'base64');
function cover(root) { const file = path.join(root, 'chosen.png'); fs.writeFileSync(file, png); return file; }

test('renaming moves MP3s and all song artifacts, updates queue and metadata, and remembers future downloads', t => {
  const root = temporary(t), state = { version: 1, jobs: [song(root, 'Old Artist', 'Song', 'BaW_jenozKc')] };
  let saved;
  const result = renameArtist({ root, from: 'Old Artist', to: 'Björk', state, saveState: () => { saved = structuredClone(state); } });
  assert.equal(result.name, 'Björk'); assert.equal(result.merged, false);
  assert.equal(fs.existsSync(path.join(root, 'mp3', 'Old Artist')), false);
  assert.equal(fs.existsSync(path.join(root, 'stems', 'Old Artist')), false);
  assert.equal(fs.readFileSync(path.join(root, saved.jobs[0].filename), 'utf8'), 'BaW_jenozKc');
  assert.equal(saved.jobs[0].artist, 'Björk');
  const folder = path.join(root, saved.jobs[0].stemsFolder);
  assert.equal(fs.readdirSync(folder).length, 7);
  assert.equal(JSON.parse(fs.readFileSync(path.join(folder, 'metadata.json'))).artist, 'Björk');
  assert.equal(path.dirname(songPaths(root, { artist: 'Old Artist', song: 'New Song' }, 'YE7VzlLtp-4').audio), path.join(root, 'mp3', 'Björk'));
  renameArtist({ root, from: 'Björk', to: 'New Name', state });
  assert.equal(path.dirname(songPaths(root, { artist: 'Old Artist', song: 'Another' }, 'YE7VzlLtp-4').audio), path.join(root, 'mp3', 'New Name'));
});

test('merging multiple artists preserves colliding songs, aligned stems, and the destination cover', t => {
  const root = temporary(t), state = { version: 1, jobs: [song(root, 'First', 'Song', 'BaW_jenozKc'), song(root, 'Second', 'Song', 'YE7VzlLtp-4'), song(root, 'Third', 'Song', 'abcdefghijk')] };
  setArtistCover({ root, name: 'First', image: cover(root) });
  const firstCover = artistSettings(root).covers.first;
  setArtistCover({ root, name: 'Second', image: cover(root) });
  const secondCover = artistSettings(root).covers.second;
  assert.equal(renameArtist({ root, from: 'Second', to: 'first', state }).merged, true);
  renameArtist({ root, from: 'Third', to: 'First', state });
  assert.deepEqual(fs.readdirSync(path.join(root, 'mp3')), ['First']);
  assert.deepEqual(fs.readdirSync(path.join(root, 'mp3', 'First')).sort(), ['Song [merged 2].mp3', 'Song [merged 3].mp3', 'Song.mp3']);
  for (const job of state.jobs) {
    assert.equal(path.basename(job.filename, '.mp3'), path.basename(job.stemsFolder));
    assert.equal(fs.readFileSync(path.join(root, job.filename), 'utf8'), job.videoId);
    assert.equal(fs.readFileSync(path.join(root, job.stemsFolder, 'vocals.wav'), 'utf8'), job.videoId);
  }
  assert.equal(artistSettings(root).covers.first, firstCover);
  assert.equal(artistSettings(root).covers.second, undefined);
  assert.ok(fs.existsSync(path.join(root, secondCover)), 'The previous artist cover is preserved on disk');
});

test('artist covers persist, follow renames, can be replaced and removed without touching song covers', t => {
  const root = temporary(t); const job = song(root, 'Artist', 'Song', 'BaW_jenozKc');
  setArtistCover({ root, name: 'Artist', image: cover(root) });
  const first = artistSettings(root).covers.artist;
  renameArtist({ root, from: 'Artist', to: 'Renamed' });
  assert.equal(artistSettings(root).covers.renamed, first);
  setArtistCover({ root, name: 'Renamed', image: cover(root) });
  const second = artistSettings(root).covers.renamed;
  assert.notEqual(first, second); assert.deepEqual(fs.readFileSync(path.join(root, second)), png);
  setArtistCover({ root, name: 'Renamed', image: null });
  assert.equal(artistSettings(root).covers.renamed, undefined);
  assert.equal(fs.readFileSync(path.join(root, 'stems', 'Renamed', path.basename(job.stemsFolder), 'cover.jpg'), 'utf8'), 'BaW_jenozKc');
});

test('case-only artist renaming preserves filenames and queue references', t => {
  const root = temporary(t), state = { jobs: [song(root, 'artist', 'Song', 'BaW_jenozKc')] };
  renameArtist({ root, from: 'artist', to: 'Artist', state });
  assert.deepEqual(fs.readdirSync(path.join(root, 'mp3')), ['Artist']);
  assert.equal(state.jobs[0].filename, path.join('mp3', 'Artist', 'Song.mp3'));
  assert.equal(fs.readFileSync(path.join(root, state.jobs[0].filename), 'utf8'), 'BaW_jenozKc');
});

test('the player artist helper accepts Unicode requests and edits covers without the downloader running', async t => {
  const root = temporary(t); song(root, 'Björk', 'Jóga', 'BaW_jenozKc');
  const input = Buffer.from(JSON.stringify({ operation: 'cover', root, name: 'Björk', image: cover(root) })).toString('base64');
  const script = fileURLToPath(new URL('../scripts/manage-artist.mjs', import.meta.url));
  const listener = net.createServer();
  await new Promise(resolve => listener.listen(0, '127.0.0.1', resolve));
  const port = listener.address().port;
  await new Promise(resolve => listener.close(resolve));
  const result = spawnSync(process.execPath, [script], { input, encoding: 'utf8', env: { ...process.env, PORT: String(port) }, timeout: 10000, windowsHide: true });
  assert.equal(result.status, 0, result.stderr);
  assert.equal(JSON.parse(result.stdout).name, 'Björk');
  assert.ok(fs.existsSync(path.join(root, artistSettings(root).covers['björk'])));
});

test('invalid names and invalid metadata cannot partially move an artist', t => {
  const root = temporary(t), state = { jobs: [song(root, 'Artist', 'Song', 'BaW_jenozKc')] };
  const before = structuredClone(state);
  for (const name of ['../outside', 'CON', 'Name.', '', 'x'.repeat(61)]) assert.throws(() => renameArtist({ root, from: 'Artist', to: name, state }));
  fs.writeFileSync(path.join(root, state.jobs[0].stemsFolder, 'metadata.json'), '{invalid');
  assert.throws(() => renameArtist({ root, from: 'Artist', to: 'Renamed', state }));
  assert.deepEqual(state, before); assert.ok(fs.existsSync(path.join(root, state.jobs[0].filename)));
  assert.equal(fs.existsSync(path.join(root, 'mp3', 'Renamed')), false);
  assert.equal(fs.existsSync(path.join(root, '.artist-folders.json')), false);
});

test('a failed file move rolls back all earlier moves and preserves existing songs', t => {
  const root = temporary(t), state = { jobs: [song(root, 'Artist', 'Song', 'BaW_jenozKc'), song(root, 'Target', 'Song', 'YE7VzlLtp-4')] };
  const before = structuredClone(state), rename = fs.renameSync;
  let failed = false;
  fs.renameSync = (source, destination) => {
    if (!failed && source === path.join(root, 'stems', 'Artist', 'Song')) { failed = true; throw new Error('Simulated locked stems'); }
    return rename(source, destination);
  };
  try { assert.throws(() => renameArtist({ root, from: 'Artist', to: 'Target', state }), /locked stems/); }
  finally { fs.renameSync = rename; }
  assert.deepEqual(state, before);
  for (const job of state.jobs) assert.equal(fs.readFileSync(path.join(root, job.filename), 'utf8'), job.videoId);
  assert.deepEqual(fs.readdirSync(path.join(root, 'mp3', 'Target')), ['Song.mp3']);
});

test('a settings write failure restores metadata, queue, and physical folders', t => {
  const root = temporary(t), state = { jobs: [song(root, 'Artist', 'Song', 'BaW_jenozKc')] }, before = structuredClone(state);
  const rename = fs.renameSync; let saved;
  fs.renameSync = (source, destination) => { if (destination === path.join(root, '.artist-folders.json')) throw new Error('Simulated full drive'); return rename(source, destination); };
  try { assert.throws(() => renameArtist({ root, from: 'Artist', to: 'Target', state, saveState: () => { saved = structuredClone(state); } }), /full drive/); }
  finally { fs.renameSync = rename; }
  assert.deepEqual(state, before); assert.deepEqual(saved, before);
  assert.equal(JSON.parse(fs.readFileSync(path.join(root, before.jobs[0].stemsFolder, 'metadata.json'))).artist, 'Artist');
  assert.ok(fs.existsSync(path.join(root, before.jobs[0].filename)));
});

test('live artist editing updates downloader state and rejects renames during processing and foreign origins', async t => {
  const root = temporary(t), library = path.join(root, 'library'); const job = song(library, 'Artist', 'Song', 'BaW_jenozKc');
  const worker = { running: false, stop() {}, kick() {} };
  const app = createApp({ root, downloadsDir: library, command: '', downloaderVersion: '', ffmpeg: false, separator: null, analyzer: null, workerFactory: () => worker });
  app.inbox.state.jobs.push(job); app.inbox.save();
  await new Promise(resolve => app.server.listen(0, '127.0.0.1', resolve));
  t.after(() => new Promise(resolve => app.server.close(resolve)));
  const base = `http://127.0.0.1:${app.server.address().port}`;
  const post = (body, headers = {}) => fetch(base + '/api/artists', { method: 'POST', headers: { 'Content-Type': 'application/json', ...headers }, body: JSON.stringify(body) });
  worker.running = true;
  assert.equal((await post({ operation: 'rename', name: 'Artist', newName: 'Renamed' })).status, 400);
  worker.running = false;
  assert.equal((await post({ operation: 'rename', name: 'Artist', newName: 'Renamed' }, { Origin: 'https://example.com' })).status, 403);
  const response = await post({ operation: 'rename', name: 'Artist', newName: 'Renamed' });
  assert.equal(response.status, 200); assert.equal((await response.json()).name, 'Renamed');
  assert.equal(app.inbox.state.jobs[0].filename, path.join('mp3', 'Renamed', 'Song.mp3'));
  assert.equal(JSON.parse(fs.readFileSync(path.join(root, 'data', 'queue.json'))).jobs[0].artist, 'Renamed');
});
