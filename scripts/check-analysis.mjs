import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { ROOT } from './setup-player.mjs';

const python = path.join(ROOT, 'tools', 'stem-runtime', 'Scripts', 'python.exe');
const run = spawnSync(python, [path.join(ROOT, 'scripts', 'check-analysis.py')], {
  cwd: ROOT, windowsHide: true, encoding: 'utf8', timeout: 120000,
  env: { ...process.env, PYTHONUTF8: '1', NUMBA_CACHE_DIR: path.join(ROOT, 'tools', 'numba-cache') },
});
if (run.stdout) console.log(run.stdout.trim());
if (run.stderr) console.log(run.stderr.trim());
if (run.error) console.error(run.error.message);
process.exitCode = run.status || (run.error ? 1 : 0);
