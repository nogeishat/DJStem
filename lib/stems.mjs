import fs from 'node:fs';
import path from 'node:path';
import { spawn, spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import readline from 'node:readline';
import { inside } from './music.mjs';
import { dataPath, settingsPath } from './paths.mjs';

export function separationDevice(root) {
  if (process.env.STEMKIT_EDITION === 'cpu') return 'cpu';
  if (process.env.STEMKIT_DEVICE) return process.env.STEMKIT_DEVICE;
  try { return JSON.parse(fs.readFileSync(settingsPath(root), 'utf8')).separationDevice === 'cpu' ? 'cpu' : 'auto'; }
  catch { return 'auto'; }
}

export const UVR_COMMIT = '5517e0cf0d1acd16a1618eeedec596957523f9e1';
export const STEM_MODELS = {
  drums: 'Demucs: v4 | htdemucs', bass: 'Demucs: v4 | htdemucs', other: 'Demucs: v4 | htdemucs',
  vocals: 'MDX-Net: UVR-MDX-NET Inst HQ 5',
};

export function stemsReady(audio, folder) {
  try {
    const manifest = JSON.parse(fs.readFileSync(path.join(folder, 'separation.json'), 'utf8'));
    if (manifest.pipelineVersion !== 1 || manifest.uvrCommit !== UVR_COMMIT || manifest.inputSha256 !== createHash('sha256').update(fs.readFileSync(audio)).digest('hex')) return false;
    for (const [stem, model] of Object.entries(STEM_MODELS)) {
      if (manifest.models?.[stem] !== model) return false;
      const file = path.join(folder, stem + '.wav');
      if (fs.statSync(file).size <= 44) return false;
      const handle = fs.openSync(file, 'r');
      const header = Buffer.alloc(12);
      try { fs.readSync(handle, header, 0, 12, 0); } finally { fs.closeSync(handle); }
      if (header.toString('ascii', 0, 4) !== 'RIFF' || header.toString('ascii', 8, 12) !== 'WAVE') return false;
    }
    return true;
  } catch { return false; }
}

export class StemSeparator {
  constructor({ root, libraryRoot = path.join(root, 'downloads'), spawnProcess = spawn } = {}) {
    this.root = root; this.spawnProcess = spawnProcess; this.child = null; this.stopping = false;
    this.libraryRoot = libraryRoot;
    this.python = process.env.STEMKIT_PYTHON || path.join(root, 'tools', 'stem-runtime', 'Scripts', 'python.exe');
  }

  async run(audio, folder, onProgress = () => {}) {
    if (!inside(this.libraryRoot, audio) || !inside(this.libraryRoot, folder) || path.resolve(audio) === path.resolve(folder)) throw new Error('Invalid stems folder.');
    if (stemsReady(audio, folder)) { onProgress({ progress: 100, message: 'Four stems already saved' }); return folder; }
    if (this.stopping) throw new Error('Stem processing stopped.');
    if (!fs.existsSync(this.python) || !fs.existsSync(path.join(this.root, 'tools', 'stem-models', 'mdx', 'config.json'))) {
      throw new Error('Stem processing is not installed. Run npm run setup:stems, then retry processing. Your MP3 is already saved.');
    }
    const args = ['-u', path.join(this.root, 'scripts', 'separate-stems.py'), '--input', audio, '--output', folder, '--device', separationDevice(this.root)];
    await new Promise((resolve, reject) => {
      const child = this.spawnProcess(this.python, args, { cwd: this.root, windowsHide: true, shell: false, stdio: ['ignore', 'pipe', 'pipe'], env: { ...process.env, PYTHONUTF8: '1', PYTHONUNBUFFERED: '1', NUMBA_CACHE_DIR: process.env.NUMBA_CACHE_DIR || dataPath(this.root, 'numba-cache') } });
      this.child = child;
      const errors = [];
      let spawnError = false;
      const stdout = readline.createInterface({ input: child.stdout });
      const stderr = readline.createInterface({ input: child.stderr });
      stdout.on('line', line => {
        if (!line.startsWith('STEMKIT_STEMS:')) return;
        try { onProgress(JSON.parse(line.slice('STEMKIT_STEMS:'.length))); } catch { }
      });
      stderr.on('line', line => { errors.push(line); if (errors.length > 30) errors.shift(); });
      child.once('error', error => { spawnError = true; this.child = null; reject(new Error(`Could not start UVR: ${error.message}`)); });
      child.once('close', code => {
        stdout.close(); stderr.close(); this.child = null;
        if (spawnError) return;
        if (this.stopping) return reject(new Error('Stem processing stopped.'));
        if (code !== 0) {
          fs.mkdirSync(folder, { recursive: true });
          fs.writeFileSync(path.join(folder, 'stem-error.log'), errors.join('\n') + '\n');
          return reject(new Error(`UVR separation failed: ${errors.slice(-2).join(' ').slice(0, 800) || 'exit code ' + code}. The MP3 is saved; right-click to retry processing.`));
        }
        resolve();
      });
    });
    if (!stemsReady(audio, folder)) throw new Error('UVR did not produce four valid stems for the selected models. The MP3 is saved; retry processing.');
    return folder;
  }

  stop() {
    this.stopping = true;
    if (!this.child) return;
    if (process.platform === 'win32' && this.child.pid) spawnSync('taskkill.exe', ['/PID', String(this.child.pid), '/T', '/F'], { windowsHide: true, stdio: 'ignore' });
    else this.child.kill();
  }
}
