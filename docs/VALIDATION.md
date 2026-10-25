# DJStem validation

## 1.0.3 playback, transition scrolling and queue priority

Validated on 25 October 2026. The final source passed all 77 JavaScript tests,
222 native queue checks, 153 library/browser checks, 154 native player checks,
133 scrolling checks and 24 transition-scrolling checks. Both scrolling suites
completed their 60-second runs, including the active transition-start branch
with incoming replay analysis populated for automatic and Next-triggered handoffs.
The full autoplay test binary passed 15,927 checks on a direct retry, followed
by 30 seek checks. The initial test launch was blocked by Windows Application
Control; no security settings were changed. Launcher regressions also passed.

CPU and NVIDIA releases passed the bundled-runtime checks, model checksums,
selected-library service test, native player construction and live audio DLL
checks. CPU runtime isolation and the absence of GPU providers were verified;
the NVIDIA build exposed CUDA and detected the test GPU. Both full installer
archives passed integrity checks. Version metadata is 1.0.3 throughout the
application, launcher, installer, dependency manifests and release README.

The first-party binaries and installers remain unsigned. Signature/hash audits
record Smart App Control compatibility as unverified. Previous local playback
checks exercised the same fixes through a real Next handoff with isolated test
preferences; neither those checks nor these release checks establish acceptance
of downloaded installers under enforcement.

## 1.0.2 launcher and shutdown patch

Validated on 25 October 2026. All 77 Node.js tests pass. Added real missing-child
and interrupted-child checks and an open HTTP response at window shutdown;
the owned service closes and releases its port. Native launcher regressions
also reproduce 1.0.0's self-conflicting log read without another DJStem process.
The old player log was unlocked with the visible app closed, but a hidden legacy
launcher/service was later found still serving the original library. Windows
denied access to that process's executable details and termination. In 1.0.0,
a synchronous spawn failure occurs before the shutdown handler is registered,
leaving the downloader alive. The corrected launch path always stops its worker
and service, including that failure case.

The installed downloader's `spawn UNKNOWN` log corresponds to Windows Code
Integrity event 3077 for `tools/StemKit.exe`: Windows rejected its signing level.
This is separate from the corrected log-sharing and shutdown bugs. These builds
remain unsigned; acceptance under Smart App Control is unverified.

Both assembled 1.0.2 editions passed private-runtime isolation, model checksums,
selected-library service and native audio/decoder/icon verification. All 153
native browser/settings checks and eight drop-format/polling checks passed.
Both final installer archives passed full integrity checks. The rebuilt player
opened and Add songs correctly passed the selected library and reported an
existing-service conflict. The rebuilt drop box opened, closed, reopened using
the same isolated library and closed again; each owned service shut down. This
window check used separate local ports because the legacy service was still
answering on the normal release port. It does not establish acceptance of the
downloaded installers under Smart App Control.

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
