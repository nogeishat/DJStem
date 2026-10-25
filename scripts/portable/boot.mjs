import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { configureEnvironment, relocateState } from './state.mjs';

export const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');

export async function launch(mode) {
  try {
    if (process.platform !== 'win32' || process.arch !== 'x64') throw new Error('This USB edition requires 64-bit Windows 10/11.');
    configureEnvironment(ROOT);
    relocateState(ROOT);
    if (mode === 'check') {
      const { verify } = await import('./verify.mjs');
      await verify(ROOT);
    } else if (mode === 'player') {
      await import('../run-player.mjs');
    } else if (mode === 'downloader') {
      await import('../run-downloader.mjs');
    } else throw new Error('Unknown portable launch mode.');
  } catch (error) {
    fs.mkdirSync(path.join(ROOT, 'data'), { recursive: true });
    fs.writeFileSync(path.join(ROOT, 'data', 'launch-error.log'), `${new Date().toISOString()}\n${error.stack || error}\n`);
    console.error(error.stack || error.message);
    process.exitCode = 1;
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) await launch(process.argv[2] || 'downloader');
