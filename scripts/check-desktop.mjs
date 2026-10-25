import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { ROOT } from '../server.mjs';

const target = path.join(ROOT, 'tools', 'NativeTests.exe');
fs.mkdirSync(path.dirname(target), { recursive: true });
const compiler = path.join(process.env.WINDIR || 'C:\\Windows', 'Microsoft.NET', 'Framework64', 'v4.0.30319', 'csc.exe');
const build = spawnSync(compiler, ['/nologo', '/target:exe', '/main:NativeTests', `/out:${target}`,
  '/reference:System.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll', '/reference:System.Web.Extensions.dll',
  path.join(ROOT, 'desktop', 'StemKit.cs'), path.join(ROOT, 'desktop', 'NativeTests.cs')], { encoding: 'utf8', windowsHide: true });
if (build.status !== 0) { console.error(build.stdout || build.stderr); process.exitCode = 1; }
else {
  const run = spawnSync(target, [], { encoding: 'utf8', windowsHide: true });
  console.log(run.stdout.trim()); if (run.stderr) console.error(run.stderr.trim()); process.exitCode = run.status ?? 1;
}
