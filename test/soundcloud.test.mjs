import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { EventEmitter } from 'node:events';
import { PassThrough } from 'node:stream';
import { normalizeLink, extractLinks, resolveSoundcloudLink } from '../lib/links.mjs';
import { Inbox } from '../lib/inbox.mjs';
import { DownloadWorker } from '../lib/downloader.mjs';
import { songPaths } from '../lib/library.mjs';
import { compactMetadata } from '../lib/metadata.mjs';
import { createApp } from '../server.mjs';

const url = 'https://soundcloud.com/artist/song';
const shared = 'https://on.soundcloud.com/AbC123';
const id = '123456789';
const songId = `soundcloud-${id}`;
const info = { id, extractor_key: 'Soundcloud', webpage_url: url, title: 'Song', track: 'Song', uploader: 'Artist',
  thumbnails: [{ url: 'https://i1.sndcdn.com/artwork.jpg' }], duration: 10 };
const traits = { bpm: 120, key: 'Eb minor', duration: 10, version: 2, source: 'mp3', beatOffset: .1, beatConfidence: .8, keyConfidence: .7 };
Object.assign(traits, { tuningHz: 440, tuningVersion: 1, tuningCents: 0, tuningConfidence: .9 });

function temporary(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'stemkit-soundcloud-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  return root;
}

function mockSpawn(metadata = info, onDownload = () => {}) {
  return (_, args) => {
    const child = new EventEmitter(); child.stdout = new PassThrough(); child.stderr = new PassThrough();
    setImmediate(() => {
      if (args.includes('--dump-single-json')) child.stdout.write(JSON.stringify(metadata) + '\n');
      else {
        onDownload(args);
        const work = args[args.indexOf('--paths') + 1];
        fs.writeFileSync(path.join(work, 'audio.mp3'), 'audio');
        fs.writeFileSync(path.join(work, 'audio.jpg'), 'cover');
      }
      child.stdout.end(); child.stderr.end(); setImmediate(() => child.emit('close', 0));
    });
    return child;
  };
}

test('SoundCloud track variants strip tracking, retain access tokens, and deduplicate with YouTube in a batch', () => {
  const expected = normalizeLink(url);
  assert.equal(expected.source, 'soundcloud');
  for (const input of [url, 'http://www.soundcloud.com/Artist/Song/?si=abc&utm_source=clipboard', 'https://m.soundcloud.com/artist/song?in=artist/sets/album#t=20']) {
    assert.deepEqual(normalizeLink(input), expected);
  }
  const privateLink = normalizeLink(`${url}/s-Secret?si=abc`);
  assert.equal(privateLink.id, expected.id);
  assert.equal(privateLink.url, `${url}/s-Secret`);
  assert.deepEqual(normalizeLink(`${url}?secret_token=s-Secret&si=abc`), privateLink);
  assert.deepEqual(normalizeLink(`https://api-v2.soundcloud.com/tracks/soundcloud%3Atracks%3A${id}?secret_token=s-Secret`),
    { id: songId, url: `https://api.soundcloud.com/tracks/${id}?secret_token=s-Secret`, source: 'soundcloud' });
  assert.equal(normalizeLink(shared + '?si=abc').url, shared);
  const batch = extractLinks(`${url}?si=abc\n${url}\nhttps://youtu.be/BaW_jenozKc`);
  assert.equal(batch.links.length, 2); assert.equal(batch.repeated, 1);
});

test('SoundCloud profiles, collections, unsafe hosts, and malformed tracks are rejected', () => {
  for (const input of ['https://soundcloud.com/artist', 'https://soundcloud.com/artist/tracks', 'https://soundcloud.com/artist/sets/album',
    'https://soundcloud.com/artist/likes', 'https://soundcloud.com/stations/track/song', 'https://soundcloud.com/tags/electronic',
    `${url}/recommended`, `${url}/other/path`, 'https://soundcloud.com.evil.test/artist/song', 'https://user@soundcloud.com/artist/song',
    'https://soundcloud.com:8080/artist/song', 'ftp://soundcloud.com/artist/song', 'https://on.soundcloud.com/',
    'https://api.soundcloud.com/tracks/not-a-number', `${url}?secret_token=bad`]) assert.equal(normalizeLink(input), null, input);
});

test('share links follow only supported SoundCloud track redirects and limit redirect loops', async () => {
  let calls = 0;
  const resolved = await resolveSoundcloudLink(normalizeLink(shared), async (request, options) => {
    calls++; assert.equal(request, shared); assert.equal(options.redirect, 'manual');
    return new Response(null, { status: 302, headers: { location: url + '?si=abc' } });
  });
  assert.deepEqual(resolved, normalizeLink(url)); assert.equal(calls, 1);
  for (const location of ['https://example.com/song', 'https://soundcloud.com/artist/sets/album', 'https://soundcloud.com/artist', 'http://127.0.0.1/private']) {
    await assert.rejects(resolveSoundcloudLink(normalizeLink(shared), async () => new Response(null, { status: 302, headers: { location } })), /single track/);
  }
  await assert.rejects(resolveSoundcloudLink(normalizeLink(shared), async () => new Response(null, { status: 200 })), /Could not resolve/);
  calls = 0;
  await assert.rejects(resolveSoundcloudLink(normalizeLink(shared), async () => { calls++; return new Response(null, { status: 302, headers: { location: shared } }); }), /Too many redirects/);
  assert.equal(calls, 5);
});

test('SoundCloud archives and saved aliases reject repeats across restart without affecting YouTube', t => {
  const root = temporary(t), inbox = new Inbox(root);
  assert.deepEqual(inbox.add(`${url}?si=abc\n${url}`), { added: 1, skipped: 1, rejected: 0 });
  inbox.update(inbox.state.jobs[0], { videoId: songId, status: 'failed' });
  const restored = new Inbox(root);
  assert.equal(restored.add(url).skipped, 1);
  assert.equal(restored.add(`https://api.soundcloud.com/tracks/${id}`).skipped, 1);
  fs.writeFileSync(restored.archivePath, 'soundcloud 987654321\nyoutube BaW_jenozKc\nSoundcloud invalid\n');
  assert.equal(restored.add('https://api.soundcloud.com/tracks/987654321').skipped, 1);
  assert.equal(restored.add('https://youtu.be/BaW_jenozKc').skipped, 1);
  assert.deepEqual([...restored.archiveIds()].sort(), ['BaW_jenozKc', 'soundcloud-987654321']);
});

test('a shared SoundCloud track saves tags, artwork, metadata, analysis, and stems through the existing pipeline', async t => {
  const root = temporary(t), inbox = new Inbox(path.join(root, 'data')); inbox.add(shared);
  const phases = [];
  const worker = new DownloadWorker({ inbox, downloadsDir: root, command: 'mock', ffmpeg: true,
    fetchUrl: async () => new Response(null, { status: 302, headers: { location: url + '?si=abc' } }),
    spawnProcess: mockSpawn(info, args => {
      const tagged = JSON.parse(fs.readFileSync(args[args.indexOf('--load-info-json') + 1]));
      assert.equal(tagged.artist, 'Artist'); assert.equal(tagged.track, 'Song');
      phases.push('download');
    }),
    analyzer: { async run(audio) { assert.ok(fs.existsSync(audio)); phases.push('analysis'); return traits; } },
    separator: { async run(audio, folder) {
      assert.ok(inbox.archiveIds().has(songId)); assert.equal(JSON.parse(fs.readFileSync(path.join(folder, 'metadata.json'))).bpm, 120);
      phases.push('stems'); for (const stem of ['drums', 'bass', 'other', 'vocals']) fs.writeFileSync(path.join(folder, `${stem}.wav`), 'stem');
    } } });
  await worker.kick();
  const job = inbox.state.jobs[0];
  assert.equal(job.status, 'completed'); assert.equal(job.videoId, songId); assert.equal(job.url, url);
  assert.deepEqual(phases, ['download', 'analysis', 'stems']);
  assert.equal(job.filename, path.join('mp3', 'Artist', 'Song.mp3'));
  const folder = path.join(root, job.stemsFolder);
  assert.deepEqual(fs.readdirSync(folder).sort(), ['bass.wav', 'cover.jpg', 'drums.wav', 'metadata.json', 'other.wav', 'vocals.wav']);
  const metadata = JSON.parse(fs.readFileSync(path.join(folder, 'metadata.json')));
  assert.equal(metadata.id, songId); assert.equal(metadata.sourceUrl, url); assert.equal(metadata.key, 'Eb minor');
  assert.equal(fs.readFileSync(inbox.archivePath, 'utf8'), `soundcloud ${id}\n`);
  const restored = new Inbox(inbox.dataDir);
  assert.equal(restored.add(shared).skipped, 1); assert.equal(restored.add(url).skipped, 1);
});

test('different SoundCloud links resolving to an archived track never transfer its audio again', async t => {
  const root = temporary(t), inbox = new Inbox(path.join(root, 'data')); inbox.add(url);
  fs.writeFileSync(inbox.archivePath, `soundcloud ${id}\n`);
  let downloads = 0;
  await new DownloadWorker({ inbox, downloadsDir: root, command: 'mock', ffmpeg: true, spawnProcess: mockSpawn(info, () => { downloads++; }) }).kick();
  assert.equal(downloads, 0); assert.equal(inbox.state.jobs[0].status, 'skipped');
});

test('a permalink and numeric link queued together download the track exactly once', async t => {
  const root = temporary(t), inbox = new Inbox(path.join(root, 'data')); inbox.add(`${url}\nhttps://api.soundcloud.com/tracks/${id}`);
  let downloads = 0;
  await new DownloadWorker({ inbox, downloadsDir: root, command: 'mock', ffmpeg: true, spawnProcess: mockSpawn(info, () => { downloads++; }) }).kick();
  assert.equal(downloads, 1);
  assert.deepEqual(inbox.state.jobs.map(job => job.status), ['skipped', 'completed']);
});

test('SoundCloud analysis failures retry the saved MP3 without downloading again', async t => {
  const root = temporary(t), inbox = new Inbox(path.join(root, 'data')); inbox.add(url);
  let downloads = 0, analyses = 0;
  const worker = new DownloadWorker({ inbox, downloadsDir: root, command: 'mock', ffmpeg: true, spawnProcess: mockSpawn(info, () => { downloads++; }),
    analyzer: { async run() { if (++analyses === 1) throw new Error('Analysis interrupted'); return traits; } } });
  await worker.kick(); const job = inbox.state.jobs[0];
  assert.equal(job.status, 'failed'); assert.ok(inbox.archiveIds().has(songId));
  inbox.retry(job.id); await worker.kick();
  assert.equal(job.status, 'completed'); assert.equal(downloads, 1); assert.equal(analyses, 2);
});

test('unexpected source metadata and playlists fail before audio is transferred', async t => {
  for (const metadata of [{ ...info, _type: 'playlist', entries: [] }, { ...info, extractor_key: 'Youtube' },
    { ...info, id: '../escape' }, { ...info, webpage_url: 'https://soundcloud.com/artist/sets/album' }]) {
    const root = temporary(t), inbox = new Inbox(root); inbox.add(url); let downloads = 0;
    await new DownloadWorker({ inbox, downloadsDir: root, command: 'mock', ffmpeg: true, spawnProcess: mockSpawn(metadata, () => { downloads++; }) }).kick();
    assert.equal(inbox.state.jobs[0].status, 'failed'); assert.equal(downloads, 0); assert.equal(inbox.archiveIds().size, 0);
  }
});

test('SoundCloud sources get separate collision paths and never infer a YouTube URL', t => {
  const root = temporary(t), names = { artist: 'Artist', song: 'Song' };
  const youtube = songPaths(root, names, 'BaW_jenozKc'); fs.mkdirSync(youtube.folder, { recursive: true });
  fs.writeFileSync(path.join(youtube.folder, 'metadata.json'), JSON.stringify({ id: 'BaW_jenozKc' }));
  const soundcloud = songPaths(root, names, songId);
  assert.equal(soundcloud.folder, youtube.folder + ` [${songId}]`);
  assert.throws(() => songPaths(root, names, 'soundcloud-link-unresolved'));
  assert.equal(compactMetadata({ id: songId, title: 'Song' }).sourceUrl, `https://api.soundcloud.com/tracks/${id}`);
});

test('the links API accepts a mixed browser drop and advertises SoundCloud support', async t => {
  const root = temporary(t), app = createApp({ dataDir: path.join(root, 'data'), downloadsDir: root,
    downloaderVersion: null, ffmpeg: true, separator: null, analyzer: null });
  await new Promise(resolve => app.server.listen(0, '127.0.0.1', resolve));
  t.after(() => new Promise(resolve => app.server.close(resolve)));
  const endpoint = `http://127.0.0.1:${app.server.address().port}`;
  const result = await fetch(endpoint + '/api/links', { method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ text: `${url}?si=abc\nhttps://youtu.be/BaW_jenozKc` }) });
  assert.equal(result.status, 200); assert.deepEqual(await result.json(), { added: 2, skipped: 0, rejected: 0 });
  assert.equal(await (await fetch(endpoint + '/api/links')).text(), url + '\nhttps://www.youtube.com/watch?v=BaW_jenozKc\n');
  assert.deepEqual((await (await fetch(endpoint + '/api/state')).json()).supportedSources, ['youtube', 'soundcloud']);
});
