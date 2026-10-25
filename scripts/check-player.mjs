import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { playerReferences, PLAYER_SOURCES } from './build-player.mjs';
import { ROOT, setupPlayer } from './setup-player.mjs';
import { libraryRoot } from '../lib/library.mjs';

await setupPlayer();
const target = path.join(ROOT, 'tools', 'PlayerTests.exe');
const compiler = path.join(process.env.WINDIR || 'C:\\Windows', 'Microsoft.NET', 'Framework64', 'v4.0.30319', 'csc.exe');
const result = spawnSync(compiler, ['/nologo', '/target:exe', '/optimize+', '/platform:x64', '/main:PlayerTests', `/out:${target}`,
  '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll', '/reference:System.Web.Extensions.dll',
  ...playerReferences().map(file => '/reference:' + file), ...[...PLAYER_SOURCES, 'PlayerTests.cs', 'PlayerMatchedCacheTests.cs'].map(name => path.join(ROOT, 'desktop', name))], { windowsHide: true, encoding: 'utf8' });
if (result.status !== 0) { console.error(result.stdout || result.stderr); process.exitCode = 1; }
else {
  const args = process.argv.includes('--library') ? [libraryRoot(ROOT), ROOT] : process.argv.includes('--matching') ? ['', ROOT] : [];
  const run = spawnSync(target, args, { windowsHide: true, encoding: 'utf8', timeout: 60000 });
  if (run.stdout) console.log(run.stdout.trim());
  if (run.stderr || run.error) console.error(run.stderr?.trim() || run.error.message);
  process.exitCode = run.status || (run.error ? 1 : 0);
}
