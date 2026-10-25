import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { deleteSong } from '../lib/song-delete.mjs';
import { dataPath } from '../lib/paths.mjs';

const project = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
try {
  let input = ''; for await (const chunk of process.stdin) input += chunk;
  const body = JSON.parse(Buffer.from(input.trim(), 'base64').toString('utf8'));
  if (body.operation !== 'delete') throw new Error('Unknown song operation.');
  const root = path.resolve(body.root), address = `http://127.0.0.1:${process.env.PORT || 4317}`;
  let running;
  try {
    const response = await fetch(address + '/api/state', { signal: AbortSignal.timeout(1500) });
    if (!response.ok) throw new Error('The downloader service could not be reached.');
    running = await response.json();
  } catch (error) {
    if (error.cause?.code !== 'ECONNREFUSED') throw new Error('The downloader is not responding. Restart it before deleting a song.');
  }
  let result;
  if (running) {
    if (!running.songDeletion) throw new Error('Restart the StemKit drop box to enable song deletion.');
    const identity = file => process.platform === 'win32' ? path.resolve(file).toLowerCase() : path.resolve(file);
    if (identity(root) !== identity(running.downloadsDir)) throw new Error('The downloader is using another library. Close it before deleting this song.');
    const response = await fetch(address + '/api/songs/delete', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
    result = await response.json(); if (!response.ok) throw new Error(result.error || 'Song deletion failed.');
  } else result = deleteSong({ root, dataDir: dataPath(project), audio: body.audio });
  console.log(JSON.stringify(result));
} catch (error) { console.error(error.message); process.exitCode = 1; }
