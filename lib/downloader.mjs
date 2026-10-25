import { spawn, spawnSync } from 'node:child_process';
import path from 'node:path';
import fs from 'node:fs';
import readline from 'node:readline';
import { inside, musicNames } from './music.mjs';
import { songPaths, savedSongPaths } from './library.mjs';
import { artistName } from './artists.mjs';
import { compactMetadata, hasMusicAnalysis, hasTuningAnalysis, tuningFields, writeMetadata } from './metadata.mjs';
import { normalizeLink, resolveSoundcloudLink, soundcloudTrackId } from './links.mjs';

export function findDownloader(root) {
  if (process.env.YTDLP_PATH) return process.env.YTDLP_PATH;
  const bundled = path.join(root, 'tools', process.platform === 'win32' ? 'yt-dlp.exe' : 'yt-dlp');
  return fs.existsSync(bundled) ? bundled : 'yt-dlp';
}

export function toolVersion(command, args = ['--version']) {
  const result = spawnSync(command, args, { encoding: 'utf8', windowsHide: true, timeout: 15000 });
  return result.status === 0 ? result.stdout.trim().split(/\r?\n/)[0] : null;
}

const commonArgs = () => [
  '--ignore-config', '--no-playlist', '--no-colors', '--js-runtimes', `node:${process.execPath}`,
  '--format', 'bestaudio', '--socket-timeout', '30', '--retries', '3', '--fragment-retries', '3',
];

export function metadataArgs(job) {
  return [...commonArgs(), '--skip-download', '--dump-single-json', '--', job.url];
}

export function downloadArgs(infoPath, workDir) {
  return [...commonArgs(), '--load-info-json', infoPath, '--paths', workDir, '--output', 'audio.%(ext)s',
    '--windows-filenames', '--no-overwrites', '--newline', '--extract-audio', '--audio-format', 'mp3',
    '--audio-quality', '0', '--embed-metadata', '--embed-thumbnail', '--write-thumbnail',
    '--convert-thumbnails', 'jpg', '--write-info-json', '--no-write-playlist-metafiles',
    '--print', 'after_move:STEMKIT_FILE:%(filepath)j', '--no-simulate', '--progress',
    '--progress-delta', '1', '--progress-template', 'download:STEMKIT_PROGRESS:%(progress)j',
  ];
}

export class DownloadWorker {
  constructor({ inbox, downloadsDir, command, ffmpeg, separator = null, analyzer = null, spawnProcess = spawn, fetchUrl = fetch }) {
    Object.assign(this, { inbox, downloadsDir, command, ffmpeg, separator, analyzer, spawnProcess, fetchUrl });
    this.child = null; this.running = false; this.stopping = false;
  }

  async kick() {
    if (this.running || this.stopping || this.inbox.state.paused) return;
    this.running = true;
    try {
      let job;
      while (!this.stopping && !this.inbox.state.paused && (job = this.inbox.state.jobs.find(job => job.status === 'queued'))) {
        try { await this.run(job); }
        catch (error) {
          this.inbox.update(job, this.stopping ? { status: 'queued', message: 'Will resume when StemKit starts' } : {
            status: 'failed', message: error.message,
            ...(job.status === 'analyzing' ? { analysisStatus: 'failed' } : {}),
            ...(job.status === 'separating' ? { stemStatus: 'failed' } : {}),
          });
        }
      }
    } finally { this.running = false; }
  }

  process(args, onLine = () => {}) {
    return new Promise((resolve, reject) => {
      const child = this.spawnProcess(this.command, args, { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'], shell: false });
      this.child = child;
      const errors = []; const lines = [];
      let spawnError = false;
      const output = readline.createInterface({ input: child.stdout });
      const diagnostics = readline.createInterface({ input: child.stderr });
      output.on('line', line => { lines.push(line); onLine(line); });
      diagnostics.on('line', line => { errors.push(line); if (errors.length > 12) errors.shift(); });
      child.once('error', error => {
        spawnError = true; this.child = null;
        reject(new Error(error.code === 'ENOENT' ? 'yt-dlp was not found. Run Start-StemKit.cmd to install it, then retry.' : error.message));
      });
      child.once('close', code => {
        output.close(); diagnostics.close(); this.child = null;
        if (spawnError) return;
        if (this.stopping) return reject(new Error('Download stopped.'));
        if (code !== 0) return reject(new Error(errors.filter(line => /ERROR/i.test(line)).slice(-1)[0] || errors.slice(-3).join(' ').slice(0, 1000) || `yt-dlp exited with code ${code ?? 'unknown'}.`));
        resolve(lines.join('\n'));
      });
    });
  }

  async run(job) {
    if (job.filename) {
      const { audio, folder } = savedSongPaths(this.downloadsDir, job);
      if (!fs.existsSync(audio)) throw new Error('The saved MP3 was moved or deleted. Restore it to the MP3 artist folder, then retry processing.');
      await this.analyze(job, audio, folder);
      await this.separate(job, audio, folder);
      return;
    }
    if (this.inbox.archiveIds().has(job.videoId)) {
      this.inbox.update(job, { status: 'skipped', message: 'MP3 already downloaded', completedAt: new Date().toISOString() });
      return;
    }
    if (!this.ffmpeg) throw new Error('FFmpeg is required for MP3 conversion and cover art. Install FFmpeg on PATH, restart StemKit, and retry.');
    this.inbox.update(job, { status: 'downloading', progress: 0, message: 'Reading artist and song metadata…' });
    const soundcloud = job.source === 'soundcloud';
    if (soundcloud) {
      const link = await resolveSoundcloudLink(normalizeLink(job.url), this.fetchUrl);
      if (this.stopping) throw new Error('Download stopped.');
      this.inbox.update(job, { url: link.url, linkIds: [...new Set([...(job.linkIds || []), link.id])] });
    }
    const raw = await this.process(metadataArgs(job));
    const info = JSON.parse(raw);
    if (info._type === 'playlist' || info.entries) throw new Error('Drop a single YouTube video or SoundCloud track, rather than a playlist.');
    if (soundcloud) {
      const trackId = soundcloudTrackId(info.id);
      const canonical = normalizeLink(info.webpage_url || job.url);
      if (!trackId || (info.extractor_key !== 'Soundcloud' && info.extractor !== 'soundcloud') || canonical?.source !== 'soundcloud'
        || new URL(canonical.url).hostname === 'on.soundcloud.com'
        || (job.videoId.match(/^soundcloud-\d+$/) && job.videoId !== trackId)) throw new Error('SoundCloud returned unexpected track metadata.');
      const duplicate = this.inbox.archiveIds().has(trackId) || this.inbox.state.jobs.some(other => other !== job && other.status !== 'skipped' && other.videoId === trackId);
      this.inbox.update(job, { videoId: trackId, linkIds: [...new Set([...job.linkIds, canonical.id])] });
      if (duplicate) {
        this.inbox.update(job, { status: 'skipped', message: 'SoundCloud track already added or downloaded', completedAt: new Date().toISOString() });
        return;
      }
      // The public permalink identifies the source without retaining private access tokens in library metadata.
      this.inbox.update(job, { sourceUrl: canonical.url.replace(/\/s-[A-Za-z0-9_-]+$/, '').replace(/\?secret_token=.*$/, '') });
    } else if (info.id !== job.videoId) throw new Error('YouTube returned unexpected video metadata.');
    if (!info.thumbnail && !info.thumbnails?.length) throw new Error('This track has no cover image available.');
    const names = musicNames(info);
    const workBase = path.join(this.inbox.dataDir, 'work');
    const workDir = path.join(workBase, job.videoId);
    if (!inside(workBase, workDir)) throw new Error('Invalid download working directory.');
    fs.mkdirSync(workDir, { recursive: true });
    const infoPath = path.join(workDir, 'source.info.json');
    names.artist = artistName(this.downloadsDir, names.artist);
    const taggedInfo = { ...info, artist: names.artist, track: names.song, title: names.song };
    fs.writeFileSync(infoPath, JSON.stringify(taggedInfo));
    this.inbox.update(job, { artist: names.artist, title: names.song, message: 'Downloading audio…' });
    await this.process(downloadArgs(infoPath, workDir), line => {
      if (!line.startsWith('STEMKIT_PROGRESS:')) return;
      try {
        const progress = JSON.parse(line.slice('STEMKIT_PROGRESS:'.length));
        const total = progress.total_bytes || progress.total_bytes_estimate;
        this.inbox.update(job, { progress: total ? Math.min(100, Math.round(progress.downloaded_bytes / total * 100)) : 0, message: progress.status === 'finished' ? 'Adding MP3 tags and cover…' : 'Downloading audio…' }, false);
      } catch { /* Ignore incomplete progress samples. */ }
    });
    const audio = path.join(workDir, 'audio.mp3');
    const cover = path.join(workDir, 'audio.jpg');
    for (const file of [audio, cover]) {
      if (!fs.existsSync(file) || fs.statSync(file).size === 0) throw new Error('The MP3 or cover image was not produced. Right-click the box to retry.');
    }
    const { audio: filename, folder: directory } = songPaths(this.downloadsDir, names, job.videoId);
    fs.mkdirSync(directory, { recursive: true });
    fs.mkdirSync(path.dirname(filename), { recursive: true });
    const metadata = compactMetadata(info, { id: job.videoId, artist: names.artist, title: names.song, sourceUrl: job.sourceUrl || job.url, downloadedAt: new Date().toISOString() });
    writeMetadata(path.join(directory, 'metadata.json'), metadata);
    fs.copyFileSync(audio, filename);
    fs.copyFileSync(cover, path.join(directory, 'cover.jpg'));
    // Commit duplicate protection only after the complete three-file bundle exists.
    if (!this.inbox.archiveIds().has(job.videoId)) fs.appendFileSync(this.inbox.archivePath, soundcloud ? `soundcloud ${info.id}\n` : `youtube ${job.videoId}\n`);
    this.inbox.update(job, { status: 'completed', progress: 100, filename: path.relative(this.downloadsDir, filename), stemsFolder: path.relative(this.downloadsDir, directory), libraryLayout: 2, message: 'MP3, metadata and cover saved', completedAt: new Date().toISOString() });
    fs.rmSync(workDir, { recursive: true, force: true });
    await this.analyze(job, filename, directory);
    await this.separate(job, filename, directory);
  }

  async analyze(job, audio, folder) {
    if (!this.analyzer) return;
    const file = path.join(folder, 'metadata.json');
    const metadata = JSON.parse(fs.readFileSync(file, 'utf8'));
    if (metadata.id !== job.videoId) throw new Error('Saved metadata belongs to a different song.');
    const musicReady = hasMusicAnalysis(metadata), tuningReady = hasTuningAnalysis(metadata);
    if (musicReady && tuningReady && job.analysisStatus === 'completed') return;
    this.inbox.update(job, { status: 'analyzing', analysisStatus: 'processing', progress: 0, message: 'Analyzing MP3 BPM, key and tuning…' });
    const result = musicReady && tuningReady ? null : await this.analyzer.run(audio, { tuningOnly: musicReady });
    if (this.stopping) throw new Error('Music analysis stopped.');
    if (result && musicReady) {
      const tuning = tuningFields(result); metadata.tuningHz = tuning.tuningHz; metadata.analysis = { ...metadata.analysis, ...tuning.analysis };
    }
    writeMetadata(file, compactMetadata(metadata, { analysis: musicReady ? null : result, artist: job.artist, title: job.title, sourceUrl: job.sourceUrl || job.url }));
    this.inbox.update(job, { analysisStatus: 'completed', status: 'completed', progress: 100, message: 'MP3 BPM, key and tuning saved' });
  }

  async separate(job, audio, folder) {
    if (!this.separator) { this.inbox.update(job, { status: 'completed', progress: 100 }); return; }
    this.inbox.update(job, { status: 'separating', stemStatus: 'processing', progress: 0, message: 'Separating four stems…' });
    await this.separator.run(audio, folder, update => {
      this.inbox.update(job, { progress: Math.max(0, Math.min(100, Number(update.progress) || 0)), message: update.message || 'Separating stems…' }, false);
    });
    // Technical/model details already live in separation.json; song metadata stays small.
    this.inbox.update(job, { status: 'completed', stemStatus: 'completed', progress: 100, stemsFolder: path.relative(this.downloadsDir, folder), message: 'MP3 and four stems saved', completedAt: new Date().toISOString() });
  }

  stop() {
    this.stopping = true;
    this.separator?.stop();
    this.analyzer?.stop();
    if (!this.child) return;
    if (process.platform === 'win32' && this.child.pid) spawnSync('taskkill.exe', ['/PID', String(this.child.pid), '/T', '/F'], { windowsHide: true, stdio: 'ignore' });
    else this.child.kill();
  }
}
