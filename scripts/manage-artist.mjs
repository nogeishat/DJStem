import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { renameArtist, setArtistCover } from '../lib/artists.mjs';
import { dataPath } from '../lib/paths.mjs';

const project = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
try {
  let input = '';
  for await (const chunk of process.stdin) input += chunk;
  const body = JSON.parse(Buffer.from(input.trim(), 'base64').toString('utf8')), root = path.resolve(body.root);
  const address = `http://127.0.0.1:${process.env.PORT || 4317}`;
  let running;
  try {
    const response = await fetch(address + '/api/state', { signal: AbortSignal.timeout(1500) });
    if (!response.ok) throw new Error('The downloader service could not be reached.');
    running = await response.json();
  } catch (error) {
    // Only a refused connection means it is safe to edit the saved queue offline.
    if (error.cause?.code !== 'ECONNREFUSED') throw new Error('The downloader service is not responding. Restart it before editing artist folders.');
  }
  let result;
  if (running) {
    if (!running.artistFolders) throw new Error('Restart the StemKit downloader to enable artist folder editing.');
    const identity = file => process.platform === 'win32' ? path.resolve(file).toLowerCase() : path.resolve(file);
    if (identity(running.downloadsDir) !== identity(root)) throw new Error('The downloader is using another music library. Close it before editing this library.');
    const response = await fetch(address + '/api/artists', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
    const data = await response.json();
    if (!response.ok) throw new Error(data.error || 'Restart the downloader to enable artist folder editing.');
    result = data;
  } else if (body.operation === 'rename') {
    const queueFile = dataPath(project, 'queue.json');
    const state = fs.existsSync(queueFile) ? JSON.parse(fs.readFileSync(queueFile, 'utf8')) : { version: 1, jobs: [] };
    const saveState = () => {
      if (!fs.existsSync(queueFile)) return;
      const temporary = queueFile + '.artist-rename.tmp';
      fs.writeFileSync(temporary, JSON.stringify(state, null, 2) + '\n'); fs.renameSync(temporary, queueFile);
    };
    result = renameArtist({ root, from: body.name, to: body.newName, state, saveState });
  } else if (body.operation === 'cover') result = setArtistCover({ root, name: body.name, image: body.image });
  else throw new Error('Unknown artist folder operation.');
  console.log(JSON.stringify(result));
} catch (error) { console.error(error.message); process.exitCode = 1; }
