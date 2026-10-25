import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

export const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
export const PLAYER_DLLS = ['NAudio.Core.dll', 'NAudio.WinMM.dll', 'NAudio.Wasapi.dll'];
const VERSION = '2.2.1';
const quote = text => "'" + text.replaceAll("'", "''") + "'";

function setupRealtime(tools) {
  const names = ['rubberband-3.dll', 'sleefdft.dll', 'sleef.dll', 'samplerate.dll', 'MSVCP140.dll', 'VCRUNTIME140.dll', 'VCRUNTIME140_1.dll'];
  if (names.every(name => fs.existsSync(path.join(tools, name)))) return;
  const source = [process.env.STEMKIT_RUBBERBAND_DIR, path.join(process.env.ProgramFiles || 'C:\\Program Files', 'Mixxx')]
    .filter(Boolean).find(dir => fs.existsSync(path.join(dir, 'rubberband-3.dll')));
  if (!source) throw new Error('Live BPM/key processing needs the 64-bit Mixxx Rubber Band runtime. Install Mixxx or set STEMKIT_RUBBERBAND_DIR to its DLL folder.');
  // Copy the main library last so an interrupted setup is retried.
  fs.mkdirSync(tools, { recursive: true });
  const receipts = [];
  for (const name of [...names.slice(1), names[0]]) {
    const file = path.join(source, name);
    if (!fs.existsSync(file)) throw new Error(`The live audio runtime is incomplete: ${file}`);
    const bytes = fs.readFileSync(file);
    fs.writeFileSync(path.join(tools, name), bytes);
    receipts.push({ name, source: file, sha256: createHash('sha256').update(bytes).digest('hex') });
  }
  fs.mkdirSync(path.join(tools, 'player-packages'), { recursive: true });
  fs.writeFileSync(path.join(tools, 'player-packages', 'realtime-receipts.json'), JSON.stringify(receipts, null, 2));
  for (const name of ['COPYING', 'LICENSE']) {
    const license = path.join(source, 'doc', name);
    if (fs.existsSync(license)) fs.copyFileSync(license, path.join(tools, 'Mixxx-' + name + '.txt'));
  }
}

export async function setupPlayer() {
  const tools = path.join(ROOT, 'tools');
  setupRealtime(tools);
  if (PLAYER_DLLS.every(name => fs.existsSync(path.join(tools, name)))) return;
  const receipts = [];
  for (const name of PLAYER_DLLS) {
    const id = name.replace(/\.dll$/, '').toLowerCase();
    const base = `https://api.nuget.org/v3-flatcontainer/${id}/${VERSION}/${id}.${VERSION}.nupkg`;
    console.log(`Installing ${name} ${VERSION} for the desktop player…`);
    const binary = await fetch(base);
    if (!binary.ok) throw new Error(`Could not download ${name} from NuGet (${binary.status}).`);
    const bytes = Buffer.from(await binary.arrayBuffer());
    const actual = createHash('sha512').update(bytes).digest('base64');
    const folder = path.join(tools, 'player-packages', id);
    const zip = folder + '.zip';
    fs.mkdirSync(path.dirname(folder), { recursive: true }); fs.writeFileSync(zip, bytes);
    const extract = spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', `Expand-Archive -LiteralPath ${quote(zip)} -DestinationPath ${quote(folder)} -Force`], { windowsHide: true, encoding: 'utf8' });
    if (extract.status !== 0) throw new Error(`Could not unpack ${name}: ${extract.stderr || extract.stdout}`);
    fs.copyFileSync(path.join(folder, 'lib', 'netstandard2.0', name), path.join(tools, name));
    receipts.push({ name, version: VERSION, packageSha512: actual, source: base });
  }
  fs.writeFileSync(path.join(tools, 'player-packages', 'receipts.json'), JSON.stringify(receipts, null, 2));
  const license = await fetch('https://raw.githubusercontent.com/naudio/NAudio/v2.2.1/license.txt');
  if (!license.ok) throw new Error('Could not download the NAudio license.');
  fs.writeFileSync(path.join(tools, 'NAudio-LICENSE.txt'), await license.text());
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try { await setupPlayer(); } catch (error) { console.error(error.message); process.exitCode = 1; }
}
