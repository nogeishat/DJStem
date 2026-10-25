import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { playerReferences, PLAYER_SOURCES } from './build-player.mjs';
import { ROOT, setupPlayer } from './setup-player.mjs';

await setupPlayer();
const target = path.join(ROOT, 'tools', 'PlayerAutoPlayTests.exe');
const compiler = path.join(process.env.WINDIR || 'C:\\Windows', 'Microsoft.NET', 'Framework64', 'v4.0.30319', 'csc.exe');
const build = spawnSync(compiler, ['/nologo', '/target:exe', '/platform:x64', '/main:PlayerAutoPlayTests', `/out:${target}`,
  '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll', '/reference:System.Web.Extensions.dll',
  ...playerReferences().map(file => '/reference:' + file), ...[...PLAYER_SOURCES, 'PlayerAutoPlayTests.cs'].map(name => path.join(ROOT, 'desktop', name))], { windowsHide: true, encoding: 'utf8' });
if (build.status !== 0) { console.error(build.stdout || build.stderr); process.exitCode = 1; }
else {
  const args = process.argv.includes('--replays') ? ['--replays'] : process.argv.includes('--markers') ? ['--markers'] : process.argv.includes('--seeks') ? ['--seeks'] : process.argv.includes('--next') ? ['--next'] : process.argv.includes('--handoff') ? ['--handoff'] : process.argv.includes('--preloading') ? ['--preloading'] : [];
  // Give the seek fixture a fresh WinForms/audio session after the editor's
  // displayed preview is disposed. Run both processes serially in the full suite.
  for (const testArgs of args.length ? [args] : [[], ['--seeks']]) {
    const run = spawnSync(target, testArgs, { windowsHide: true, encoding: 'utf8', timeout: 120000 });
    if (run.stdout) console.log(run.stdout.trim()); if (run.stderr) console.error(run.stderr.trim()); if (run.error) console.error(run.error.message);
    if (run.status !== 0 || run.error) { process.exitCode = run.status || 1; break; }
  }
}
