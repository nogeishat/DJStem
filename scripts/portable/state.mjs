import fs from 'node:fs';
import path from 'node:path';
import { randomUUID } from 'node:crypto';

// Saved mixes contain paths in both values and dictionary keys (KeyShifts).
// Match whole path prefixes, never song titles, URLs, or partial folder names.
export function relocate(value, mappings) {
  if (typeof value === 'string') {
    for (const [from, to] of mappings) {
      const prefix = from.replaceAll('/', '\\').replace(/\\+$/, '');
      const candidate = value.replaceAll('/', '\\');
      if (candidate.toLowerCase() === prefix.toLowerCase() || candidate.toLowerCase().startsWith(prefix.toLowerCase() + '\\')) {
        return to + candidate.slice(prefix.length);
      }
    }
    return value;
  }
  if (Array.isArray(value)) return value.map(item => relocate(item, mappings));
  if (value && typeof value === 'object') return Object.fromEntries(Object.entries(value).map(([key, item]) => [relocate(key, mappings), relocate(item, mappings)]));
  return value;
}

export function atomicJson(file, value) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  const temporary = `${file}.${randomUUID()}.tmp`;
  try {
    fs.writeFileSync(temporary, JSON.stringify(value, null, 2) + '\n');
    fs.renameSync(temporary, file);
  } finally { if (fs.existsSync(temporary)) fs.unlinkSync(temporary); }
}

export function relocateState(root) {
  const markerFile = path.join(root, 'data', 'portable-location.json');
  const marker = fs.existsSync(markerFile) ? JSON.parse(fs.readFileSync(markerFile, 'utf8')) : {};
  const library = path.join(root, 'library');
  const oldRoots = marker.libraryRoots || [];
  const mappings = oldRoots.filter(old => old.toLowerCase() !== library.toLowerCase()).map(old => [old, library]);
  if (marker.projectRoot && marker.projectRoot.toLowerCase() !== root.toLowerCase()) mappings.push([marker.projectRoot, root]);
  mappings.sort((a, b) => b[0].length - a[0].length);
  for (const name of ['player-settings.json', 'queue.json']) {
    const file = path.join(root, 'data', name);
    if (!fs.existsSync(file) || !mappings.length) continue;
    const before = fs.readFileSync(file, 'utf8');
    const next = relocate(JSON.parse(before), mappings);
    if (JSON.stringify(next) === JSON.stringify(JSON.parse(before))) continue;
    // Keep the first pre-move snapshot; moving the drive again must not erase it.
    const backup = file + '.before-portable-move';
    if (!fs.existsSync(backup)) fs.copyFileSync(file, backup);
    atomicJson(file, next);
  }
  // Retain historical roots so an interrupted relocation can be safely retried.
  if (marker.projectRoot !== root || !oldRoots.includes(library)) atomicJson(markerFile, { projectRoot: root, libraryRoots: [...new Set([...oldRoots, library])] });
  return library;
}

export function configureEnvironment(root) {
  process.chdir(root);
  const tools = path.join(root, 'tools');
  const python = path.join(tools, 'stem-runtime');
  const temp = path.join(root, 'data', 'temp');
  for (const directory of [temp, path.join(root, 'library', 'mp3'), path.join(root, 'library', 'stems')]) fs.mkdirSync(directory, { recursive: true });
  // Ignore host Python/Node customizations and keep runtime caches on the USB.
  for (const key of ['PYTHONHOME', 'PYTHONPATH', 'VIRTUAL_ENV', 'NODE_OPTIONS', 'NODE_PATH', 'STEMKIT_BASE_PYTHON', 'STEMKIT_RUBBERBAND_DIR']) delete process.env[key];
  Object.assign(process.env, {
    PATH: [tools, python, path.join(python, 'DLLs'), process.env.PATH || ''].join(path.delimiter),
    STEMKIT_PORTABLE_ROOT: root,
    STEMKIT_NODE: path.join(tools, 'node.exe'),
    STEMKIT_PYTHON: path.join(python, 'python.exe'),
    STEMKIT_FFMPEG: path.join(tools, 'ffmpeg.exe'),
    STEMKIT_FFPROBE: path.join(tools, 'ffprobe.exe'),
    STEMKIT_LIBRARY: path.join(root, 'library'),
    YTDLP_PATH: path.join(tools, 'yt-dlp.exe'),
    PORT: '4318',
    PYTHONUTF8: '1', PYTHONNOUSERSITE: '1', PYTHONDONTWRITEBYTECODE: '1',
    NUMBA_CACHE_DIR: path.join(tools, 'numba-cache'),
    NODE_COMPILE_CACHE: path.join(tools, 'node-compile-cache'),
    MPLCONFIGDIR: path.join(root, 'data', 'matplotlib'),
    TORCH_HOME: path.join(root, 'data', 'torch'),
    XDG_CACHE_HOME: path.join(root, 'data', 'cache'),
    TEMP: temp, TMP: temp,
  });
}
