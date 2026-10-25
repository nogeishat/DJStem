import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { EventEmitter } from 'node:events';
import { PassThrough } from 'node:stream';
import http from 'node:http';
import { normalizeLink, extractLinks } from '../lib/links.mjs';
import { Inbox } from '../lib/inbox.mjs';
import { DownloadWorker, downloadArgs, metadataArgs } from '../lib/downloader.mjs';
import { createApp } from '../server.mjs';

const id = 'BaW_jenozKc';
const url = `https://www.youtube.com/watch?v=${id}`;
function temporary(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'stemkit-test-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  return root;
}
function mockSpawn(handler) {
  return (command, args, options) => {
    const child = new EventEmitter();
    child.stdout = new PassThrough(); child.stderr = new PassThrough();
    child.kill = () => { child.stdout.end(); child.stderr.end(); child.emit('close', null); };
    setImmediate(() => handler(child, command, args, options));
    return child;
  };
}
function closeChild(child, code) { child.stdout.end(); child.stderr.end(); setImmediate(() => child.emit('close', code)); }

test('video ID normalization recognizes alternate URLs and strips tracking/playlist/timestamps', () => {
  for (const input of [url, `https://youtu.be/${id}?si=abc&t=10`, `https://m.youtube.com/watch?v=${id}&list=PLtest`, `https://youtube.com/shorts/${id}`, `https://youtube.com/live/${id}`, `https://www.youtube-nocookie.com/embed/${id}`, `https://music.youtube.com/watch?v=${id}`]) {
    assert.deepEqual(normalizeLink(input), { id, url });
  }
  for (const input of ['https://youtube.com/playlist?list=abc', 'https://youtube.com/@channel', `https://youtube.com.evil.test/watch?v=${id}`, `https://evil.test/?v=${id}`, `https://user@youtube.com/watch?v=${id}`, `ftp://youtube.com/watch?v=${id}`, `${url.slice(0, -1)}`, `https://youtube.com:8080/watch?v=${id}`]) assert.equal(normalizeLink(input), null);
});

test('mixed text deduplicates a batch and counts unsupported links', () => {
  const result = extractLinks(`Here (${url}&amp;t=20).\nhttps://youtu.be/${id}\nhttps://example.com/other`);
  assert.deepEqual(result.links, [{ id, url }]);
  assert.equal(result.repeated, 1); assert.equal(result.rejected.length, 1);
});

test('saved queue, failures, and download archive all reject repeats across restarts', t => {
  const root = temporary(t);
  const inbox = new Inbox(root);
  assert.deepEqual(inbox.add(`${url}\nhttps://youtu.be/${id}`), { added: 1, skipped: 1, rejected: 0 });
  inbox.update(inbox.state.jobs[0], { status: 'failed', message: 'Offline' });
  const restored = new Inbox(root);
  assert.equal(restored.add(`https://youtube.com/shorts/${id}`).skipped, 1);
  assert.equal(restored.state.jobs.length, 1);
  assert.equal(fs.readFileSync(path.join(root, 'links.txt'), 'utf8'), url + '\n');
  fs.writeFileSync(restored.archivePath, 'youtube YE7VzlLtp-4\n');
  assert.equal(restored.add('https://youtu.be/YE7VzlLtp-4').skipped, 1);
  restored.retry(restored.state.jobs[0].id);
  assert.equal(restored.state.jobs[0].status, 'queued');
  assert.throws(() => restored.retry(restored.state.jobs[0].id));
});

test('restart recovers interrupted jobs and keeps queue pause preference', t => {
  const root = temporary(t); const inbox = new Inbox(root);
  inbox.add(url); inbox.state.paused = true;
  inbox.update(inbox.state.jobs[0], { status: 'downloading', progress: 50 });
  const restored = new Inbox(root);
  assert.equal(restored.state.jobs[0].status, 'queued');
  assert.equal(restored.state.paused, true);
});

test('invalid batches and corrupt state fail without overwriting saved links', t => {
  const root = temporary(t); const inbox = new Inbox(root);
  assert.throws(() => inbox.add('https://example.com'));
  assert.equal(inbox.state.jobs.length, 0);
  fs.writeFileSync(inbox.statePath, '{ broken');
  assert.throws(() => new Inbox(root));
  assert.equal(fs.readFileSync(inbox.statePath, 'utf8'), '{ broken');
});

test('yt-dlp requests audio only, with MP3 conversion, tags, a JPEG cover, and canonical URLs', t => {
  const root = temporary(t); const inbox = new Inbox(root); inbox.add(url);
  const args = downloadArgs(path.join(root, 'source.info.json'), root);
  assert.ok(args.includes('--no-playlist')); assert.ok(args.includes('--ignore-config'));
  assert.equal(args[args.indexOf('--format') + 1], 'bestaudio');
  assert.equal(args[args.indexOf('--audio-format') + 1], 'mp3');
  assert.equal(args[args.indexOf('--convert-thumbnails') + 1], 'jpg');
  for (const flag of ['--embed-metadata', '--embed-thumbnail', '--write-thumbnail']) assert.ok(args.includes(flag));
  assert.deepEqual(metadataArgs(inbox.state.jobs[0]).slice(-2), ['--', url]);
});

test('worker serially saves real output paths and skips archive entries without spawning', async t => {
  const root = temporary(t); const inbox = new Inbox(path.join(root, 'data'));
  inbox.add(`${url}\nhttps://youtu.be/YE7VzlLtp-4`);
  fs.writeFileSync(inbox.archivePath, 'youtube YE7VzlLtp-4\n');
  let spawns = 0;
  const worker = new DownloadWorker({ inbox, downloadsDir: root, command: 'mock', ffmpeg: true, spawnProcess: mockSpawn((child, command, args, options) => {
    spawns++; assert.equal(options.shell, false);
    if (args.includes('--dump-single-json')) child.stdout.write(JSON.stringify({ title: 'Test video', artist: 'Test artist', track: 'Test song', thumbnail: 'https://example.com/cover.jpg', id }) + '\n');
    else {
      const work = args[args.indexOf('--paths') + 1];
      fs.writeFileSync(path.join(work, 'audio.mp3'), 'test audio');
      fs.writeFileSync(path.join(work, 'audio.jpg'), 'test cover');
      child.stdout.write('STEMKIT_PROGRESS:' + JSON.stringify({ downloaded_bytes: 50, total_bytes: 100, status: 'downloading' }) + '\n');
    }
    closeChild(child, 0);
  }) });
  await Promise.all([worker.kick(), worker.kick()]);
  assert.equal(spawns, 2); assert.equal(inbox.state.jobs[0].status, 'completed');
  assert.equal(inbox.state.jobs[0].title, 'Test song');
  assert.equal(inbox.state.jobs[0].filename, path.join('mp3', 'Test artist', 'Test song.mp3'));
  const folder = path.join(root, 'stems', 'Test artist', 'Test song');
  assert.deepEqual(fs.readdirSync(folder).sort(), ['cover.jpg', 'metadata.json']);
  assert.deepEqual(fs.readdirSync(path.join(root, 'mp3', 'Test artist')), ['Test song.mp3']);
  const metadata = JSON.parse(fs.readFileSync(path.join(folder, 'metadata.json')));
  assert.equal(metadata.title, 'Test song'); assert.equal(metadata.artist, 'Test artist');
  assert.equal(metadata.sourceUrl, url); assert.equal(metadata.stemkit, undefined);
  assert.ok(inbox.archiveIds().has(id));
  assert.equal(inbox.state.jobs[1].status, 'skipped');
});

test('a failed job does not block the next video and exposes the error for retry', async t => {
  const root = temporary(t); const inbox = new Inbox(root); inbox.add(`${url}\nhttps://youtu.be/YE7VzlLtp-4`);
  let count = 0;
  const worker = new DownloadWorker({ inbox, downloadsDir: root, command: 'mock', ffmpeg: true, spawnProcess: mockSpawn(child => {
    count++; child.stderr.write('ERROR: Video unavailable\n'); closeChild(child, 1);
  }) });
  await worker.kick();
  assert.equal(count, 2); assert.ok(inbox.state.jobs.every(job => job.status === 'failed'));
  assert.equal(inbox.state.jobs[0].message, 'ERROR: Video unavailable');
  assert.equal(inbox.archiveIds().size, 0);
});

test('pause holds jobs; resume starts them; stopping preserves a resumable queue', async t => {
  const root = temporary(t); const inbox = new Inbox(root); inbox.add(url); inbox.state.paused = true;
  let child;
  const worker = new DownloadWorker({ inbox, downloadsDir: root, command: 'mock', ffmpeg: true, spawnProcess: mockSpawn(instance => { child = instance; }) });
  await worker.kick(); assert.equal(worker.child, null);
  inbox.state.paused = false;
  const running = worker.kick();
  await new Promise(resolve => setImmediate(resolve));
  assert.ok(child); worker.stop(); await running;
  assert.equal(inbox.state.jobs[0].status, 'queued');
});

test('a missing downloader records a useful failure instead of blocking the queue', async t => {
  const root = temporary(t); const inbox = new Inbox(root); inbox.add(url);
  const worker = new DownloadWorker({ inbox, downloadsDir: root, command: path.join(root, 'missing-downloader.exe'), ffmpeg: true });
  await worker.kick();
  assert.equal(inbox.state.jobs[0].status, 'failed');
  assert.match(inbox.state.jobs[0].message, /yt-dlp was not found/);
});

test('saved MP3s support byte ranges and reject missing files and path traversal', async t => {
  const root = temporary(t);
  const app = createApp({ dataDir: path.join(root, 'data'), downloadsDir: path.join(root, 'downloads'), downloaderVersion: null, ffmpeg: true });
  app.inbox.add(url);
  const job = app.inbox.state.jobs[0];
  fs.writeFileSync(path.join(root, 'downloads', 'song.mp3'), '0123456789');
  app.inbox.update(job, { status: 'completed', filename: 'song.mp3' });
  await new Promise(resolve => app.server.listen(0, '127.0.0.1', resolve));
  t.after(() => new Promise(resolve => app.server.close(resolve)));
  const endpoint = `http://127.0.0.1:${app.server.address().port}/api/jobs/${job.id}/file`;
  const response = await fetch(endpoint, { headers: { Range: 'bytes=2-5' } });
  assert.equal(response.status, 206); assert.equal(await response.text(), '2345');
  assert.equal(response.headers.get('content-type'), 'audio/mpeg');
  assert.equal(response.headers.get('content-range'), 'bytes 2-5/10');
  assert.equal((await fetch(endpoint, { headers: { Range: 'bytes=15-' } })).status, 416);
  app.inbox.update(job, { filename: '../queue.json' });
  assert.equal((await fetch(endpoint)).status, 404);
  app.inbox.update(job, { filename: 'missing.mp4' });
  assert.equal((await fetch(endpoint)).status, 404);
});

test('HTTP saves/exports links, skips repeats, rejects foreign origins and invalid input', async t => {
  const root = temporary(t); let kicked = 0;
  const app = createApp({ dataDir: root, downloadsDir: path.join(root, 'downloads'), downloaderVersion: 'mock', ffmpeg: true, workerFactory: () => ({ kick: async () => { kicked++; }, stop() {} }) });
  await new Promise(resolve => app.server.listen(0, '127.0.0.1', resolve));
  t.after(() => new Promise(resolve => app.server.close(resolve)));
  const base = `http://127.0.0.1:${app.server.address().port}`;
  const post = (route, body, headers = {}) => fetch(base + route, { method: 'POST', headers: { 'Content-Type': 'application/json', ...headers }, body: JSON.stringify(body) });
  assert.equal((await fetch(base + '/')).status, 404);
  assert.deepEqual(await (await post('/api/links', { text: url })).json(), { added: 1, skipped: 0, rejected: 0 });
  assert.equal((await (await post('/api/links', { text: `https://youtu.be/${id}` })).json()).skipped, 1);
  assert.equal(await (await fetch(base + '/api/links')).text(), url + '\n');
  assert.equal((await post('/api/links', { text: url }, { Origin: 'https://example.com' })).status, 403);
  const foreignHostStatus = await new Promise((resolve, reject) => {
    const request = http.get(base + '/api/state', { headers: { Host: 'example.com' } }, response => { response.resume(); resolve(response.statusCode); });
    request.on('error', reject);
  });
  assert.equal(foreignHostStatus, 403);
  assert.equal((await post('/api/links', { text: 'not a link' })).status, 400);
  assert.equal((await post('/api/links', { text: 'a'.repeat(70000) })).status, 400);
  await post('/api/pause', { paused: true });
  assert.equal((await (await fetch(base + '/api/state')).json()).paused, true);
  await post('/api/pause', { paused: false }); assert.equal(kicked, 3);
});
