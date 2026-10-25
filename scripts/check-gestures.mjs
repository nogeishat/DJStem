import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { playerReferences, PLAYER_SOURCES } from './build-player.mjs';
import { ROOT, setupPlayer } from './setup-player.mjs';

await setupPlayer();
const target = path.join(ROOT, 'tools', 'PlayerGestureTests.exe');
const compiler = path.join(process.env.WINDIR || 'C:\\Windows', 'Microsoft.NET', 'Framework64', 'v4.0.30319', 'csc.exe');
const build = spawnSync(compiler, ['/nologo', '/target:exe', '/platform:x64', '/main:PlayerGestureTests', `/out:${target}`,
  '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll', '/reference:System.Web.Extensions.dll',
  ...playerReferences().map(file => '/reference:' + file), ...[...PLAYER_SOURCES, 'PlayerGestureTests.cs'].map(name => path.join(ROOT, 'desktop', name))], { windowsHide: true, encoding: 'utf8' });
if (build.status !== 0) { console.error(build.stdout || build.stderr); process.exitCode = 1; }
else {
  const args = process.argv.includes('--preview') ? [path.join(ROOT, 'tools', 'stem-gesture-preview.png')] : [];
  const run = spawnSync(target, args, { windowsHide: true, encoding: 'utf8', timeout: 120000 });
  console.log(run.stdout.trim()); if (run.stderr) console.error(run.stderr.trim()); process.exitCode = run.status || (run.error ? 1 : 0);
}
