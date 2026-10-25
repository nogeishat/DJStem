import { spawn } from 'node:child_process';
import { buildPlayer } from './build-player.mjs';
import { ROOT } from './setup-player.mjs';
import { libraryRoot } from '../lib/library.mjs';

try {
  const executable = await buildPlayer();
  const player = spawn(executable, [libraryRoot(ROOT)], { windowsHide: false, stdio: 'ignore' });
  player.once('error', error => { console.error(error.message); process.exitCode = 1; });
  player.once('exit', code => { process.exitCode = code || 0; });
} catch (error) { console.error(error.message); process.exitCode = 1; }
