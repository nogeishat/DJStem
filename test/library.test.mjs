import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { libraryRoot, songPaths, savedSongPaths } from '../lib/library.mjs';
import { planMigration, applyMigration } from '../scripts/migrate-library.mjs';

function temporary(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'stemkit-library-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true })); return root;
}
function oldLibrary(t) {
  const root = temporary(t), source = path.join(root, 'old'), destination = path.join(root, 'new');
  const folder = path.join(source, 'Artist', 'Song'); fs.mkdirSync(folder, { recursive: true });
  fs.writeFileSync(path.join(folder, 'Song.mp3'), 'original MP3 with embedded tags and cover');
  fs.writeFileSync(path.join(folder, 'cover.jpg'), 'cover');
  fs.writeFileSync(path.join(folder, 'metadata.json'), JSON.stringify({ id: 'BaW_jenozKc', artist: 'Artist', track: 'Song', stemkit: { song: 'Song', artist: 'Artist' } }));
  fs.mkdirSync(path.join(root, 'data'));
  fs.writeFileSync(path.join(root, 'data', 'queue.json'), JSON.stringify({ version: 1, paused: false, jobs: [{ videoId: 'BaW_jenozKc', status: 'failed', filename: path.relative(source, path.join(folder, 'Song.mp3')), title: 'Song', artist: 'Artist', outputType: 'mp3' }] }));
  fs.writeFileSync(path.join(root, 'data', 'mp3-download-archive.txt'), 'youtube BaW_jenozKc\n');
  return { root, source, destination, folder };
}

test('library configuration selects the requested root, and MP3s and artifacts use separate trees', t => {
  const root = temporary(t), target = path.join(root, 'external');
  fs.writeFileSync(path.join(root, 'settings.json'), JSON.stringify({ libraryRoot: target }));
  assert.equal(libraryRoot(root), target);
  const output = songPaths(target, { artist: 'Björk', song: 'Jóga' }, 'BaW_jenozKc');
  assert.equal(output.audio, path.join(target, 'mp3', 'Björk', 'Jóga.mp3'));
  assert.equal(output.folder, path.join(target, 'stems', 'Björk', 'Jóga'));
  assert.throws(() => savedSongPaths(target, { filename: '../other.mp3', stemsFolder: 'stems/Artist/Song' }));
  assert.throws(() => savedSongPaths(target, { filename: 'mp3/Artist/Song.mp3', stemsFolder: '../other' }));
});

test('matching titles and occupied MP3 filenames cannot overwrite another video', t => {
  const root = temporary(t), names = { artist: 'Artist', song: 'Song' };
  const first = songPaths(root, names, 'BaW_jenozKc');
  fs.mkdirSync(first.folder, { recursive: true });
  fs.writeFileSync(path.join(first.folder, 'metadata.json'), JSON.stringify({ id: 'BaW_jenozKc' }));
  fs.mkdirSync(path.dirname(first.audio), { recursive: true }); fs.writeFileSync(first.audio, 'first');
  assert.deepEqual(songPaths(root, names, 'BaW_jenozKc'), first);
  const second = songPaths(root, names, 'YE7VzlLtp-4');
  assert.equal(second.audio, path.join(root, 'mp3', 'Artist', 'Song [YE7VzlLtp-4].mp3'));
  assert.equal(second.folder, path.join(root, 'stems', 'Artist', 'Song [YE7VzlLtp-4]'));
  const occupied = songPaths(root, { artist: 'Artist', song: 'Other' }, 'BaW_jenozKc');
  fs.writeFileSync(occupied.audio, 'unrelated file');
  assert.notEqual(songPaths(root, { artist: 'Artist', song: 'Other' }, 'BaW_jenozKc').audio, occupied.audio);
});

test('migration verifies bytes, puts only MP3s under mp3, updates the queue and retains duplicate history', t => {
  const options = oldLibrary(t), plan = planMigration(options);
  assert.equal(plan.plans.length, 1);
  assert.equal(fs.existsSync(options.destination), false, 'Preview must not create anything');
  applyMigration(plan);
  assert.deepEqual(fs.readdirSync(path.join(options.destination, 'mp3', 'Artist')), ['Song.mp3']);
  assert.equal(fs.readFileSync(path.join(options.destination, 'mp3', 'Artist', 'Song.mp3'), 'utf8'), 'original MP3 with embedded tags and cover');
  assert.deepEqual(fs.readdirSync(path.join(options.destination, 'stems', 'Artist', 'Song')).sort(), ['cover.jpg', 'metadata.json']);
  const state = JSON.parse(fs.readFileSync(path.join(options.root, 'data', 'queue.json')));
  assert.equal(state.jobs[0].filename, path.join('mp3', 'Artist', 'Song.mp3'));
  assert.equal(state.jobs[0].stemsFolder, path.join('stems', 'Artist', 'Song'));
  assert.equal(fs.readFileSync(path.join(options.root, 'data', 'mp3-download-archive.txt'), 'utf8'), 'youtube BaW_jenozKc\n');
  assert.ok(fs.existsSync(path.join(options.root, 'data', 'queue.json.before-library-move.json')));
  assert.equal(fs.existsSync(options.folder), false);
  assert.equal(planMigration(options).plans.length, 0, 'Repeating migration is safe');
});

test('migration refuses conflicting destination files and preserves original files and queue', t => {
  const options = oldLibrary(t);
  const queue = fs.readFileSync(path.join(options.root, 'data', 'queue.json'), 'utf8');
  const plan = planMigration(options), target = plan.plans[0].target;
  fs.mkdirSync(target.folder, { recursive: true });
  fs.writeFileSync(path.join(target.folder, 'metadata.json'), JSON.stringify({ id: 'BaW_jenozKc' }));
  fs.mkdirSync(path.dirname(target.audio), { recursive: true }); fs.writeFileSync(target.audio, 'different existing MP3');
  assert.throws(() => planMigration(options), /different file exists/);
  assert.equal(fs.readFileSync(target.audio, 'utf8'), 'different existing MP3');
  assert.ok(fs.existsSync(path.join(options.folder, 'Song.mp3')));
  assert.equal(fs.readFileSync(path.join(options.root, 'data', 'queue.json'), 'utf8'), queue);
});
