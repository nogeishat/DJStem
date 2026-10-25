import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { spawn } from 'node:child_process';
import { atomicJson } from './state.mjs';

export function run(command, args = [], timeout = 120000) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { windowsHide: true, shell: false, stdio: ['ignore', 'pipe', 'pipe'], timeout });
    let output = '', errors = '';
    child.stdout.on('data', bytes => { output = (output + bytes).slice(-100000); });
    child.stderr.on('data', bytes => { errors = (errors + bytes).slice(-10000); });
    child.once('error', reject);
    child.once('close', code => code === 0 ? resolve(output.trim()) : reject(new Error(`${path.basename(command)} failed (${code}): ${errors || output}`)));
  });
}

export function requiredFiles(root) {
  for (const file of ['tools/node.exe', 'tools/ffmpeg.exe', 'tools/ffprobe.exe', 'tools/yt-dlp.exe', 'tools/StemKit.exe', 'tools/StemKitPlayer.exe', 'tools/stem-runtime/python.exe', 'tools/stem-runtime/python312.dll', 'tools/rubberband-3.dll', 'tools/NAudio.Core.dll', 'tools/NAudio.WinMM.dll', 'tools/NAudio.Wasapi.dll', 'tools/uvr/separate.py', 'tools/stem-models/demucs/htdemucs.yaml', 'tools/stem-models/mdx/config.json']) {
    if (!fs.existsSync(path.join(root, file))) throw new Error(`The USB copy is incomplete: ${file} is missing. Copy the entire StemKit-USB folder again.`);
  }
}

export async function verifyStems(root) {
  requiredFiles(root);
  const output = await run(process.env.STEMKIT_PYTHON, ['-u', path.join(root, 'scripts', 'separate-stems.py'), '--check']);
  return JSON.parse(output.split(/\r?\n/).at(-1));
}

export async function verify(root) {
  requiredFiles(root);
  const checks = {};
  // A host with Python/FFmpeg already installed must not mask missing USB files.
  process.env.PATH = [path.join(root, 'tools'), path.join(root, 'tools', 'stem-runtime'), path.join(process.env.SystemRoot || 'C:\\Windows', 'System32')].join(path.delimiter);
  for (const [name, args] of [['node', ['--version']], ['ffmpeg', ['-version']], ['ffprobe', ['-version']], ['yt-dlp', ['--version']]]) {
    checks[name] = (await run(path.join(root, 'tools', name + '.exe'), args)).split(/\r?\n/)[0];
    console.log(name + ': ' + checks[name]);
  }
  checks.python = JSON.parse(await run(process.env.STEMKIT_PYTHON, ['-c', 'import json,sys,ssl,sqlite3,ctypes,librosa,numpy,soundfile; print(json.dumps({"executable":sys.executable,"prefix":sys.prefix,"basePrefix":sys.base_prefix,"version":sys.version,"librosa":librosa.__version__}))']));
  if (path.resolve(checks.python.prefix).toLowerCase() !== path.resolve(root, 'tools', 'stem-runtime').toLowerCase() || checks.python.prefix !== checks.python.basePrefix) throw new Error('Python is using a host installation instead of the portable runtime.');
  console.log('Standalone Python and music analysis imports passed.');
  checks.stems = await verifyStems(root);
  const receipts = JSON.parse(fs.readFileSync(path.join(root, 'tools', 'stem-models', 'model-receipts.json'), 'utf8'));
  for (const [name, subdir] of [['htdemucs', 'demucs'], ['mdx', 'mdx']]) {
    const model = receipts.models[name];
    const bytes = fs.readFileSync(path.join(root, 'tools', 'stem-models', subdir, model.filename));
    if (bytes.length !== model.bytes || createHash('sha256').update(bytes).digest('hex') !== model.sha256) throw new Error(`The ${name} model is incomplete or damaged.`);
  }
  console.log('UVR imports and both model checksums passed.');
  checks.player = await run(path.join(root, 'tools', 'PortableProbe.exe'));
  console.log(checks.player);
  const { startServer } = await import('../../server.mjs');
  const app = await startServer({ port: 0, workerFactory: () => ({ kick() {}, stop() {} }) });
  try {
    const response = await fetch(app.address + '/api/state');
    const state = await response.json();
    if (!response.ok || state.downloadsDir !== path.join(root, 'library') || !state.ffmpeg) throw new Error('The USB service is not using the portable library and FFmpeg.');
    checks.service = { library: state.downloadsDir, jobs: state.jobs.length, ffmpeg: state.ffmpeg };
  } finally { await new Promise(resolve => app.server.close(resolve)); }
  checks.library = { songs: 0, bytes: 0 };
  for (const artist of fs.readdirSync(path.join(root, 'library', 'mp3'), { withFileTypes: true })) {
    if (!artist.isDirectory()) continue;
    for (const name of fs.readdirSync(path.join(root, 'library', 'mp3', artist.name))) if (name.endsWith('.mp3')) {
      checks.library.songs++;
      checks.library.bytes += fs.statSync(path.join(root, 'library', 'mp3', artist.name, name)).size;
    }
  }
  atomicJson(path.join(root, 'data', 'usb-check.json'), { passed: true, testedAt: new Date().toISOString(), root, ...checks });
  console.log('USB checks passed. Report: data/usb-check.json');
}
