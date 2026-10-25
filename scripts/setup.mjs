import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { findDownloader, toolVersion } from '../lib/downloader.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

export async function setup(update = false) {
  const current = toolVersion(findDownloader(root));
  if (current && !update) return current;
  if (process.platform !== 'win32') throw new Error('Install yt-dlp from https://github.com/yt-dlp/yt-dlp and add it to PATH, or set YTDLP_PATH to its executable.');
  const asset = process.arch === 'arm64' ? 'yt-dlp_arm64.exe' : process.arch === 'ia32' ? 'yt-dlp_x86.exe' : 'yt-dlp.exe';
  // Fetch the checksum from the same concrete release, so a new release cannot race the two downloads.
  console.log('Installing the official yt-dlp Windows executable into tools/…');
  const releaseResponse = await fetch('https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest', { headers: { 'User-Agent': 'StemKit' } });
  if (!releaseResponse.ok) throw new Error(`GitHub release lookup failed (${releaseResponse.status}).`);
  const release = await releaseResponse.json();
  const base = `https://github.com/yt-dlp/yt-dlp/releases/download/${encodeURIComponent(release.tag_name)}`;
  const checksumResponse = await fetch(`${base}/SHA2-256SUMS`);
  if (!checksumResponse.ok) throw new Error(`Checksum download failed (${checksumResponse.status}).`);
  const checksums = await checksumResponse.text();
  const expected = checksums.split(/\r?\n/).map(line => line.trim().split(/\s+/)).find(parts => parts[1]?.replace(/^\*/, '') === asset)?.[0];
  if (!expected || !/^[a-f0-9]{64}$/i.test(expected)) throw new Error('No valid checksum found for the yt-dlp executable.');
  const binaryResponse = await fetch(`${base}/${asset}`);
  if (!binaryResponse.ok) throw new Error(`yt-dlp download failed (${binaryResponse.status}).`);
  const binary = Buffer.from(await binaryResponse.arrayBuffer());
  const actual = createHash('sha256').update(binary).digest('hex');
  if (actual.toLowerCase() !== expected.toLowerCase()) throw new Error('yt-dlp checksum mismatch; the executable was not installed.');
  const target = path.join(root, 'tools', 'yt-dlp.exe');
  fs.mkdirSync(path.dirname(target), { recursive: true });
  const temporary = `${target}.installing.exe`;
  let version;
  try {
    fs.writeFileSync(temporary, binary);
    version = toolVersion(temporary);
    if (!version) throw new Error('The downloaded yt-dlp executable could not start; the existing downloader was kept.');
    fs.renameSync(temporary, target);
  } finally { if (fs.existsSync(temporary)) fs.unlinkSync(temporary); }
  console.log(`yt-dlp ${version} installed and verified.`);
  return version;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try { await setup(process.argv.includes('--update')); }
  catch (error) { console.error(`Setup failed: ${error.message}`); process.exitCode = 1; }
}
