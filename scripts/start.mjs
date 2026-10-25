import fs from 'node:fs';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { setup } from './setup.mjs';
import { buildDesktop } from './build-desktop.mjs';
import { setupStems } from './setup-stems.mjs';
import { ROOT, startServer } from '../server.mjs';
import { libraryRoot } from '../lib/library.mjs';
import { buildPlayer } from './build-player.mjs';

let app;
let desktop;
let closing = false;
const stop = () => {
  if (closing) return;
  closing = true;
  app?.worker.stop(); app?.server.close();
  desktop?.kill();
  setTimeout(() => process.exit(0), 1500).unref();
};

try {
  const executable = buildDesktop();
  await setup();
  await setupStems();
  await buildPlayer();
  const port = Number(process.env.PORT || 4317);
  let address = `http://127.0.0.1:${port}`;
  let existing = null;
  try {
    const response = await fetch(address + '/api/state', { signal: AbortSignal.timeout(1500) });
    if (response.ok) existing = await response.json();
  } catch { /* A new service will be started. */ }
  if (existing && existing.outputMode !== 'mp3') throw new Error('Close the old StemKit video downloader before starting the MP3 drop box.');
  if (existing && !existing.supportedSources?.includes('soundcloud')) throw new Error('Close the previous StemKit app and restart it to enable SoundCloud downloads.');
  if (existing && !existing.stemPipeline) throw new Error('Close the previous StemKit app and restart it to enable automatic stems.');
  if (existing && !existing.musicAnalysis) throw new Error('Close the previous StemKit app and restart it to enable automatic BPM/key analysis.');
  if (existing && (existing.libraryLayout !== 2 || path.resolve(existing.downloadsDir) !== libraryRoot(ROOT))) throw new Error('Close the previous StemKit app and restart it to use the new library layout.');
  if (!existing) { app = await startServer({ port }); address = app.address; }
  desktop = spawn(executable, [address, app?.downloadsDir || existing.downloadsDir], { windowsHide: false, stdio: 'ignore' });
  desktop.once('error', error => { console.error(error.message); stop(); });
  desktop.once('exit', stop);
  process.once('SIGINT', stop); process.once('SIGTERM', stop);
} catch (error) {
  fs.mkdirSync(path.join(ROOT, 'data'), { recursive: true });
  fs.writeFileSync(path.join(ROOT, 'data', 'launch-error.log'), `${new Date().toISOString()}\n${error.stack || error.message}\n`);
  console.error(error.message);
  try { spawn(buildDesktop(), ['--error', error.message], { windowsHide: false, detached: true, stdio: 'ignore' }).unref(); } catch { }
  app?.worker.stop(); app?.server.close(); process.exitCode = 1;
}
