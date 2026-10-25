import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { ROOT, setupPlayer, PLAYER_DLLS } from './setup-player.mjs';

export function playerReferences() {
  const gac = path.join(process.env.WINDIR || 'C:\\Windows', 'Microsoft.NET', 'assembly', 'GAC_MSIL', 'netstandard');
  const standard = fs.readdirSync(gac).map(name => path.join(gac, name, 'netstandard.dll')).find(file => fs.existsSync(file));
  if (!standard) throw new Error('The music player needs .NET Framework 4.8.');
  return [standard, ...PLAYER_DLLS.map(name => path.join(ROOT, 'tools', name))];
}

export const PLAYER_SOURCES = ['AssemblyInfo.cs', 'PlayerData.cs', 'PlayerAudio.cs', 'PlayerRealtime.cs', 'PlayerLoudness.cs', 'PlayerStemTiming.cs', 'PlayerStemGestures.cs', 'PlayerMix.cs', 'PlayerLibrary.cs', 'PlayerLibraryFilters.cs', 'PlayerWaveforms.cs', 'PlayerMatching.cs', 'PlayerMatchedCache.cs', 'PlayerStemSources.cs', 'PlayerSwaps.cs', 'PlayerStemLoading.cs', 'PlayerSongColors.cs', 'PlayerSongHeader.cs', 'PlayerDragDrop.cs', 'PlayerControls.cs', 'PlayerCamelot.cs', 'PlayerTuning.cs', 'PlayerAutoPlan.cs', 'PlayerAutoDynamics.cs', 'PlayerPlayback.cs', 'PlayerQueue.cs', 'PlayerAutoPlay.cs', 'PlayerAutoMarkers.cs', 'PlayerAutoPreload.cs', 'PlayerAutoRestart.cs', 'PlayerAutoReplay.cs', 'PlayerSettings.cs', 'PlayerArtists.cs', 'PlayerBrowser.cs', 'PlayerSavedMixModel.cs', 'PlayerSavedMixes.cs', 'PlayerPlaylists.cs', 'PlayerPlaylistAudio.cs', 'PlayerExport.cs', 'PlayerMp3Export.cs', 'PlayerSongDeletion.cs', 'Player.cs'];

export async function buildPlayer({ output = path.join(ROOT, 'tools', 'StemKitPlayer.exe') } = {}) {
  await setupPlayer();
  const sources = PLAYER_SOURCES.map(name => path.join(ROOT, 'desktop', name));
  const icon = path.join(ROOT, 'assets', 'app.ico');
  const target = output;
  if (fs.existsSync(target) && sources.every(file => fs.statSync(target).mtimeMs >= fs.statSync(file).mtimeMs)) return target;
  const compiler = path.join(process.env.WINDIR || 'C:\\Windows', 'Microsoft.NET', 'Framework64', 'v4.0.30319', 'csc.exe');
  const result = spawnSync(compiler, ['/nologo', '/target:winexe', '/optimize+', '/platform:x64', `/out:${target}`,
    '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll', '/reference:System.Web.Extensions.dll',
    ...(fs.existsSync(icon) ? ['/win32icon:' + icon] : []), ...playerReferences().map(file => '/reference:' + file), ...sources], { encoding: 'utf8', windowsHide: true });
  if (result.status !== 0) throw new Error(`Player build failed: ${result.stdout || result.stderr || result.error?.message}`);
  return target;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try { console.log(await buildPlayer()); } catch (error) { console.error(error.message); process.exitCode = 1; }
}
