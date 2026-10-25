import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { musicNames, safeName, songDirectory, inside } from '../lib/music.mjs';
import { Inbox } from '../lib/inbox.mjs';
import { DownloadWorker } from '../lib/downloader.mjs';
import { EventEmitter } from 'node:events';
import { PassThrough } from 'node:stream';

function temporary(t) { const root = fs.mkdtempSync(path.join(os.tmpdir(), 'stemkit-music-test-')); t.after(() => fs.rmSync(root, { recursive: true, force: true })); return root; }

test('artist and song metadata takes precedence, with a title/channel fallback', () => {
  assert.deepEqual(musicNames({ artist: 'Artist', track: 'Song', title: 'Video title' }), { artist: 'Artist', song: 'Song' });
  assert.deepEqual(musicNames({ title: 'Artist - Song (Official Music Video)', channel: 'Channel' }), { artist: 'Artist', song: 'Song' });
  assert.deepEqual(musicNames({ title: 'Song', uploader: 'Artist - Topic' }), { artist: 'Artist', song: 'Song' });
  assert.deepEqual(musicNames({ title: 'Song' }), { artist: 'Unknown Artist', song: 'Song' });
});

test('Windows filenames preserve Unicode and cannot escape folders or use device names', () => {
  assert.equal(safeName('Björk: Jóga?'), 'Björk_ Jóga_');
  assert.equal(safeName('CON'), '_CON');
  assert.equal(safeName('..'), 'Untitled');
  assert.equal(safeName('Song. '), 'Song');
  assert.equal(safeName('x'.repeat(200)).length, 60);
  assert.equal(inside('C:\\music', 'C:\\music\\Artist\\Song'), true);
  assert.equal(inside('C:\\music', 'C:\\music-elsewhere\\Song'), false);
});

test('songs with identical artist/title keep separate folders for different video IDs', t => {
  const root = temporary(t); const names = { artist: 'Artist', song: 'Song' };
  const first = songDirectory(root, names, 'BaW_jenozKc'); fs.mkdirSync(first, { recursive: true });
  fs.writeFileSync(path.join(first, 'metadata.json'), JSON.stringify({ id: 'BaW_jenozKc' }));
  assert.equal(songDirectory(root, names, 'BaW_jenozKc'), first);
  assert.equal(songDirectory(root, names, 'YE7VzlLtp-4'), first + ' [YE7VzlLtp-4]');
});

test('old video queue entries migrate to MP3 jobs without using the video archive', t => {
  const root = temporary(t);
  fs.writeFileSync(path.join(root, 'queue.json'), JSON.stringify({ version: 1, paused: false, jobs: [{ id: 'old', videoId: 'BaW_jenozKc', url: 'https://www.youtube.com/watch?v=BaW_jenozKc', status: 'completed', filename: 'old.mp4' }] }));
  fs.writeFileSync(path.join(root, 'download-archive.txt'), 'youtube BaW_jenozKc\n');
  const inbox = new Inbox(root);
  assert.equal(inbox.state.jobs[0].status, 'queued');
  assert.equal(inbox.state.jobs[0].outputType, 'mp3');
  assert.equal(inbox.archiveIds().has('BaW_jenozKc'), false);
});

test('a missing cover leaves the job retryable and never records a successful MP3', async t => {
  const root = temporary(t); const inbox = new Inbox(path.join(root, 'data')); inbox.add('https://youtu.be/BaW_jenozKc');
  const worker = new DownloadWorker({ inbox, downloadsDir: path.join(root, 'music'), command: 'mock', ffmpeg: true, spawnProcess: (command, args) => {
    const child = new EventEmitter(); child.stdout = new PassThrough(); child.stderr = new PassThrough();
    setImmediate(() => {
      if (args.includes('--dump-single-json')) child.stdout.write(JSON.stringify({ id: 'BaW_jenozKc', title: 'Artist - Song', thumbnail: 'https://example.com/cover.jpg' }) + '\n');
      else fs.writeFileSync(path.join(args[args.indexOf('--paths') + 1], 'audio.mp3'), 'audio');
      child.stdout.end(); child.stderr.end(); setImmediate(() => child.emit('close', 0));
    });
    return child;
  } });
  await worker.kick();
  assert.equal(inbox.state.jobs[0].status, 'failed');
  assert.equal(inbox.archiveIds().size, 0);
  inbox.retry(inbox.state.jobs[0].id);
  assert.equal(inbox.state.jobs[0].status, 'queued');
});

test('FFmpeg absence fails clearly before downloading any media', async t => {
  const root = temporary(t); const inbox = new Inbox(root); inbox.add('https://youtu.be/BaW_jenozKc');
  const worker = new DownloadWorker({ inbox, downloadsDir: root, command: 'mock', ffmpeg: false, spawnProcess: () => { throw new Error('Should not spawn'); } });
  await worker.kick();
  assert.match(inbox.state.jobs[0].message, /FFmpeg is required/);
});
