import fs from 'node:fs';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
import { inside, safeName } from './music.mjs';

const key = name => name.normalize('NFC').toLowerCase();
const configPath = root => path.join(root, '.artist-folders.json');

export function artistSettings(root) {
  if (!fs.existsSync(configPath(root))) return { version: 1, aliases: Object.create(null), covers: Object.create(null) };
  const data = JSON.parse(fs.readFileSync(configPath(root), 'utf8'));
  if (data.version !== 1 || !data.aliases || !data.covers) throw new Error('Unsupported artist folder settings.');
  data.aliases = Object.assign(Object.create(null), data.aliases);
  data.covers = Object.assign(Object.create(null), data.covers);
  return data;
}

export function artistName(root, name) {
  const normalized = safeName(name, 'Unknown Artist');
  return folderName(artistSettings(root).aliases[key(normalized)] || normalized);
}

function folderName(name, renamed = false) {
  if (typeof name !== 'string' || !name.trim() || /[<>:"/\\|?*\x00-\x1f]/.test(name) || /[. ]$/.test(name) || name === '.' || name === '..' || /^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)/i.test(name)) throw new Error('Enter a valid artist folder name without Windows filename characters.');
  if (renamed && safeName(name) !== name) throw new Error('Use a folder name of up to 60 characters without extra spaces.');
  return name;
}

function localPath(root, file) {
  if (!inside(root, file)) throw new Error('Invalid artist library path.');
  for (let current = file; current !== path.resolve(root); current = path.dirname(current)) {
    if (fs.existsSync(current) && fs.lstatSync(current).isSymbolicLink()) throw new Error('Artist folders cannot contain links to other locations.');
  }
  return file;
}

function checkTree(root, file) {
  localPath(root, file);
  if (fs.lstatSync(file).isDirectory()) for (const item of fs.readdirSync(file)) checkTree(root, path.join(file, item));
}

function atomicWrite(file, bytes) {
  const temporary = file + '.' + randomUUID() + '.tmp';
  try { fs.writeFileSync(temporary, bytes); fs.renameSync(temporary, file); }
  finally { if (fs.existsSync(temporary)) fs.unlinkSync(temporary); }
}

function directories(root, tree) {
  const base = path.join(root, tree);
  if (!fs.existsSync(base)) return [];
  localPath(root, base);
  return fs.readdirSync(base, { withFileTypes: true }).filter(item => item.isDirectory() || item.isSymbolicLink()).map(item => item.name);
}

// MP3 and stem folders move as a pair. Existing songs never get overwritten.
export function renameArtist({ root, from, to, state, saveState = () => {} }) {
  root = path.resolve(root); folderName(from); folderName(to, true);
  localPath(root, configPath(root));
  const names = [...new Set([...directories(root, 'mp3'), ...directories(root, 'stems')])];
  if (!names.some(name => key(name) === key(from))) throw new Error('This artist folder no longer exists. Refresh the library.');
  const existing = names.find(name => key(name) === key(to) && key(name) !== key(from));
  const target = existing || to;
  const sources = names.filter(name => (key(name) === key(from) || key(name) === key(to)) && name !== target);
  const settings = artistSettings(root), moves = [], mappings = [], metadata = [], reserved = new Set();
  const oldState = state ? structuredClone(state) : null;
  const writes = [], created = [], completed = [];
  const reserve = (tree, basename) => reserved.add(key(path.join(tree, target, basename)));
  const occupied = (tree, basename) => reserved.has(key(path.join(tree, target, basename)));
  for (const tree of ['mp3', 'stems']) {
    const destination = localPath(root, path.join(root, tree, target));
    if (fs.existsSync(destination)) {
      checkTree(root, destination);
      for (const entry of fs.readdirSync(destination)) reserve(tree, entry);
    }
  }
  const uniqueSong = basename => {
    let result = basename, number = 2;
    while (occupied('mp3', result + '.mp3') || occupied('stems', result)) result = basename + ' [merged ' + number++ + ']';
    reserve('mp3', result + '.mp3'); reserve('stems', result); return result;
  };
  for (const source of sources) {
    const audioDir = localPath(root, path.join(root, 'mp3', source)), stemDir = localPath(root, path.join(root, 'stems', source));
    const handled = new Set();
    if (fs.existsSync(audioDir)) {
      checkTree(root, audioDir);
      for (const entry of fs.readdirSync(audioDir)) {
        const audio = path.join(audioDir, entry);
        if (path.extname(entry).toLowerCase() === '.mp3' && fs.statSync(audio).isFile()) {
          const basename = path.basename(entry, path.extname(entry)), next = uniqueSong(basename);
          const oldFolder = path.join(stemDir, basename), newFolder = path.join(root, 'stems', target, next);
          const newAudio = path.join(root, 'mp3', target, next + '.mp3');
          moves.push([audio, newAudio]);
          if (fs.existsSync(oldFolder)) { checkTree(root, oldFolder); moves.push([oldFolder, newFolder]); handled.add(key(basename)); }
          mappings.push({ audio, newAudio, folder: oldFolder, newFolder });
        } else {
          let next = entry, number = 2;
          while (occupied('mp3', next)) next = entry + '.merged-' + number++;
          reserve('mp3', next); moves.push([audio, path.join(root, 'mp3', target, next)]);
        }
      }
    }
    if (fs.existsSync(stemDir)) {
      checkTree(root, stemDir);
      for (const entry of fs.readdirSync(stemDir)) {
        if (handled.has(key(entry))) continue;
        const oldFolder = path.join(stemDir, entry), next = uniqueSong(entry), newFolder = path.join(root, 'stems', target, next);
        moves.push([oldFolder, newFolder]); mappings.push({ folder: oldFolder, newFolder });
      }
    }
  }
  // A case-only rename needs an intermediate directory on Windows.
  if (key(from) === key(target) && from !== target && sources.length) {
    moves.length = 0; mappings.length = 0;
    for (const tree of ['mp3', 'stems']) {
      const oldFolder = path.join(root, tree, from), newFolder = path.join(root, tree, target);
      if (!fs.existsSync(oldFolder)) continue;
      checkTree(root, oldFolder);
      const temporary = path.join(root, tree, '.artist-rename-' + randomUUID());
      moves.push([oldFolder, temporary], [temporary, newFolder]);
      mappings.push({ directory: oldFolder, newDirectory: newFolder });
    }
  }
  for (const [oldName, destination] of Object.entries(settings.aliases)) if (key(destination) === key(from) || key(destination) === key(target)) settings.aliases[oldName] = target;
  settings.aliases[key(from)] = target; settings.aliases[key(target)] = target;
  const cover = settings.covers[key(target)] || settings.covers[key(from)];
  if (key(from) !== key(target)) delete settings.covers[key(from)];
  if (cover) settings.covers[key(target)] = cover;
  const remap = file => {
    for (const mapping of mappings) {
      if (mapping.directory && (key(file) === key(mapping.directory) || inside(mapping.directory, file))) return mapping.newDirectory + file.slice(mapping.directory.length);
      if (mapping.audio && key(file) === key(mapping.audio)) return mapping.newAudio;
      if (mapping.folder && (key(file) === key(mapping.folder) || inside(mapping.folder, file))) return mapping.newFolder + file.slice(mapping.folder.length);
    }
    return file;
  };
  // Validate metadata before moving anything.
  for (const name of names.filter(name => key(name) === key(from) || key(name) === key(target))) {
    const stemDir = path.join(root, 'stems', name);
    if (!fs.existsSync(stemDir)) continue;
    for (const entry of fs.readdirSync(stemDir)) {
      const file = path.join(stemDir, entry, 'metadata.json');
      if (!fs.existsSync(file)) continue;
      localPath(root, file);
      const data = JSON.parse(fs.readFileSync(file, 'utf8'));
      data.artist = target; metadata.push([remap(file), JSON.stringify(data, null, 2) + '\n']);
    }
  }
  const write = (file, bytes) => {
    writes.push([file, fs.existsSync(file) ? fs.readFileSync(file) : null]); atomicWrite(file, bytes);
  };
  const mkdir = folder => {
    if (fs.existsSync(folder)) return;
    if (folder !== root) mkdir(path.dirname(folder));
    fs.mkdirSync(folder); created.push(folder);
  };
  try {
    for (const [source, destination] of moves) {
      localPath(root, source); localPath(root, destination); mkdir(path.dirname(destination));
      fs.renameSync(source, destination); completed.push([source, destination]);
    }
    for (const [file, bytes] of metadata) write(file, bytes);
    if (state) {
      for (const job of state.jobs) {
        let changed = false;
        for (const field of ['filename', 'stemsFolder']) if (job[field]) {
          const before = path.resolve(root, job[field]), after = remap(before);
          if (after !== before) { job[field] = path.relative(root, after); changed = true; }
        }
        if (changed || (job.artist && (key(safeName(job.artist)) === key(from) || key(safeName(job.artist)) === key(target)))) job.artist = target;
      }
      saveState();
    }
    write(configPath(root), JSON.stringify(settings, null, 2) + '\n');
  } catch (error) {
    const failures = [];
    for (const [file, bytes] of writes.reverse()) try { if (bytes === null) { if (fs.existsSync(file)) fs.unlinkSync(file); } else atomicWrite(file, bytes); } catch (failure) { failures.push(failure.message); }
    for (const [source, destination] of completed.reverse()) try { fs.renameSync(destination, source); } catch (failure) { failures.push(failure.message); }
    if (state) try { Object.assign(state, oldState); saveState(); } catch (failure) { failures.push(failure.message); }
    for (const folder of created.reverse()) try { if (fs.existsSync(folder) && fs.readdirSync(folder).length === 0) fs.rmdirSync(folder); } catch { }
    throw new Error(error.message + (failures.length ? '\nSome files could not be restored: ' + failures.join('; ') : ''));
  }
  for (const source of sources) for (const tree of ['mp3', 'stems']) {
    const folder = localPath(root, path.join(root, tree, source));
    try { if (fs.existsSync(folder) && fs.readdirSync(folder).length === 0) fs.rmdirSync(folder); } catch { }
  }
  return { name: target, mappings, merged: Boolean(existing) };
}

export function setArtistCover({ root, name, image }) {
  root = path.resolve(root); folderName(name);
  const actual = directories(root, 'mp3').find(candidate => key(candidate) === key(name));
  if (!actual) throw new Error('This artist folder no longer exists. Refresh the library.');
  const settings = artistSettings(root);
  if (image) {
    const source = path.resolve(image);
    if (path.extname(source).toLowerCase() !== '.png' || fs.statSync(source).size > 20 * 1024 * 1024) throw new Error('Choose an image that can be saved as a PNG under 20 MB.');
    const bytes = fs.readFileSync(source);
    if (!bytes.subarray(0, 8).equals(Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]))) throw new Error('The artist cover is not a valid PNG image.');
    const folder = localPath(root, path.join(root, '.artist-covers')); fs.mkdirSync(folder, { recursive: true });
    const cover = path.join(folder, randomUUID() + '.png');
    fs.writeFileSync(cover, bytes, { flag: 'wx' }); settings.covers[key(actual)] = path.relative(root, cover);
    try { atomicWrite(configPath(root), JSON.stringify(settings, null, 2) + '\n'); }
    catch (error) { fs.unlinkSync(cover); throw error; }
  } else {
    delete settings.covers[key(actual)]; atomicWrite(configPath(root), JSON.stringify(settings, null, 2) + '\n');
  }
  return { name: actual };
}
