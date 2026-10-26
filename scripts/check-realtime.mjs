import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { playerReferences, PLAYER_SOURCES } from './build-player.mjs';
import { ROOT, setupPlayer } from './setup-player.mjs';

await setupPlayer();
const target = path.join(ROOT, 'tools', 'PlayerRealtimeTests.exe');
const compiler = path.join(process.env.WINDIR || 'C:\\Windows', 'Microsoft.NET', 'Framework64', 'v4.0.30319', 'csc.exe');
const build = spawnSync(compiler, ['/nologo', '/target:exe', '/optimize+', '/platform:x64', '/main:PlayerRealtimeTests', `/out:${target}`,
  '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll', '/reference:System.Web.Extensions.dll',
  ...playerReferences().map(file => '/reference:' + file), ...[...PLAYER_SOURCES, 'PlayerRealtimeTests.cs'].map(name => path.join(ROOT, 'desktop', name))], { windowsHide: true, encoding: 'utf8' });
if (build.status !== 0) { console.error(build.stdout || build.stderr); process.exitCode = 1; }
else {
  const run = spawnSync(target, process.argv.slice(2), { windowsHide: true, encoding: 'utf8', timeout: 90000 });
  if (run.stdout) console.log(run.stdout.trim()); if (run.stderr) console.error(run.stderr.trim()); if (run.error) console.error(run.error.message);
  process.exitCode = run.status || (run.error ? 1 : 0);
}
