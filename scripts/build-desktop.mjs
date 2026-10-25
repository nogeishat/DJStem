import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
export function buildDesktop() {
  if (process.platform !== 'win32') throw new Error('The desktop drop box requires Windows.');
  const source = path.join(root, 'desktop', 'StemKit.cs');
  const target = path.join(root, 'tools', 'StemKit.exe');
  const assembly = path.join(root, 'desktop', 'AssemblyInfo.cs');
  if (fs.existsSync(target) && [source, assembly].every(file => fs.statSync(target).mtimeMs >= fs.statSync(file).mtimeMs)) return target;
  const compiler = path.join(process.env.WINDIR || 'C:\\Windows', 'Microsoft.NET', 'Framework64', 'v4.0.30319', 'csc.exe');
  fs.mkdirSync(path.dirname(target), { recursive: true });
  const result = spawnSync(compiler, ['/nologo', '/target:winexe', '/optimize+', `/out:${target}`,
    ...(fs.existsSync(path.join(root, 'assets', 'app.ico')) ? ['/win32icon:' + path.join(root, 'assets', 'app.ico')] : []), '/reference:System.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll', '/reference:System.Web.Extensions.dll', assembly, source], { encoding: 'utf8', windowsHide: true });
  if (result.status !== 0) throw new Error(`Desktop build failed: ${result.stdout || result.stderr || result.error?.message}`);
  return target;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try { console.log(buildDesktop()); } catch (error) { console.error(error.message); process.exitCode = 1; }
}
