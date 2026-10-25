import fs from 'node:fs';
import path from 'node:path';
import { inside, safeName } from './music.mjs';
import { isSongId } from './links.mjs';
import { artistName } from './artists.mjs';
import { settingsPath, userRoot } from './paths.mjs';

export function libraryRoot(root) {
  const config = settingsPath(root);
  const settings = fs.existsSync(config) ? JSON.parse(fs.readFileSync(config, 'utf8')) : {};
  return path.resolve(userRoot(root), process.env.STEMKIT_LIBRARY || settings.libraryRoot || path.join(root, 'downloads'));
}

export function songPaths(root, names, videoId, reserved = new Set()) {
  if (!isSongId(videoId)) throw new Error('Invalid source ID for the song library.');
  const artist = artistName(root, names.artist);
  const song = safeName(names.song);
  for (const basename of [song, `${song} [${videoId}]`]) {
    const folder = path.join(root, 'stems', artist, basename);
    const audio = path.join(root, 'mp3', artist, basename + '.mp3');
    if (reserved.has(folder) || reserved.has(audio)) continue;
    if (!inside(root, folder) || !inside(root, audio)) throw new Error('Invalid song library path.');
    let owned = false;
    try { owned = JSON.parse(fs.readFileSync(path.join(folder, 'metadata.json'), 'utf8')).id === videoId; } catch { }
    const unusedFolder = !fs.existsSync(folder) || (fs.statSync(folder).isDirectory() && fs.readdirSync(folder).length === 0);
    if (owned || (unusedFolder && !fs.existsSync(audio))) return { audio, folder };
  }
  throw new Error('A different file already occupies this song name. Move it and retry.');
}

export function savedSongPaths(root, job) {
  const audio = path.resolve(root, job.filename || '');
  const folder = path.resolve(root, job.stemsFolder || '');
  if (!job.filename || !job.stemsFolder || !inside(path.join(root, 'mp3'), audio) || !inside(path.join(root, 'stems'), folder) || path.extname(audio).toLowerCase() !== '.mp3') {
    throw new Error('Invalid saved song paths. Migrate the old library before processing it.');
  }
  return { audio, folder };
}
