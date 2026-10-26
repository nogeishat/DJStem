# DJStem 1.0.4

Fixes an intermittent error when changing BPM or key after adding another song,
and makes the library layout more compact.

- Fixed the `IMFSourceReader` COM cast error (`E_NOINTERFACE`, `0x80004002`).
  The playback handoff now reads prepared compressed-audio decoders in the same
  COM apartment as background preparation and the audio callback. Musical
  position, crossfading and decoder ownership are preserved.
- Removed the library's "Your library" heading and top Auto play button.
  Now playing sits immediately before All songs, and navigation, search, counts
  and filters sit higher above the song, artist and playlist views.
- Removed the Sort label, centered the BPM range controls, and changed Reset,
  Grid and List to text controls without button outlines. The selected view
  remains indicated by its text color.
- Removed the solid background behind library cards and rows. The bottom
  Now Playing strip spans the window width and extends to the bottom edge,
  keeping its previous top position.

Both CPU and NVIDIA editions include these changes and all 1.0.3 fixes.
Bundled separation runtimes, models and the library format are unchanged.

## Validation

All 77 JavaScript tests passed, plus 154 native player, 26 stem-loading, 163
live tempo/key, 153 library/browser, 57 saved-mix and 140 playlist checks.
Launcher regression checks passed.

The decoder regression first reproduced the exact reported exception. It now
passes with a 48 kHz MP3 mixed at 44.1 kHz and an added stem, background warm-up,
UI-thread handoff, short and long catch-up, tempo changes, key changes, resets,
seeks and continued audio-callback reads. Grid, list, saved-mix and playlist
layouts were rendered and inspected.

Both assembled editions passed private-runtime, model checksum, selected-library
service and native audio checks. The NVIDIA package detected CUDA and the test
GPU. Both complete installer archives passed integrity checks; extracted version
metadata, updated sources and executable checksums were verified.

## Updating

Close the player and drop box, install the matching edition over the existing
application folder, and keep/select your existing library. Music, stems,
playlists, saved mixes and preferences are preserved.

## Windows trust

The installers and first-party executables remain unsigned. Smart App Control
compatibility remains unverified. See [Windows trust status](CODE-SIGNING.md).
