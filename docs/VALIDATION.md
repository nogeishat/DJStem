# DJStem 1.0.0 validation

## 1.0.1 launcher patch

Validated on 25 October 2026. The launcher regression reproduces the original
locked `launcher-player.log` and verifies concurrent launch logs, reading a log
while its writer is active, retaining the original startup error, rapid starts,
and downloader/player mode selection. All 74 Node.js tests and 153 native browser
checks passed. Real child-process failures release their owned service port.
The 1.0.0 separation runtimes and models are unchanged. The Windows signing-policy
block remains a separate limitation of these unsigned builds.

## Initial release

Validated on 25 October 2026 on 64-bit Windows, using fresh test library folders.
The original music library and its playlists/mixes were not included in the packages.

- 70 Node.js tests passed, including new-library defaults, preserving existing
  settings/playlists/mixes, relocating a moved library, and enforcing CPU-only mode.
- 154 native player checks and 153 browser/settings checks passed.
- 132 live tempo/key checks passed.
- Both assembled editions passed isolated interpreter, model checksum, dependency,
  local service, selected-library storage, native icon and audio processing checks.
- Both editions analyzed an eight-second synthetic MP3 and produced four valid
  44.1 kHz stereo stems using their own bundled runtimes and models.
- The CPU edition reported a CPU-only PyTorch build and no CUDA execution provider.
- The NVIDIA edition completed both Demucs and MDX separation using CUDA on an
  NVIDIA GeForce RTX 3050 Ti Laptop GPU.

The new installer and launcher are unsigned. This PC's Windows application-control
policy blocked their execution, so the interactive installer and first-run folder
chooser could not be exercised locally. The policy was not changed or bypassed.
The source compiled successfully, and the native player and dependency tests above
ran separately. Code signing is needed on PCs whose policies reject unsigned apps.
