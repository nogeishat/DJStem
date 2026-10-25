import fs from 'node:fs';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
import { extractLinks } from './links.mjs';

export class Inbox {
  constructor(dataDir, { enableStems = false, enableAnalysis = false } = {}) {
    this.dataDir = dataDir;
    fs.mkdirSync(dataDir, { recursive: true });
    this.statePath = path.join(dataDir, 'queue.json');
    this.archivePath = path.join(dataDir, 'mp3-download-archive.txt');
    this.state = { version: 1, paused: false, jobs: [] };
    if (fs.existsSync(this.statePath)) {
      this.state = JSON.parse(fs.readFileSync(this.statePath, 'utf8'));
      if (this.state.version !== 1 || !Array.isArray(this.state.jobs)) throw new Error('Unsupported queue file. Keep data/queue.json and restore a valid backup.');
    }
    for (const job of this.state.jobs) {
      if (job.outputType !== 'mp3') {
        if (job.status === 'completed' || job.status === 'skipped') {
          job.status = 'queued'; job.filename = null; job.completedAt = null; job.progress = 0;
        }
        job.outputType = 'mp3';
      }
      if (['downloading', 'separating', 'analyzing'].includes(job.status)) { job.status = 'queued'; job.message = 'Resuming after restart'; }
      if (enableStems && job.status === 'completed' && job.filename && job.stemStatus !== 'completed') {
        job.status = 'queued'; job.stemStatus = 'queued'; job.progress = 0; job.message = 'Waiting to separate saved MP3';
      }
      if (enableAnalysis && job.status === 'completed' && job.filename && job.analysisStatus !== 'completed') {
        job.status = 'queued'; job.analysisStatus = 'queued'; job.progress = 0; job.message = 'Waiting to analyze saved MP3';
      }
    }
    this.save();
  }

  archiveIds() {
    if (!fs.existsSync(this.archivePath)) return new Set();
    return new Set(fs.readFileSync(this.archivePath, 'utf8').split(/\r?\n/)
      .map(line => {
        const match = /^(youtube|soundcloud)\s+([A-Za-z0-9_-]+)$/i.exec(line.trim());
        if (!match) return null;
        if (match[1].toLowerCase() === 'soundcloud') return /^\d{1,20}$/.test(match[2]) ? `soundcloud-${match[2]}` : null;
        return /^[A-Za-z0-9_-]{11}$/.test(match[2]) ? match[2] : null;
      }).filter(Boolean));
  }

  save() {
    const temporary = `${this.statePath}.tmp`;
    fs.writeFileSync(temporary, JSON.stringify(this.state, null, 2) + '\n');
    fs.renameSync(temporary, this.statePath);
    const linksPath = path.join(this.dataDir, 'links.txt');
    fs.writeFileSync(`${linksPath}.tmp`, this.state.jobs.map(job => job.url).join('\n') + (this.state.jobs.length ? '\n' : ''));
    fs.renameSync(`${linksPath}.tmp`, linksPath);
  }

  add(text) {
    const parsed = extractLinks(text);
    if (!parsed.links.length) throw new Error('No YouTube or SoundCloud track links found. Drop a linked title, artwork, or browser address, or paste a track link.');
    const known = new Set(this.state.jobs.flatMap(job => [job.videoId, ...(job.linkIds || [])]));
    const archive = this.archiveIds();
    const added = [];
    let skipped = parsed.repeated;
    for (const link of parsed.links) {
      if (known.has(link.id) || archive.has(link.id)) { skipped++; continue; }
      const job = { id: randomUUID(), videoId: link.id, url: link.url, title: '', outputType: 'mp3', status: 'queued', progress: 0, message: 'Waiting to download', createdAt: new Date().toISOString(), completedAt: null, filename: null };
      if (link.source === 'soundcloud') Object.assign(job, { source: 'soundcloud', linkIds: [link.id] });
      this.state.jobs.push(job);
      added.push(job);
      known.add(link.id);
    }
    this.save();
    return { added: added.length, skipped, rejected: parsed.rejected.length };
  }

  update(job, changes, persist = true) {
    Object.assign(job, changes);
    if (persist) this.save();
  }

  retry(id) {
    const job = this.state.jobs.find(job => job.id === id);
    if (!job || job.status !== 'failed') throw new Error('Only failed jobs can be retried.');
    this.update(job, { status: 'queued', progress: 0, message: 'Waiting to retry', stemStatus: job.filename ? 'queued' : job.stemStatus, filename: job.filename || null });
  }
}
