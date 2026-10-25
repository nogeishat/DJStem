STEMKIT USB EDITION

Copy this ENTIRE folder to a USB drive. Keep its files and subfolders together.
Open Start-Player.exe to play music, or Start-StemKit.exe to download and separate.
Check-USB.exe checks this copy and saves its report to data/usb-check.json.
There is no installer. Administrator access, Node.js, Python, FFmpeg, Git and Mixxx
installations are not needed. Node, standalone Python, FFmpeg, yt-dlp, the player
libraries and both stem models are included.

This is an app that starts FROM USB while Windows is running. It does not boot
the computer into an operating system, and inserting the drive does not auto-run
the app. Open one of the launchers above.

PC REQUIREMENTS
64-bit Windows 10/11, .NET Framework 4.8 or newer, Windows audio/media components
and a writable USB drive. Managed PCs may block programs run from removable media.
An NVIDIA GPU is optional: separation falls back to CPU, which can be much slower.
Playback works offline. Downloading YouTube/SoundCloud tracks needs internet.
Windows N editions may need the Media Feature Pack to decode MP3s in the player.

FILES AND DRIVE LETTERS
library/mp3       - your copied MP3 collection, grouped by artist
library/stems     - your copied stems, artwork and song metadata
library/matched-stems - generated playback cache, recreated when needed
data              - independent queue, playlists, saved mixes, settings and logs
tools             - bundled runtimes, libraries, models and source dependencies

Your original project and music library are separate from this copy. Changes here
stay here. On each launch, saved paths are adjusted to the current folder and
drive letter. Start through the supplied launchers so relocation happens first.
The first pre-move settings snapshot is retained in data/*.before-portable-move.
Stem models and required packages are already included; there is no first-run
setup download. Generated caches were omitted because the app can recreate them.

The downloader uses local port 4318, separate from the original app on port 4317.
Close the USB copy before starting another copy of this USB edition on the same PC.

USB STORAGE
Use a USB 3 drive or USB SSD. The supplied music and runtime need about 24 GB;
allow extra free space for caches, downloads and processing. A 64 GB or larger
drive gives useful working space. Existing USB files do not need to be erased.
Use exFAT or NTFS when preparing a drive; do not format a drive containing files
you need. Close BOTH StemKit windows and wait for processing to stop before using
Windows' Safely Remove Hardware option. Do not unplug during a download or save.

TROUBLESHOOTING
Run Check-USB.exe after copying. Startup errors are saved in data/launch-error.log
and data/launcher-player.log or data/launcher-downloader.log.
If copying was interrupted, copy the complete folder again before launching.
If port 4318 is already occupied, close the other StemKit USB copy.
Some saved mixes in the original settings may already refer to deleted/renamed
songs; moving the app cannot restore missing original files.

SOURCE AND NOTICES
Application sources are in desktop/, lib/ and scripts/. Third-party notices and
licenses are retained in THIRD-PARTY-NOTICES.md, tools/, the Python package license
directories, and tools/uvr/. FFmpeg's license is tools/FFmpeg-LICENSE.txt. The
bundled Node notices are included in tools/Node-LICENSE.txt.
docs/README-original.md describes the original project's features and development
commands; its installation instructions and old drive paths do not apply here.
