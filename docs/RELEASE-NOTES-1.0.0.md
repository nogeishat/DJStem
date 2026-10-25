# DJStem 1.0.0

Two self-contained installers for 64-bit Windows 10/11:

- **CPU**: stem separation on your CPU.
- **NVIDIA**: accelerated separation on a compatible NVIDIA GPU, plus a CPU-only
  option in Settings and automatic CPU fallback when CUDA is unavailable.

Both editions include the same player, downloader, models and features. Each is
one download containing Node.js, Python, FFmpeg, yt-dlp, audio libraries, all Python
dependencies and both separation models. No additional setup downloads are needed.

On first launch, create a library folder wherever you choose or select an existing
DJStem library. MP3s, stems, artwork, playlists, saved mixes, preferences, history
and caches stay together in that folder. Keep it when updating or changing editions.
Existing settings and content are retained; new libraries use the release defaults.
The app and its shortcuts use a #F49BBF circle icon.

Requires .NET Framework 4.8 and Windows audio/media components. Windows N may need
the Media Feature Pack. NVIDIA acceleration requires hardware and a driver compatible
with the included CUDA 12.4/PyTorch 2.5.1 runtime. CPU processing is slower.

These installers are **unsigned**. This PC's application-control policy prevented
interactive installer/launcher testing; dependency, native player, library migration
and real CPU/GPU separation tests passed. See [validation](https://github.com/nogeishat/DJStem/blob/main/docs/VALIDATION.md).

Application source: GPL-3.0-or-later. Third-party components retain their licences;
notices, source references and dependency/model checksums are included in each edition.
