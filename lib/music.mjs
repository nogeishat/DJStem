import fs from 'node:fs';
import path from 'node:path';

export function safeName(value, fallback = 'Untitled', limit = 60) {
  let name = Array.from(String(value || fallback).normalize('NFC').replace(/[<>:"/\\|?*\x00-\x1f]/g, '_').replace(/\s+/g, ' ').trim()).slice(0, limit).join('').replace(/[. ]+$/, '');
  if (!name || name === '.' || name === '..') name = fallback;
  if (/^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)/i.test(name)) name = '_' + name;
  return name;
}

export function musicNames(info) {
  let artist = info.artist || info.artists?.join(', ') || '';
  let song = info.track || '';
  const title = String(info.title || info.id || 'Untitled');
  const split = /^(.+?)\s+[-–—]\s+(.+)$/.exec(title);
  if (!artist && !song && split) { artist = split[1]; song = split[2]; }
  artist ||= String(info.uploader || info.channel || 'Unknown Artist').replace(/\s*-\s*Topic$/i, '');
  song ||= title;
  if (!info.track) song = song.replace(/\s*[([](?:official\s*(?:music\s*)?(?:video|audio|visuali[sz]er)|lyrics?(?:\s*video)?)[)\]]\s*$/i, '').trim() || title;
  return { artist: String(artist), song: String(song) };
}

export function inside(base, file) {
  const relative = path.relative(path.resolve(base), path.resolve(file));
  return relative !== '' && relative !== '..' && !relative.startsWith('..' + path.sep) && !path.isAbsolute(relative);
}

export function songDirectory(downloadsDir, names, videoId) {
  const artistDir = path.join(downloadsDir, safeName(names.artist, 'Unknown Artist'));
  const base = path.join(artistDir, safeName(names.song));
  const belongsToVideo = directory => {
    if (!fs.existsSync(directory)) return true;
    try { return JSON.parse(fs.readFileSync(path.join(directory, 'metadata.json'), 'utf8')).id === videoId; }
    catch { return fs.statSync(directory).isDirectory() && fs.readdirSync(directory).length === 0; }
  };
  if (belongsToVideo(base)) return base;
  const alternate = `${base} [${videoId}]`;
  if (!belongsToVideo(alternate)) throw new Error('A different file already occupies this song folder. Move it and retry.');
  return alternate;
}
