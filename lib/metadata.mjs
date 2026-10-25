import fs from 'node:fs';
import { randomUUID } from 'node:crypto';
import { isSongId } from './links.mjs';

export const ANALYSIS_VERSION = 2;
export const TUNING_VERSION = 1;
const keys = new Set(['C', 'C#', 'D', 'Eb', 'E', 'F', 'F#', 'G', 'Ab', 'A', 'Bb', 'B'].flatMap(note => [`${note} major`, `${note} minor`]));
const confidence = value => Number.isFinite(value) && value >= 0 && value <= 1;
const text = value => typeof value === 'string' && value.trim() ? value.trim() : undefined;
const date = value => /^\d{8}$/.test(value || '') ? `${value.slice(0, 4)}-${value.slice(4, 6)}-${value.slice(6)}` : text(value);

export function hasTuningAnalysis(metadata) {
  const analysis = metadata?.analysis;
  if (analysis?.tuningVersion !== TUNING_VERSION || analysis.source !== 'mp3' || !confidence(analysis.tuningConfidence)) return false;
  if (metadata.tuningHz === null) return analysis.tuningConfidence === 0 && analysis.tuningCents === null;
  return Number.isFinite(metadata.tuningHz) && metadata.tuningHz >= 427.47 && metadata.tuningHz <= 452.9
    && Number.isFinite(analysis.tuningCents) && Math.abs(analysis.tuningCents) <= 50
    && Math.abs(1200 * Math.log2(metadata.tuningHz / 440) - analysis.tuningCents) < .01
    && analysis.tuningConfidence > 0;
}

export function tuningFields(result) {
  const fields = { tuningHz: result.tuningHz, analysis: { source: result.source,
    tuningVersion: result.tuningVersion, tuningCents: result.tuningCents, tuningConfidence: result.tuningConfidence } };
  if (!hasTuningAnalysis(fields)) throw new Error('Tuning analysis returned invalid song values.');
  return fields;
}

export function hasMusicAnalysis(metadata) {
  const analysis = metadata?.analysis;
  return analysis?.version === ANALYSIS_VERSION && analysis.source === 'mp3'
    && (metadata.bpm === null || (Number.isFinite(metadata.bpm) && metadata.bpm >= 30 && metadata.bpm <= 300))
    && (metadata.key === null || keys.has(metadata.key))
    && Number.isFinite(analysis.beatOffset) && analysis.beatOffset >= 0
    && (metadata.bpm === null ? analysis.beatOffset === 0 : analysis.beatOffset < 60 / metadata.bpm)
    && confidence(analysis.beatConfidence) && confidence(analysis.keyConfidence);
}

export function analysisFields(result) {
  const fields = { bpm: result.bpm, key: result.key,
    analysis: { version: result.version, source: result.source, beatOffset: result.beatOffset,
      beatConfidence: result.beatConfidence, keyConfidence: result.keyConfidence } };
  if (!hasMusicAnalysis(fields) || !Number.isFinite(result.duration) || result.duration <= 0) throw new Error('Music analysis returned invalid song values.');
  const tuning = tuningFields(result);
  return { duration: result.duration, ...fields, tuningHz: tuning.tuningHz, analysis: { ...fields.analysis, ...tuning.analysis } };
}

// Deliberately whitelist song data: no yt-dlp formats, URLs with tokens, comments,
// uploader statistics, thumbnail lists, or duplicate separation diagnostics.
export function compactMetadata(info, { id, artist, title, sourceUrl, downloadedAt, analysis } = {}) {
  const kit = info.stemkit || {};
  const metadata = {
    id: text(id) || text(info.id),
    artist: text(artist) || text(kit.artist) || text(info.artist) || 'Unknown Artist',
    title: text(title) || text(kit.song) || text(info.track) || text(info.title) || 'Untitled',
    album: text(info.album),
    duration: Number.isFinite(info.duration) && info.duration > 0 ? info.duration : undefined,
    releaseDate: date(info.releaseDate || info.release_date),
    sourceUrl: text(sourceUrl) || text(info.sourceUrl) || text(kit.sourceUrl),
    downloadedAt: text(downloadedAt) || text(info.downloadedAt) || text(kit.downloadedAt),
    bpm: null, key: null,
  };
  // A download job's canonical link takes precedence over temporary stream URLs.
  if (!metadata.sourceUrl && isSongId(metadata.id)) metadata.sourceUrl = metadata.id.startsWith('soundcloud-')
    ? `https://api.soundcloud.com/tracks/${metadata.id.slice('soundcloud-'.length)}` : `https://www.youtube.com/watch?v=${metadata.id}`;
  if (hasMusicAnalysis(info)) Object.assign(metadata, { bpm: info.bpm, key: info.key, analysis: {
    version: info.analysis.version, source: info.analysis.source, beatOffset: info.analysis.beatOffset,
    beatConfidence: info.analysis.beatConfidence, keyConfidence: info.analysis.keyConfidence,
  } });
  if (hasTuningAnalysis(info)) {
    const tuning = tuningFields({ ...info.analysis, tuningHz: info.tuningHz });
    metadata.tuningHz = tuning.tuningHz; metadata.analysis = { ...metadata.analysis, ...tuning.analysis };
  }
  if (analysis) Object.assign(metadata, analysisFields(analysis));
  return Object.fromEntries(Object.entries(metadata).filter(([, value]) => value !== undefined));
}

export function writeMetadata(file, metadata) {
  const temporary = `${file}.${randomUUID()}.tmp`;
  try {
    fs.writeFileSync(temporary, JSON.stringify(metadata, null, 2) + '\n');
    fs.renameSync(temporary, file);
  } finally { fs.rmSync(temporary, { force: true }); }
}
