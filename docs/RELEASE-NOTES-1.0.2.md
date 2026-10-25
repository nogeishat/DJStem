# DJStem 1.0.2

Fixes the `launcher-player.log is being used by another process` error when
starting DJStem or opening Add songs from an existing installation.

- Startup diagnostics can read the launch log while its writer is active, so
  the original startup failure is preserved. The 1.0.0 error could occur with
  just one launcher; it was not evidence of a damaged library or a new-user issue.
- Each launch uses its own log under the selected library's `data/logs/` folder.
  Old logs may remain in `data/`; they do not need to be deleted.
- Repeated opens focus the existing player or drop box. Add songs passes the
  player's current library explicitly.
- Failed launches and normal closes stop their owned downloader worker and
  release its local HTTP service. Unfinished HTTP requests cannot keep the
  service and launch log open after the drop box closes.
- Rebuilt installers refresh the included README, including its version and
  Windows trust notice.

Close the player and drop box, install the matching edition over the existing
application folder, and keep/select your existing library. No music, stems,
playlists, saved mixes or preferences need to be deleted or recreated.

Both CPU and NVIDIA editions remain self-contained. Their bundled separation
runtimes and models are unchanged from 1.0.0. Version 1.0.1 was withdrawn;
1.0.2 is a fresh release of the launcher fixes with additional shutdown checks.

## Windows trust limitation

These installers and first-party executables remain unsigned. On the test PC,
Windows Code Integrity event 3077 explicitly recorded the installed 1.0.0 drop
box being blocked by signing policy, matching its `spawn UNKNOWN` diagnostic.
The launcher bug previously obscured startup failures. This release fixes the
logging and cleanup defects; it does not resolve signing-policy rejection.
No Smart App Control compatibility claim is made. See [trust status](CODE-SIGNING.md).

## Validation

All 77 Node.js tests pass, including real failed/missing child executables,
normal shutdown, interruption, preserving a reused service, and an unfinished
HTTP response during shutdown. Native launcher regressions reproduce the
original self-conflicting log read and verify locked legacy logs, concurrent
launch logs, live diagnostic reads, rapid starts and mode selection.
All 153 native browser/settings checks and eight drop-format/polling checks pass.
Both assembled editions pass runtime/model/library/native-audio checks, and
both installer archives pass full integrity checks. The rebuilt drop box also
passes an interactive close/reopen/close check on a separate local test port.
