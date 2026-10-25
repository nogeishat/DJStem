DJStem 1.0.3 — Windows 10/11, 64-bit

Windows trust notice: this release is unsigned. Smart App Control has blocked
setup and app executables on the test PC. The 1.0.3 playback and queue fixes do not resolve that
trust decision. Check the current release status before downloading:
https://github.com/nogeishat/DJStem/blob/main/docs/CODE-SIGNING.md

Open DJStem.exe to start the player. On first launch, choose a new library folder
or select an existing DJStem library. Songs (mp3/ and stems/), playlists, saved
mixes, preferences, download history and caches all stay inside that folder.
Keep this folder when updating the app or switching between CPU and NVIDIA editions.
Your application folder can be replaced without replacing your library.

The CPU edition separates songs on the CPU. The NVIDIA edition uses a supported
NVIDIA GPU when available and otherwise uses the CPU. Its Settings window also
offers CPU-only processing. A compatible NVIDIA graphics driver is required.
Both editions include the same separation models and all other features.
No Node.js, Python, Git, FFmpeg or Mixxx installation is needed.
.NET Framework 4.8 and Windows audio/media components are required. Windows N
editions may need the Media Feature Pack. CPU separation can take several minutes.

Click + in the player to open the link drop box. Drop or paste YouTube/SoundCloud
track links to download MP3s and create four stems. Downloading needs internet;
separation and playback work offline. Only download material you may use.

To change libraries, use the Start menu's Choose library folder shortcut or run
DJStem.exe --choose-library. The current folder location is remembered in
%LOCALAPPDATA%/DJStem/library-location.json; that file holds only a location pointer.
If the library is moved to another PC, choose Use an existing library there.
Saved song paths, including paths in playlists and saved mixes, are relocated.

Your library layout:
  mp3/                 Original recordings, grouped by artist
  stems/               Four stems, artwork and song metadata
  data/                Playlists, saved mixes, preferences, queue and caches
  matched-stems/       Generated playback audio
  settings.json        Storage and separation preferences
  djstem-library.json  Library format and relocation record

New libraries start with the release's default preferences. Existing preferences,
playlists and mixes are retained. No personal songs, playlists, mixes or download
history are included in this release.

Version 1.0.3 improves scrolling during playback, Auto play and Next transitions.
New completed downloads go after manual queue choices and before Auto play
suggestions. The Auto play button is white when enabled in both views.
The launcher fixes from 1.0.2 are included. Per-launch diagnostics are saved
inside your library under data/logs/. Failed window launches stop their background
service instead of leaving it running. Repeated launches focus the existing window.

The installer creates an optional desktop shortcut with the pink circle icon.
To uninstall, close the app, remove its application folder and shortcuts. Keep
your library folder to preserve your music and personal settings.

Source: https://github.com/nogeishat/DJStem
See LICENSE, THIRD-PARTY-NOTICES.md and third-party/ for dependency notices/source.
