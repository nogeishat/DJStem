import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { atomicJson } from './state.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const source = path.resolve(process.argv[2]);
const library = path.join(root, 'library');
async function hash(file) {
  const digest = createHash('sha256');
  for await (const bytes of fs.createReadStream(file)) digest.update(bytes);
  return digest.digest('hex');
}
function files(folder, prefix = '') {
  return fs.readdirSync(folder, { withFileTypes: true }).flatMap(entry => {
    if (entry.name === '.work') return [];
    const name = path.join(prefix, entry.name);
    return entry.isDirectory() ? files(path.join(folder, entry.name), name) : [name];
  });
}
const inventory = [];
for (const part of ['mp3', 'stems', '.artist-covers']) {
  if (!fs.existsSync(path.join(source, part))) continue;
  for (const relative of files(path.join(source, part))) {
    const name = path.join(part, relative);
    const original = path.join(source, name), copied = path.join(library, name);
    const bytes = fs.statSync(original).size;
    if (fs.statSync(copied).size !== bytes) throw new Error(`Size mismatch: ${name}`);
    const [before, after] = await Promise.all([hash(original), hash(copied)]);
    if (before !== after) throw new Error(`Checksum mismatch: ${name}`);
    inventory.push({ file: name, bytes, sha256: after });
  }
}
if (fs.existsSync(path.join(source, '.artist-folders.json'))) {
  const before = await hash(path.join(source, '.artist-folders.json'));
  if (before !== await hash(path.join(library, '.artist-folders.json'))) throw new Error('Artist settings mismatch.');
}
atomicJson(path.join(root, 'data', 'library-copy-check.json'), { passed: true, testedAt: new Date().toISOString(), files: inventory.length, bytes: inventory.reduce((total, item) => total + item.bytes, 0), inventory });
console.log(`Library copy verified: ${inventory.length} files, ${inventory.reduce((total, item) => total + item.bytes, 0)} bytes; every SHA-256 matches the original.`);
