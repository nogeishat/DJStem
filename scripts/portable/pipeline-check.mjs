import fs from 'node:fs';
import path from 'node:path';
import { configureEnvironment, relocateState, atomicJson } from './state.mjs';
import { ROOT } from './boot.mjs';
import { run } from './verify.mjs';

configureEnvironment(ROOT);
relocateState(ROOT);
process.env.PATH = [path.join(ROOT, 'tools'), path.join(ROOT, 'tools', 'stem-runtime'), path.join(process.env.SystemRoot, 'System32')].join(path.delimiter);
const folder = fs.mkdtempSync(path.join(ROOT, 'data', 'pipeline-test-'));
try {
  const mp3 = path.join(folder, 'test.mp3');
  console.log('Generating a three-second stereo MP3 with bundled FFmpeg.');
  await run(process.env.STEMKIT_FFMPEG, ['-hide_banner', '-loglevel', 'error', '-f', 'lavfi', '-i', 'sine=frequency=440:duration=3', '-ar', '44100', '-ac', '2', '-q:a', '0', mp3]);
  console.log('Analyzing it with standalone Python.');
  await run(process.env.STEMKIT_PYTHON, ['-u', path.join(ROOT, 'scripts', 'analyze-music.py'), '--input', mp3, '--output', path.join(folder, 'analysis.json')], 180000);
  console.log('Running both bundled UVR models on CPU.');
  const stems = path.join(folder, 'stems');
  await run(process.env.STEMKIT_PYTHON, ['-u', path.join(ROOT, 'scripts', 'separate-stems.py'), '--input', mp3, '--output', stems, '--device', 'cpu'], 240000);
  const record = JSON.parse(fs.readFileSync(path.join(stems, 'separation.json'), 'utf8'));
  if (record.frames !== 132300 || Object.values(record.devices).some(device => device !== 'cpu')) throw new Error('Unexpected CPU stem output.');
  const outputs = {};
  for (const stem of ['drums', 'bass', 'other', 'vocals']) {
    const info = JSON.parse(await run(process.env.STEMKIT_FFPROBE, ['-v', 'error', '-show_streams', '-of', 'json', path.join(stems, stem + '.wav')]));
    const stream = info.streams[0];
    if (stream.sample_rate !== '44100' || stream.channels !== 2 || stream.bits_per_sample !== 24 || Math.abs(Number(stream.duration) - 3) > 0.001) throw new Error(`Invalid ${stem} output.`);
    outputs[stem] = { sampleRate: 44100, channels: 2, bits: 24, seconds: Number(stream.duration) };
  }
  atomicJson(path.join(ROOT, 'data', 'pipeline-check.json'), { passed: true, testedAt: new Date().toISOString(), root: ROOT, device: 'cpu', analysis: JSON.parse(fs.readFileSync(path.join(folder, 'analysis.json'), 'utf8')), outputs });
  console.log('CPU pipeline passed: MP3 creation, music analysis, and all four stereo 24-bit stems.');
} finally {
  if (path.dirname(folder) !== path.join(ROOT, 'data')) throw new Error('Unexpected test folder.');
  fs.rmSync(folder, { recursive: true, force: true });
}
