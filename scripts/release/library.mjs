import fs from 'node:fs';
import path from 'node:path';
import { atomicJson, relocate } from '../portable/state.mjs';

export function initializeLibrary(appRoot, selected) {
  const library = path.resolve(selected);
  const markerFile = path.join(library, 'djstem-library.json');
  let marker = {};
  if (fs.existsSync(markerFile)) {
    marker = JSON.parse(fs.readFileSync(markerFile, 'utf8'));
    if (marker.format !== 'djstem-library' || marker.version !== 1) throw new Error('This library format is not supported by this version of DJStem.');
  }
  for (const name of ['mp3', 'stems', 'data', 'matched-stems']) fs.mkdirSync(path.join(library, name), { recursive: true });
  const defaults = JSON.parse(fs.readFileSync(path.join(appRoot, 'defaults', 'settings.json'), 'utf8'));
  const settingsFile = path.join(library, 'settings.json');
  const settings = fs.existsSync(settingsFile) ? JSON.parse(fs.readFileSync(settingsFile, 'utf8')) : defaults;
  const oldRoots = [...new Set([...(marker.locations || []), marker.location, settings.libraryRoot].filter(value => value && path.isAbsolute(value)))];
  const mappings = oldRoots.filter(value => path.resolve(value).toLowerCase() !== library.toLowerCase()).map(value => [value, library]).sort((a,b) => b[0].length - a[0].length);
  for (const name of ['player-settings.json', 'queue.json']) {
    const file = path.join(library, 'data', name);
    if (!fs.existsSync(file)) continue;
    const before = JSON.parse(fs.readFileSync(file, 'utf8'));
    const after = relocate(before, mappings);
    if (JSON.stringify(before) !== JSON.stringify(after)) {
      fs.copyFileSync(file, file + '.before-library-move');
      atomicJson(file, after);
    }
  }
  // Roots are relative to this folder; a copied library works on another PC.
  settings.libraryRoot = '.';
  settings.matchedStemsRoot = 'matched-stems';
  if (!fs.existsSync(settingsFile) || JSON.stringify(settings) !== JSON.stringify(JSON.parse(fs.readFileSync(settingsFile, 'utf8')))) atomicJson(settingsFile, settings);
  const preferencesFile = path.join(library, 'data', 'player-settings.json');
  if (!fs.existsSync(preferencesFile)) fs.copyFileSync(path.join(appRoot, 'defaults', 'player-settings.json'), preferencesFile);
  atomicJson(markerFile, { format: 'djstem-library', version: 1, location: library, locations: [...new Set([...oldRoots, library])] });
  return library;
}

export function configureRelease(appRoot, library, edition) {
  const tools = path.join(appRoot, 'tools'), python = path.join(tools, 'stem-runtime');
  const data = path.join(library, 'data');
  for (const directory of ['temp', 'numba-cache', 'node-cache']) fs.mkdirSync(path.join(data, directory), { recursive: true });
  for (const key of ['PYTHONHOME', 'PYTHONPATH', 'VIRTUAL_ENV', 'NODE_OPTIONS', 'NODE_PATH', 'STEMKIT_DEVICE', 'STEMKIT_BASE_PYTHON', 'STEMKIT_RUBBERBAND_DIR']) delete process.env[key];
  Object.assign(process.env, {
    PATH: [tools, python, path.join(python, 'DLLs'), path.join(process.env.SystemRoot || 'C:\\Windows', 'System32')].join(path.delimiter),
    STEMKIT_RELEASE_ROOT: appRoot, STEMKIT_USER_DATA: library, STEMKIT_LIBRARY: library, STEMKIT_EDITION: edition,
    STEMKIT_NODE: path.join(tools, 'node.exe'), STEMKIT_PYTHON: path.join(python, 'python.exe'),
    STEMKIT_FFMPEG: path.join(tools, 'ffmpeg.exe'), STEMKIT_FFPROBE: path.join(tools, 'ffprobe.exe'), YTDLP_PATH: path.join(tools, 'yt-dlp.exe'),
    PORT: '4320', PYTHONUTF8: '1', PYTHONNOUSERSITE: '1', PYTHONDONTWRITEBYTECODE: '1',
    NUMBA_CACHE_DIR: path.join(data, 'numba-cache'), NODE_COMPILE_CACHE: path.join(data, 'node-cache'),
    MPLCONFIGDIR: path.join(data, 'matplotlib'), TORCH_HOME: path.join(data, 'torch'), XDG_CACHE_HOME: path.join(data, 'cache'),
    TEMP: path.join(data, 'temp'), TMP: path.join(data, 'temp'),
  });
  process.chdir(appRoot);
}
