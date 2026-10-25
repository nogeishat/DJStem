# DJStem 1.0.1

Fixes the launcher error saying `launcher-player.log` is being used by another
process when opening the player or link drop box.

- The startup error handler can read its log while the writer is open, so a log
  sharing error no longer hides the actual startup failure.
- Each launch gets a separate log in the selected library's `data/logs/` folder.
- Repeated launches focus the existing window instead of reopening its log or
  starting another service.
- Add songs explicitly selects the downloader and the player's current library.
- A failed or blocked window launch stops its worker and closes the service port.

CPU-only and NVIDIA/CPU editions remain self-contained, with the same bundled
runtimes and models as 1.0.0. Close DJStem before installing the update into the
same application folder, then keep/select your existing library. Songs, stems,
playlists, mixes and preferences do not need to be moved or recreated.

The builds remain unsigned. A Windows application-control policy that blocks
unsigned DJStem executables still requires a trusted code-signing certificate.
This patch reports that startup failure correctly; it does not change Windows
security policies.

Validation: launcher regression tests cover a locked legacy log, concurrent logs,
error-log reads while writing, rapid repeated starts and mode selection. All 74
Node.js tests and 153 native browser/settings checks passed, including real child
process failures releasing the local service port.
