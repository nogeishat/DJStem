# DJStem

A Windows music player with YouTube/SoundCloud link downloads, four-stem separation, live stem mixing, tempo and key matching, playlists, saved mixes and auto-play.

## Download

**Smart App Control status: the current installers and app executables are unsigned. Windows has blocked them on the test PC; compatibility is not confirmed.** A minimal application change cannot guarantee acceptance. See [Windows trust status and the Microsoft file-review route](docs/CODE-SIGNING.md).

Latest available download: **1.0.2**, including the launcher and drop-box shutdown fixes. Unsigned downloads from [Releases](https://github.com/nogeishat/DJStem/releases/tag/v1.0.2):

- [CPU edition](https://github.com/nogeishat/DJStem/releases/download/v1.0.2/DJStem-1.0.2-Windows-x64-cpu-Setup.exe) — CPU stem separation.
- [NVIDIA edition](https://github.com/nogeishat/DJStem/releases/download/v1.0.2/DJStem-1.0.2-Windows-x64-nvidia-Setup.exe) — NVIDIA GPU acceleration with a CPU-only option.

Both installers include Node.js, Python, FFmpeg, yt-dlp, native audio libraries and both separation models. No additional setup downloads are required. Both editions otherwise have the same features and library format.

Requires 64-bit Windows 10/11, .NET Framework 4.8 and Windows audio/media components. The NVIDIA edition needs a GPU and driver compatible with CUDA 12.4/PyTorch 2.5.1; it falls back to CPU when CUDA is unavailable. Windows N may require Microsoft's Media Feature Pack.

Smart App Control can allow unsigned apps through Microsoft reputation, or apps signed by a trusted publisher. Neither Artifact Signing nor an open-source signing programme is mandatory. Microsoft file-review materials are prepared only; no review has been submitted and no signing-policy fix is claimed. Windows security policies are not changed by the app.

## Your library

On first launch choose **Create a new library** or **Use an existing library**. This folder holds your MP3s, stems, artwork, playlists, saved mixes, preferences, history and generated caches. New libraries use the release's defaults; existing preferences and contents are preserved.

Keep the entire library folder when updating DJStem or switching editions. A library copied to a different location can be reopened; saved song and mix paths are relocated automatically. Application files are separate from this folder.

To choose another library, use the Start menu shortcut or run `DJStem.exe --choose-library`. A location pointer is stored in `%LOCALAPPDATA%/DJStem/library-location.json`; all personal content and preferences stay in the selected library.

See [the user guide](docs/RELEASE-README.md) and [full feature documentation](docs/FEATURES.md). The feature document includes historical development paths; installed releases use your selected library instead.

## Development

Node.js 22+ is required for the JavaScript service and tests. Run `npm test`. Native compilation uses Windows' .NET Framework 4.8 compiler. Source launch scripts also need FFmpeg/ffprobe, a Python 3.10–3.12 base interpreter and Git; `npm run setup:stems` installs the pinned UVR engine and NVIDIA runtime. Native audio setup obtains Rubber Band DLLs from a local 64-bit Mixxx installation.

Release sources are in [scripts/release](scripts/release). The builder uses a configured local runtime and the official CPU wheels and LZMA SDK packaging tools in `tools/release-support`. Build with `scripts/release/build.ps1 -Edition cpu -Package` and `-Edition nvidia -Package`. Run the [Windows trust audit](docs/CODE-SIGNING.md) before release and verify every feature with Smart App Control enforced before claiming compatibility. Public installers contain prebuilt runtimes and never run Git, pip or a compiler on the user's PC.

## Licence and credits

DJStem source is licensed under [GPL-3.0-or-later](LICENSE). Third-party components retain their own licences; see [notices](THIRD-PARTY-NOTICES.md) and [source references](third-party/SOURCES.md). Credit to UVR's developers, the Demucs and MDX-Net authors, NAudio, Rubber Band and the original UI design reference.
