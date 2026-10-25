import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { EventEmitter } from 'node:events';
import { PassThrough } from 'node:stream';
import { Inbox } from '../lib/inbox.mjs';
import { DownloadWorker } from '../lib/downloader.mjs';
import { StemSeparator, stemsReady, STEM_MODELS, UVR_COMMIT } from '../lib/stems.mjs';

const url = 'https://youtu.be/BaW_jenozKc';
function workspace(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'stemkit-stems-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  return root;
}
function outputs(audio, folder) {
  fs.mkdirSync(folder, { recursive: true });
  const wave = Buffer.alloc(56);
  wave.write('RIFF'); wave.writeUInt32LE(48, 4); wave.write('WAVEfmt ', 8);
  wave.writeUInt32LE(16, 16); wave.writeUInt16LE(1, 20); wave.writeUInt16LE(2, 22);
  wave.writeUInt32LE(44100, 24); wave.writeUInt32LE(264600, 28);
  wave.writeUInt16LE(6, 32); wave.writeUInt16LE(24, 34); wave.write('data', 36); wave.writeUInt32LE(12, 40);
  for (const stem of Object.keys(STEM_MODELS)) fs.writeFileSync(path.join(folder, stem + '.wav'), wave);
  const manifest = { pipelineVersion: 1, uvrCommit: UVR_COMMIT, models: STEM_MODELS,
    inputSha256: createHash('sha256').update(fs.readFileSync(audio)).digest('hex'), sampleRate: 44100, channels: 2, subtype: 'PCM_24' };
  fs.writeFileSync(path.join(folder, 'separation.json'), JSON.stringify(manifest));
}
function saved(t) {
  const root = workspace(t), downloadsDir = path.join(root, 'downloads');
  const inbox = new Inbox(path.join(root, 'data')); inbox.add(url);
  const audio = path.join(downloadsDir, 'mp3', 'Artist', 'Song.mp3');
  const folder = path.join(downloadsDir, 'stems', 'Artist', 'Song');
  fs.mkdirSync(path.dirname(audio), { recursive: true });
  fs.writeFileSync(audio, 'saved audio');
  fs.mkdirSync(folder, { recursive: true });
  fs.writeFileSync(path.join(folder, 'metadata.json'), JSON.stringify({ id: 'BaW_jenozKc', stemkit: { sourceUrl: url } }));
  const job = inbox.state.jobs[0];
  inbox.update(job, { filename: path.relative(downloadsDir, audio), stemsFolder: path.relative(downloadsDir, folder), libraryLayout: 2, status: 'completed' });
  fs.writeFileSync(inbox.archivePath, 'youtube BaW_jenozKc\n');
  return { root, inbox, audio, folder, job, downloadsDir };
}

test('startup backfills saved MP3s and resumes separation, without repeating completed stems', t => {
  const { inbox, job } = saved(t);
  const migrated = new Inbox(inbox.dataDir, { enableStems: true });
  assert.equal(migrated.state.jobs[0].status, 'queued');
  assert.equal(migrated.state.jobs[0].filename, job.filename);
  migrated.update(migrated.state.jobs[0], { status: 'separating', stemStatus: 'processing' });
  const recovered = new Inbox(inbox.dataDir, { enableStems: true });
  assert.equal(recovered.state.jobs[0].status, 'queued');
  recovered.update(recovered.state.jobs[0], { status: 'completed', stemStatus: 'completed' });
  assert.equal(new Inbox(inbox.dataDir, { enableStems: true }).state.jobs[0].status, 'completed');
});

test('separation failure preserves the archived MP3; retry processes it without yt-dlp', async t => {
  const { inbox, job, audio, folder, downloadsDir } = saved(t);
  let calls = 0;
  const worker = new DownloadWorker({ inbox, downloadsDir, ffmpeg: false, spawnProcess: () => assert.fail('Must reuse the saved MP3'), separator: {
    async run(input, folder, progress) {
      calls++; assert.equal(input, audio); assert.equal(job.status, 'separating');
      if (calls === 1) throw new Error('Model failed');
      progress({ progress: 90, message: 'Saving stems' }); outputs(input, folder);
    }, stop() {},
  } });
  inbox.update(job, { status: 'queued' });
  await worker.kick();
  assert.equal(job.status, 'failed'); assert.equal(job.stemStatus, 'failed');
  assert.ok(fs.existsSync(audio)); assert.ok(inbox.archiveIds().has(job.videoId));
  inbox.retry(job.id); await worker.kick();
  assert.equal(job.status, 'completed'); assert.equal(job.stemStatus, 'completed');
  const metadata = JSON.parse(fs.readFileSync(path.join(folder, 'metadata.json')));
  assert.deepEqual(JSON.parse(fs.readFileSync(path.join(folder, 'separation.json'))).models, STEM_MODELS);
  assert.equal(metadata.stemkit.stems, undefined, 'Model details belong only in separation.json');
  assert.equal(inbox.add(url).skipped, 1); await worker.kick(); assert.equal(calls, 2);
});

test('fresh downloads automatically run separation only after the MP3 bundle is saved', async t => {
  const root = workspace(t), inbox = new Inbox(path.join(root, 'data'));
  const downloadsDir = path.join(root, 'downloads'); inbox.add(url);
  let separations = 0;
  const worker = new DownloadWorker({ inbox, downloadsDir, command: 'mock', ffmpeg: true,
    separator: { async run(audio, folder) {
      separations++; assert.ok(fs.existsSync(audio));
      assert.ok(fs.existsSync(path.join(folder, 'cover.jpg')));
      assert.deepEqual(fs.readdirSync(path.dirname(audio)), ['Song.mp3']);
      assert.ok(inbox.archiveIds().has('BaW_jenozKc')); outputs(audio, folder);
    }, stop() {} },
    spawnProcess(command, args) {
      const child = new EventEmitter(); child.stdout = new PassThrough(); child.stderr = new PassThrough();
      setImmediate(() => {
        if (args.includes('--dump-single-json')) child.stdout.write(JSON.stringify({ id: 'BaW_jenozKc', artist: 'Artist', track: 'Song', thumbnail: 'https://example.com/cover.jpg' }) + '\n');
        else {
          const work = args[args.indexOf('--paths') + 1];
          fs.writeFileSync(path.join(work, 'audio.mp3'), 'audio'); fs.writeFileSync(path.join(work, 'audio.jpg'), 'cover');
        }
        child.stdout.end(); child.stderr.end(); setImmediate(() => child.emit('close', 0));
      }); return child;
    },
  });
  await worker.kick(); assert.equal(separations, 1);
  assert.equal(inbox.state.jobs[0].stemStatus, 'completed');
});

test('valid stems are cached only for the same source audio and exact requested models', t => {
  const { audio, folder } = saved(t);
  outputs(audio, folder); assert.equal(stemsReady(audio, folder), true);
  fs.writeFileSync(audio, 'different audio'); assert.equal(stemsReady(audio, folder), false);
  outputs(audio, folder);
  const manifestPath = path.join(folder, 'separation.json');
  const manifest = JSON.parse(fs.readFileSync(manifestPath)); manifest.models.vocals = 'wrong model';
  fs.writeFileSync(manifestPath, JSON.stringify(manifest)); assert.equal(stemsReady(audio, folder), false);
  outputs(audio, folder); fs.writeFileSync(path.join(folder, 'drums.wav'), 'broken'); assert.equal(stemsReady(audio, folder), false);
});

test('UVR subprocess uses isolated Python and parses progress; valid stems skip spawning', async t => {
  const { root, audio, folder } = saved(t);
  const python = path.join(root, 'tools', 'stem-runtime', 'Scripts', 'python.exe');
  fs.mkdirSync(path.dirname(python), { recursive: true }); fs.writeFileSync(python, 'fixture');
  const config = path.join(root, 'tools', 'stem-models', 'mdx', 'config.json');
  fs.mkdirSync(path.dirname(config), { recursive: true }); fs.writeFileSync(config, '{}');
  let calls = 0; const events = [];
  const separator = new StemSeparator({ root, spawnProcess(command, args, options) {
    calls++; assert.equal(command, python); assert.equal(options.shell, false);
    assert.equal(args[args.indexOf('--input') + 1], audio);
    const child = new EventEmitter(); child.stdout = new PassThrough(); child.stderr = new PassThrough();
    setImmediate(() => {
      child.stdout.write('STEMKIT_STEMS:{"progress":60,"message":"Vocals"}\n'); outputs(audio, folder);
      child.stdout.end(); child.stderr.end(); setImmediate(() => child.emit('close', 0));
    }); return child;
  } });
  await separator.run(audio, folder, event => events.push(event));
  assert.equal(events[0].progress, 60);
  await separator.run(audio, folder); assert.equal(calls, 1);
  await assert.rejects(separator.run(audio, path.join(root, 'outside')), /Invalid stems folder/);
});

test('stopping separation leaves its saved MP3 queued for restart', async t => {
  const { inbox, job, downloadsDir } = saved(t);
  let rejectRun, started;
  const ready = new Promise(resolve => started = resolve);
  const worker = new DownloadWorker({ inbox, downloadsDir, separator: {
    run() { started(); return new Promise((_, reject) => rejectRun = reject); },
    stop() { rejectRun(new Error('stopped')); },
  } });
  inbox.update(job, { status: 'queued' });
  const running = worker.kick(); await ready; worker.stop(); await running;
  assert.equal(job.status, 'queued'); assert.ok(job.filename);
});
