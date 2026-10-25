import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { libraryRoot } from '../lib/library.mjs';
import { inside } from '../lib/music.mjs';
import { MusicAnalyzer } from '../lib/analysis.mjs';
import { compactMetadata, hasMusicAnalysis, hasTuningAnalysis, writeMetadata } from '../lib/metadata.mjs';

const ROOT = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const hash = content => createHash('sha256').update(content).digest('hex');

export function metadataSongs(root) {
  const mp3 = path.join(root, 'mp3'), stems = path.join(root, 'stems'), songs = [];
  for (const artist of fs.readdirSync(mp3, { withFileTypes: true }).filter(entry => entry.isDirectory())) {
    for (const song of fs.readdirSync(path.join(mp3, artist.name), { withFileTypes: true }).filter(entry => entry.isFile() && /\.mp3$/i.test(entry.name))) {
      const audio = path.join(mp3, artist.name, song.name), file = path.join(stems, artist.name, song.name.slice(0, -4), 'metadata.json');
      if (!inside(mp3, audio) || !inside(stems, file)) throw new Error('Invalid library song path.');
      if (!fs.existsSync(file)) continue;
      const original = fs.readFileSync(file);
      songs.push({ audio, file, original, metadata: JSON.parse(original.toString('utf8')) });
    }
  }
  return songs;
}

export async function updateSongMetadata({ root = ROOT, library = libraryRoot(root), apply = false, analyzer = new MusicAnalyzer({ root }), log = console.log } = {}) {
  const queueFile = path.join(root, 'data', 'queue.json');
  const originalQueue = fs.existsSync(queueFile) ? fs.readFileSync(queueFile) : null;
  const queue = originalQueue ? JSON.parse(originalQueue.toString('utf8')) : null;
  if (queue?.jobs.some(job => ['downloading', 'analyzing', 'separating'].includes(job.status))) throw new Error('Wait for active processing to finish before updating existing metadata.');
  const songs = metadataSongs(library);
  for (const song of songs) {
    if (!apply) { log(`Would analyze and simplify: ${song.audio}`); continue; }
    log(`Analyzing MP3: ${song.audio}`);
    const analysis = hasMusicAnalysis(song.metadata) && hasTuningAnalysis(song.metadata) ? null : await analyzer.run(song.audio);
    song.updated = compactMetadata(song.metadata, { analysis });
  }
  if (!apply) return songs.length;
  // Prepare every result before committing. Preserve old JSON outside the music tree.
  for (const song of songs) if (hash(fs.readFileSync(song.file)) !== hash(song.original)) throw new Error('Song metadata changed during analysis; rerun the update.');
  if (originalQueue && hash(fs.readFileSync(queueFile)) !== hash(originalQueue)) throw new Error('The queue changed during analysis; rerun when it is idle.');
  const backup = path.join(root, 'data', 'metadata-backups'); fs.mkdirSync(backup, { recursive: true });
  for (const song of songs) {
    const backupFile = path.join(backup, hash(song.original) + '.json');
    if (!fs.existsSync(backupFile)) fs.writeFileSync(backupFile, song.original, { flag: 'wx' });
    writeMetadata(song.file, song.updated);
    const job = queue?.jobs.find(item => item.videoId === song.updated.id && item.filename && path.resolve(library, item.filename) === path.resolve(song.audio));
    if (job) job.analysisStatus = 'completed';
    log(`Saved: ${song.updated.artist} — ${song.updated.title}: ${song.updated.bpm?.toFixed(2) || 'unknown'} BPM, ${song.updated.key || 'unknown key'}`);
  }
  if (queue) writeMetadata(queueFile, queue);
  return songs.length;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try { console.log(`Processed ${await updateSongMetadata({ apply: process.argv.includes('--apply') })} songs.`); }
  catch (error) { console.error(error.message); process.exitCode = 1; }
}
