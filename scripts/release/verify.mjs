import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { run } from '../portable/verify.mjs';
import { atomicJson } from '../portable/state.mjs';

export async function verifyRelease(root, library, edition) {
  const checks = {};
  for (const [name, args] of [['node', ['--version']], ['ffmpeg', ['-version']], ['ffprobe', ['-version']], ['yt-dlp', ['--version']]]) {
    checks[name] = (await run(path.join(root, 'tools', name + '.exe'), args)).split(/\r?\n/)[0];
    console.log(name + ': ' + checks[name]);
  }
  const python = process.env.STEMKIT_PYTHON;
  checks.python = JSON.parse(await run(python, ['-c', 'import json,sys,ssl,sqlite3,ctypes,librosa,numpy,soundfile,torch,onnxruntime; print(json.dumps({"prefix":sys.prefix,"basePrefix":sys.base_prefix,"torch":torch.__version__,"cudaBuild":torch.version.cuda,"cudaAvailable":torch.cuda.is_available(),"providers":onnxruntime.get_available_providers()}))']));
  if (path.resolve(checks.python.prefix).toLowerCase() !== path.resolve(root, 'tools', 'stem-runtime').toLowerCase() || checks.python.prefix !== checks.python.basePrefix) throw new Error('The private Python runtime is using a host installation.');
  if (edition === 'cpu' && (checks.python.cudaBuild || checks.python.providers.includes('CUDAExecutionProvider'))) throw new Error('The CPU edition contains GPU packages.');
  if (edition === 'nvidia' && (!checks.python.cudaBuild || !checks.python.providers.includes('CUDAExecutionProvider'))) throw new Error('The NVIDIA edition is missing GPU packages.');
  checks.separation = JSON.parse((await run(python, ['-u', path.join(root, 'scripts', 'separate-stems.py'), '--check'])).split(/\r?\n/).at(-1));
  const receipts = JSON.parse(fs.readFileSync(path.join(root, 'tools', 'stem-models', 'model-receipts.json'), 'utf8'));
  for (const [name, subdir] of [['htdemucs','demucs'],['mdx','mdx']]) {
    const model = receipts.models[name];
    const bytes = fs.readFileSync(path.join(root, 'tools', 'stem-models', subdir, model.filename));
    if (bytes.length !== model.bytes || createHash('sha256').update(bytes).digest('hex') !== model.sha256) throw new Error('Damaged separation model: ' + name);
  }
  const { startServer } = await import('../../server.mjs');
  const app = await startServer({ port: 0, workerFactory: () => ({kick(){},stop(){}}) });
  try {
    const response = await fetch(app.address + '/api/state'); const state = await response.json();
    if (!response.ok || state.downloadsDir !== library || !state.ffmpeg) throw new Error('The service is not using the selected library and private FFmpeg.');
    checks.service = { library: state.downloadsDir, jobs: state.jobs.length };
  } finally { await new Promise(resolve => app.server.close(resolve)); }
  checks.native = await run(path.join(root, 'tools', 'ReleaseProbe.exe'));
  atomicJson(path.join(library, 'data', 'release-check.json'), { passed: true, version:'1.0.0', edition, ...checks });
  console.log(JSON.stringify(checks, null, 2));
}
