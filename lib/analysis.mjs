import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { spawn } from 'node:child_process';
import { ANALYSIS_VERSION, analysisFields, tuningFields } from './metadata.mjs';
import { dataPath } from './paths.mjs';

export class MusicAnalyzer {
  constructor({ root, spawnProcess = spawn }) {
    this.root = root; this.spawnProcess = spawnProcess; this.child = null; this.stopping = false;
    this.python = process.env.STEMKIT_PYTHON || path.join(root, 'tools', 'stem-runtime', 'Scripts', 'python.exe');
  }

  async run(audio, { tuningOnly = false } = {}) {
    if (this.stopping) throw new Error('Music analysis stopped.');
    const script = path.join(this.root, 'scripts', 'analyze-music.py');
    const estimator = path.join(this.root, 'scripts', 'tuning_estimator.py');
    const validate = tuningOnly ? tuningFields : analysisFields;
    const hash = createHash('sha256').update(`mp3-analysis-v${ANALYSIS_VERSION}|${tuningOnly}|`).update(fs.readFileSync(script))
      .update(fs.readFileSync(estimator)).update(fs.readFileSync(audio)).digest('hex');
    const cache = dataPath(this.root, 'music-analysis', `mp3-${hash}.json`);
    if (fs.existsSync(cache)) {
      try { const result = JSON.parse(fs.readFileSync(cache, 'utf8')); validate(result); return result; }
      catch { /* An incomplete/outdated cache must be recomputed. */ }
    }
    if (!fs.existsSync(this.python)) throw new Error('Music analysis needs the stem runtime. Run npm run setup:stems, then retry processing.');
    await new Promise((resolve, reject) => {
      const child = this.spawnProcess(this.python, ['-u', script, '--input', audio, '--output', cache, ...(tuningOnly ? ['--tuning-only'] : [])], {
        cwd: this.root, windowsHide: true, shell: false, stdio: ['ignore', 'pipe', 'pipe'],
        env: { ...process.env, PYTHONUTF8: '1', NUMBA_CACHE_DIR: process.env.NUMBA_CACHE_DIR || dataPath(this.root, 'numba-cache') },
      });
      this.child = child;
      let errors = '', spawnError = false, timedOut = false;
      const timer = setTimeout(() => { timedOut = true; child.kill(); }, 180000);
      child.stdout.resume();
      child.stderr.on('data', data => { errors = (errors + data.toString()).slice(-1600); });
      child.once('error', error => { spawnError = true; clearTimeout(timer); this.child = null; reject(new Error(`Could not start music analysis: ${error.message}`)); });
      child.once('close', code => {
        clearTimeout(timer); this.child = null;
        if (spawnError) return;
        if (this.stopping) return reject(new Error('Music analysis stopped.'));
        if (timedOut) return reject(new Error('Music analysis timed out. The MP3 is saved; retry processing.'));
        if (code !== 0) return reject(new Error(`Music analysis failed: ${errors.trim() || `exit code ${code}`}. The MP3 is saved; retry processing.`));
        resolve();
      });
    });
    const result = JSON.parse(fs.readFileSync(cache, 'utf8')); validate(result); return result;
  }

  stop() { this.stopping = true; this.child?.kill(); }
}
