import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { EventEmitter } from 'node:events';
import { PassThrough } from 'node:stream';
import { compactMetadata, hasMusicAnalysis, hasTuningAnalysis, analysisFields, tuningFields } from '../lib/metadata.mjs';
import { analyzeLibraryTuning } from '../scripts/analyze-library-tuning.mjs';
import { MusicAnalyzer } from '../lib/analysis.mjs';
import { Inbox } from '../lib/inbox.mjs';
import { DownloadWorker } from '../lib/downloader.mjs';
import { updateSongMetadata } from '../scripts/update-song-metadata.mjs';

const id = 'BaW_jenozKc', url = `https://www.youtube.com/watch?v=${id}`;
const traits = { version: 2, source: 'mp3', duration: 120.12, bpm: 120, key: 'Eb minor', beatOffset: .13, beatConfidence: .8, keyConfidence: .7, tonic: 3, minor: true };
Object.assign(traits, { tuningVersion: 1, tuningHz: 432, tuningCents: 1200 * Math.log2(432 / 440), tuningConfidence: .9 });
function workspace(t) { const root = fs.mkdtempSync(path.join(os.tmpdir(), 'stemkit-metadata-')); t.after(() => fs.rmSync(root, { recursive: true, force: true })); return root; }
function download(child, args) {
  if (args.includes('--dump-single-json')) child.stdout.write(JSON.stringify({ id, artist: 'Artist', track: 'Song', title: 'Video title', thumbnail: 'https://example.com/cover.jpg', formats: [{ url: 'temporary token' }] }) + '\n');
  else {
    const work = args[args.indexOf('--paths') + 1]; fs.writeFileSync(path.join(work, 'audio.mp3'), 'MP3'); fs.writeFileSync(path.join(work, 'audio.jpg'), 'cover');
  }
  child.stdout.end(); child.stderr.end(); setImmediate(() => child.emit('close', 0));
}
function spawnDownload(_, args) { const child = new EventEmitter(); child.stdout = new PassThrough(); child.stderr = new PassThrough(); setImmediate(() => download(child, args)); return child; }

test('song metadata keeps useful music values and strips the full YouTube response', () => {
  const metadata = compactMetadata({ id, artist: 'Artist', track: 'Song', title: 'Video title', album: 'Album', release_date: '20200102', upload_date: '20250101', duration: 121,
    formats: [{ url: 'private stream' }], thumbnails: ['lots'], description: 'long text', tags: ['video tags'], view_count: 123, stemkit: { downloadedAt: '2026-10-05T12:00:00Z', stems: { models: 'technical' } } }, { analysis: traits });
  assert.deepEqual(Object.keys(metadata).sort(), ['album', 'analysis', 'artist', 'bpm', 'downloadedAt', 'duration', 'id', 'key', 'releaseDate', 'sourceUrl', 'title', 'tuningHz'].sort());
  assert.equal(metadata.title, 'Song'); assert.equal(metadata.releaseDate, '2020-01-02'); assert.equal(metadata.duration, 120.12);
  assert.equal(metadata.key, 'Eb minor'); assert.ok(hasMusicAnalysis(metadata));
  assert.equal(metadata.tuningHz, 432); assert.ok(hasTuningAnalysis(metadata));
  assert.throws(() => tuningFields({ ...traits, tuningHz: NaN }), /invalid/);
  assert.throws(() => tuningFields({ ...traits, tuningHz: 440 }), /invalid/);
  assert.ok(hasTuningAnalysis(tuningFields({ ...traits, tuningHz: null, tuningCents: null, tuningConfidence: 0 })));
  assert.deepEqual(compactMetadata(metadata), metadata, 'Cleaning an already clean file must preserve its analysis');
  assert.throws(() => analysisFields({ ...traits, bpm: NaN }), /invalid/);
  assert.throws(() => analysisFields({ ...traits, beatOffset: 10 }), /invalid/);
  assert.ok(hasMusicAnalysis(analysisFields({ ...traits, bpm: null, key: null, beatOffset: 0, beatConfidence: 0, keyConfidence: 0 })));
});

test('download analyzes the saved MP3 before separation and persists the values', async t => {
  const root = workspace(t), inbox = new Inbox(path.join(root, 'data')); inbox.add(url);
  const phases = [], worker = new DownloadWorker({ inbox, downloadsDir: root, command: 'mock', ffmpeg: true, spawnProcess: spawnDownload,
    analyzer: { async run(audio) { phases.push('analysis'); assert.equal(inbox.state.jobs[0].status, 'analyzing'); assert.equal(fs.readFileSync(audio, 'utf8'), 'MP3'); assert.ok(inbox.archiveIds().has(id)); return traits; }, stop() {} },
    separator: { async run(audio, folder) { phases.push('stems'); const metadata = JSON.parse(fs.readFileSync(path.join(folder, 'metadata.json'))); assert.equal(metadata.bpm, 120); assert.equal(metadata.key, 'Eb minor'); }, stop() {} },
  });
  await worker.kick(); const job = inbox.state.jobs[0];
  assert.deepEqual(phases, ['analysis', 'stems']); assert.equal(job.status, 'completed'); assert.equal(job.analysisStatus, 'completed');
  assert.equal(job.stemStatus, 'completed'); assert.deepEqual(fs.readdirSync(path.dirname(path.join(root, job.filename))), ['Song.mp3']);
});

test('analysis failure retries from the saved MP3 and does not redownload or damage completed stems', async t => {
  const root = workspace(t), inbox = new Inbox(path.join(root, 'data')); inbox.add(url);
  let downloads = 0, analyses = 0;
  const worker = new DownloadWorker({ inbox, downloadsDir: root, command: 'mock', ffmpeg: true,
    spawnProcess(...args) { downloads++; return spawnDownload(...args); },
    analyzer: { async run() { if (++analyses === 1) throw new Error('analysis error'); return traits; }, stop() {} },
  });
  await worker.kick(); const job = inbox.state.jobs[0];
  assert.equal(job.status, 'failed'); assert.equal(job.analysisStatus, 'failed'); assert.ok(job.filename); assert.ok(inbox.archiveIds().has(id));
  inbox.retry(job.id); await worker.kick(); assert.equal(job.status, 'completed'); assert.equal(job.analysisStatus, 'completed'); assert.equal(downloads, 2); assert.equal(analyses, 2);
  inbox.update(job, { status: 'queued' }); await worker.kick(); assert.equal(analyses, 2, 'Saved valid analysis must skip rerunning the analyzer');
});

test('a saved song with older BPM/key analysis only adds tuning when retried', async t => {
  const root = workspace(t), inbox = new Inbox(path.join(root, 'data')); inbox.add(url);
  let calls = 0;
  const worker = new DownloadWorker({ inbox, downloadsDir: root, command: 'mock', ffmpeg: true, spawnProcess: spawnDownload,
    analyzer: { async run(audio, options) { calls++; if (calls > 1) assert.equal(options.tuningOnly, true); return { ...traits, bpm: calls > 1 ? 200 : 120 }; } } });
  await worker.kick(); const job = inbox.state.jobs[0], file = path.join(root, job.stemsFolder, 'metadata.json');
  const old = JSON.parse(fs.readFileSync(file)); delete old.tuningHz; delete old.analysis.tuningVersion;
  fs.writeFileSync(file, JSON.stringify(old)); inbox.update(job, { status: 'queued' }); await worker.kick();
  const saved = JSON.parse(fs.readFileSync(file)); assert.equal(calls, 2); assert.equal(saved.bpm, 120); assert.equal(saved.tuningHz, 432); assert.equal(job.analysisStatus, 'completed');
});

test('interrupted analysis resumes and completed older songs are queued for analysis only once', t => {
  const root = workspace(t), inbox = new Inbox(root); inbox.add(url);
  inbox.update(inbox.state.jobs[0], { status: 'analyzing', filename: 'mp3/Artist/Song.mp3', stemStatus: 'completed' });
  const recovered = new Inbox(root, { enableAnalysis: true }); assert.equal(recovered.state.jobs[0].status, 'queued');
  recovered.update(recovered.state.jobs[0], { status: 'completed' });
  const backfill = new Inbox(root, { enableAnalysis: true }); assert.equal(backfill.state.jobs[0].status, 'queued'); assert.equal(backfill.state.jobs[0].stemStatus, 'completed');
  backfill.update(backfill.state.jobs[0], { status: 'completed', analysisStatus: 'completed' });
  assert.equal(new Inbox(root, { enableAnalysis: true }).state.jobs[0].status, 'completed');
});

test('analysis subprocess reads the MP3 with no shell, caches by content, and validates its output', async t => {
  const root = workspace(t), audio = path.join(root, 'song.mp3'); fs.writeFileSync(audio, 'one');
  fs.mkdirSync(path.join(root, 'scripts')); fs.writeFileSync(path.join(root, 'scripts', 'analyze-music.py'), 'script');
  fs.writeFileSync(path.join(root, 'scripts', 'tuning_estimator.py'), 'estimator');
  const python = path.join(root, 'tools', 'stem-runtime', 'Scripts', 'python.exe'); fs.mkdirSync(path.dirname(python), { recursive: true }); fs.writeFileSync(python, 'fixture');
  let spawns = 0;
  const analyzer = new MusicAnalyzer({ root, spawnProcess(command, args, options) {
    assert.equal(command, python); assert.equal(options.shell, false); assert.equal(options.windowsHide, true); assert.equal(args[args.indexOf('--input') + 1], audio); spawns++;
    const child = new EventEmitter(); child.stdout = new PassThrough(); child.stderr = new PassThrough();
    setImmediate(() => { const file = args[args.indexOf('--output') + 1]; fs.mkdirSync(path.dirname(file), { recursive: true }); fs.writeFileSync(file, JSON.stringify(traits)); child.stdout.end(); child.stderr.end(); child.emit('close', 0); }); return child;
  } });
  assert.deepEqual(await analyzer.run(audio), traits); await analyzer.run(audio); assert.equal(spawns, 1);
  fs.writeFileSync(audio, 'two'); await analyzer.run(audio); assert.equal(spawns, 2);
  fs.writeFileSync(path.join(root, 'scripts', 'tuning_estimator.py'), 'updated estimator'); await analyzer.run(audio); assert.equal(spawns, 3);
  analyzer.stop(); await assert.rejects(analyzer.run(audio), /stopped/);
});

test('one-time tuning update preserves existing music values, backs up JSON, and resumes without reanalysis', async t => {
  const root = workspace(t), library = path.join(root, 'library');
  const audio = path.join(library, 'mp3', 'Artist', 'Song.mp3'), folder = path.join(library, 'stems', 'Artist', 'Song');
  fs.mkdirSync(path.dirname(audio), { recursive: true }); fs.mkdirSync(folder, { recursive: true }); fs.writeFileSync(audio, 'unchanged MP3');
  const legacy = { id, artist: 'Artist', title: 'Song', bpm: 123, key: 'A minor', custom: 'keep', analysis: { version: 2, source: 'mp3', beatOffset: .12, beatConfidence: .8, keyConfidence: .5 } };
  const file = path.join(folder, 'metadata.json'); fs.writeFileSync(file, JSON.stringify(legacy));
  let calls = 0;
  const options = { root, library, log() {}, analyzer: { async run(input, mode) { assert.equal(input, audio); assert.equal(mode.tuningOnly, true); calls++; return traits; } } };
  assert.deepEqual(await analyzeLibraryTuning(options), { songs: 1, failed: 0 }); assert.equal(calls, 0);
  assert.deepEqual(await analyzeLibraryTuning({ ...options, apply: true }), { songs: 1, failed: 0 });
  const saved = JSON.parse(fs.readFileSync(file)); assert.ok(hasTuningAnalysis(saved)); assert.equal(saved.tuningHz, 432);
  assert.equal(saved.bpm, legacy.bpm); assert.equal(saved.key, legacy.key); assert.equal(saved.custom, 'keep'); assert.equal(saved.analysis.beatOffset, .12);
  assert.deepEqual(await analyzeLibraryTuning({ ...options, apply: true }), { songs: 0, failed: 0 }); assert.equal(calls, 1);
  assert.equal(fs.readFileSync(audio, 'utf8'), 'unchanged MP3'); assert.equal(fs.readdirSync(path.join(root, 'data', 'metadata-backups')).length, 1);
  const conflict = { ...options, apply: true, force: true, analyzer: { async run() { fs.writeFileSync(file, 'changed by downloader'); return traits; } } };
  assert.deepEqual(await analyzeLibraryTuning(conflict), { songs: 1, failed: 1 }); assert.equal(fs.readFileSync(file, 'utf8'), 'changed by downloader');
});

test('existing song update preserves MP3s and originals, simplifies metadata, and reuses saved analysis', async t => {
  const root = workspace(t), library = path.join(root, 'library'), audio = path.join(library, 'mp3', 'Artist', 'Song.mp3'), folder = path.join(library, 'stems', 'Artist', 'Song');
  fs.mkdirSync(path.dirname(audio), { recursive: true }); fs.mkdirSync(folder, { recursive: true }); fs.writeFileSync(audio, 'untouched MP3');
  const original = JSON.stringify({ id, artist: 'Artist', track: 'Song', formats: ['remove'], stemkit: { sourceUrl: url } });
  fs.writeFileSync(path.join(folder, 'metadata.json'), original);
  let calls = 0; const options = { root, library, log() {}, analyzer: { async run(input) { assert.equal(input, audio); calls++; return traits; } } };
  assert.equal(await updateSongMetadata(options), 1); assert.equal(calls, 0, 'Preview must not write or analyze');
  await updateSongMetadata({ ...options, apply: true }); await updateSongMetadata({ ...options, apply: true });
  assert.equal(calls, 1); assert.equal(fs.readFileSync(audio, 'utf8'), 'untouched MP3');
  const metadata = JSON.parse(fs.readFileSync(path.join(folder, 'metadata.json'))); assert.ok(hasMusicAnalysis(metadata)); assert.equal(metadata.formats, undefined);
  assert.ok(fs.readdirSync(path.join(root, 'data', 'metadata-backups')).some(file => fs.readFileSync(path.join(root, 'data', 'metadata-backups', file), 'utf8') === original));
  assert.deepEqual(fs.readdirSync(folder), ['metadata.json']);
});
