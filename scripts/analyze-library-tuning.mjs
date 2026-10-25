import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { libraryRoot } from '../lib/library.mjs';
import { MusicAnalyzer } from '../lib/analysis.mjs';
import { hasTuningAnalysis, tuningFields, writeMetadata } from '../lib/metadata.mjs';
import { metadataSongs } from './update-song-metadata.mjs';

const ROOT = path.dirname(path.dirname(fileURLToPath(import.meta.url)));

export async function analyzeLibraryTuning({ root = ROOT, library = libraryRoot(root), apply = false, force = false,
  analyzer = new MusicAnalyzer({ root }), log = console.log } = {}) {
  const songs = metadataSongs(library).filter(song => force || !hasTuningAnalysis(song.metadata));
  let failed = 0;
  for (const [index, song] of songs.entries()) {
    if (!apply) { log(`Would estimate tuning: ${song.audio}`); continue; }
    try {
      log(`[${index + 1}/${songs.length}] Estimating tuning: ${song.audio}`);
      const tuning = tuningFields(await analyzer.run(song.audio, { tuningOnly: true }));
      if (!fs.readFileSync(song.file).equals(song.original)) throw new Error('Metadata changed during analysis; rerun when this song is idle.');
      const backup = path.join(root, 'data', 'metadata-backups');
      fs.mkdirSync(backup, { recursive: true });
      const backupFile = path.join(backup, createHash('sha256').update(song.original).digest('hex') + '.json');
      if (!fs.existsSync(backupFile)) fs.writeFileSync(backupFile, song.original, { flag: 'wx' });
      writeMetadata(song.file, { ...song.metadata, tuningHz: tuning.tuningHz, analysis: { ...song.metadata.analysis, ...tuning.analysis } });
      log(`Saved A4: ${tuning.tuningHz === null ? 'unknown (no consistent tuning)' : `${tuning.tuningHz.toFixed(3)} Hz`}`);
    } catch (error) { failed++; log(`Failed: ${song.audio}: ${error.message}`); }
  }
  return { songs: songs.length, failed };
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const result = await analyzeLibraryTuning({ apply: process.argv.includes('--apply'), force: process.argv.includes('--force') });
    console.log(`${process.argv.includes('--apply') ? 'Analyzed' : 'Would analyze'} ${result.songs} songs; ${result.failed} failed.`);
    if (result.failed) process.exitCode = 1;
  } catch (error) { console.error(error.message); process.exitCode = 1; }
}
