import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { initializeLibrary, configureRelease } from './library.mjs';
import { launchWindow } from './launch-process.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const mode = process.argv[2] || 'player';
try {
  if (!process.env.STEMKIT_USER_DATA) throw new Error('Start DJStem.exe to choose your library folder.');
  const library = initializeLibrary(root, process.env.STEMKIT_USER_DATA);
  const build = JSON.parse(fs.readFileSync(path.join(root, 'release-build.json'), 'utf8'));
  configureRelease(root, library, build.edition);
  if (mode === 'check') {
    const { verifyRelease } = await import('./verify.mjs');
    await verifyRelease(root, library, build.edition);
  } else if (mode === 'downloader') {
    const { startServer } = await import('../../server.mjs');
    const address = `http://127.0.0.1:${process.env.PORT}`;
    let app, existing;
    try { const response = await fetch(address + '/api/state', { signal: AbortSignal.timeout(1000) }); if (response.ok) existing = await response.json(); } catch { }
    if (existing && path.resolve(existing.downloadsDir).toLowerCase() !== library.toLowerCase()) throw new Error('Close the other DJStem downloader before opening a different library.');
    if (!existing) app = await startServer();
    await launchWindow({ executable: path.join(root, 'tools', 'StemKit.exe'), args: [app?.address || address, library], label: 'the link drop box', app });
  } else if (mode === 'player') {
    await launchWindow({ executable: path.join(root, 'tools', 'StemKitPlayer.exe'), args: [library], label: 'the player', stdio: 'inherit' });
  } else throw new Error('Unknown DJStem launch mode.');
} catch (error) { console.error(error.stack || error); process.exitCode = 1; }
