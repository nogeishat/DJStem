import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { spawn, spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { ROOT } from '../server.mjs';
import { downloadModels, MDX_NAME } from './download-stem-models.mjs';
import { UVR_COMMIT } from '../lib/stems.mjs';

const runtime = path.join(ROOT, 'tools', 'stem-runtime');
const python = path.join(runtime, 'Scripts', 'python.exe');
const marker = path.join(runtime, 'ready.json');
const requirements = path.join(ROOT, 'requirements-stems.txt');
const env = { ...process.env, PYTHONUTF8: '1', NUMBA_CACHE_DIR: path.join(ROOT, 'tools', 'numba-cache') };
const fingerprint = () => createHash('sha256').update(fs.readFileSync(requirements)).update(UVR_COMMIT).update('torch2.5.1+cu124').digest('hex');

function run(command, args, capture = false) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { cwd: ROOT, env, shell: false, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
    let output = '', errors = '';
    child.stdout.on('data', chunk => {
      output = capture ? output + chunk : (output + chunk).slice(-1500);
      if (!capture) process.stdout.write(chunk);
    });
    child.stderr.on('data', chunk => {
      errors = (errors + chunk).slice(-1500);
      if (!capture) process.stderr.write(chunk);
    });
    child.once('error', reject);
    child.once('close', code => code === 0 ? resolve(output) : reject(new Error(`${path.basename(command)} failed (${code}): ${errors.trim() || output.trim() || 'No diagnostic output.'}`)));
  });
}

function basePython(requiredMinor) {
  const bundled = path.join(os.homedir(), '.cache', 'codex-runtimes', 'codex-primary-runtime', 'dependencies', 'python', 'python.exe');
  const candidates = process.env.STEMKIT_BASE_PYTHON ? [[process.env.STEMKIT_BASE_PYTHON, []]] : [[bundled, []], ['py', ['-3.12']], ['py', ['-3.11']], ['py', ['-3.10']], ['python', []]];
  for (const [command, args] of candidates) {
    const result = spawnSync(command, [...args, '-c', 'import sys; print(sys.executable); print(f"{sys.version_info.major}.{sys.version_info.minor}")'], { encoding: 'utf8', windowsHide: true, timeout: 15000 });
    const lines = result.stdout?.trim().split(/\r?\n/);
    if (result.status === 0 && /^3\.(10|11|12)$/.test(lines?.[1]) && (!requiredMinor || lines[1] === `3.${requiredMinor}`)) return lines[0];
  }
  if (requiredMinor) throw new Error(`The installed stem packages need Python 3.${requiredMinor}. Install it, or set STEMKIT_BASE_PYTHON to its python.exe, then run npm run setup:stems.`);
  throw new Error('Install Python 3.10–3.12, or set STEMKIT_BASE_PYTHON to its python.exe, then run npm run setup:stems.');
}

export async function setupStems({ verifyOnly = false } = {}) {
  const expected = fingerprint();
  const files = [python, path.join(ROOT, 'tools', 'uvr', 'separate.py'), path.join(ROOT, 'tools', 'stem-models', 'demucs', '955717e8-8726e21a.th'), path.join(ROOT, 'tools', 'stem-models', 'demucs', 'htdemucs.yaml'), path.join(ROOT, 'tools', 'stem-models', 'mdx', MDX_NAME), path.join(ROOT, 'tools', 'stem-models', 'mdx', 'config.json')];
  let ready;
  try { ready = JSON.parse(fs.readFileSync(marker, 'utf8')); } catch { }
  const installed = ready?.fingerprint === expected && files.every(file => fs.existsSync(file));
  let usable = false;
  if (fs.existsSync(python)) {
    const check = spawnSync(python, ['-c', 'import sys'], { windowsHide: true, timeout: 15000 });
    usable = check.status === 0;
  }
  if (!verifyOnly && installed && usable) return ready;
  if (!verifyOnly && !usable) {
    // A copied Windows venv keeps an absolute reference to its original base Python.
    // Rebind it without clearing site-packages or downloading the installed models again.
    const existing = fs.existsSync(python);
    console.log(existing ? 'Repairing the Python runtime path; keeping installed packages and models.' : 'Creating the Python runtime.');
    const keepPip = fs.existsSync(path.join(runtime, 'Lib', 'site-packages', 'pip', '__init__.py'));
    let minor;
    try { minor = fs.readFileSync(path.join(runtime, 'pyvenv.cfg'), 'utf8').match(/^version\s*=\s*3\.(\d+)/m)?.[1]; } catch { }
    await run(basePython(minor), ['-m', 'venv', ...(keepPip ? ['--without-pip'] : []), runtime]);
  }
  if (!verifyOnly && !installed) {
    console.log('Preparing UVR stem separation. First setup downloads several GB; later launches reuse it.');
    fs.mkdirSync(path.dirname(runtime), { recursive: true });
    const repo = path.join(ROOT, 'tools', 'uvr');
    if (!fs.existsSync(path.join(repo, '.git'))) await run('git', ['clone', 'https://github.com/Anjok07/ultimatevocalremovergui.git', repo]);
    await run('git', ['-C', repo, 'checkout', '--detach', UVR_COMMIT]);
    const pip = ['-m', 'pip', '--isolated', 'install', '--disable-pip-version-check', '--cache-dir', path.join(ROOT, 'tools', 'pip-cache')];
    await run(python, [...pip, 'torch==2.5.1', 'torchvision==0.20.1', 'torchaudio==2.5.1', '--index-url', 'https://download.pytorch.org/whl/cu124']);
    await run(python, [...pip, '-r', requirements]);
    await downloadModels();
  }
  if (!files.every(file => fs.existsSync(file))) throw new Error('The UVR runtime or requested model files are missing. Run npm run setup:stems.');
  const repoCommit = (await run('git', ['-C', path.join(ROOT, 'tools', 'uvr'), 'rev-parse', 'HEAD'], true)).trim();
  if (repoCommit !== UVR_COMMIT) throw new Error('UVR source version does not match StemKit. Run npm run setup:stems.');
  const output = await run(python, ['-u', path.join(ROOT, 'scripts', 'separate-stems.py'), '--check'], true);
  const details = JSON.parse(output.trim().split(/\r?\n/).at(-1));
  ready = { fingerprint: expected, ...details, installedAt: new Date().toISOString() };
  fs.writeFileSync(marker + '.tmp', JSON.stringify(ready, null, 2) + '\n');
  fs.renameSync(marker + '.tmp', marker);
  console.log(`UVR ready: ${details.gpu || 'CPU'}; htdemucs + Inst HQ 5.`);
  return ready;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try { await setupStems({ verifyOnly: process.argv.includes('--verify') }); }
  catch (error) { console.error(error.message); process.exitCode = 1; }
}
