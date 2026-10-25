# DJStem 1.0.3

Improves scrolling during music playback and Auto play transitions, gives newly
downloaded songs manual queue priority, and makes the enabled Auto play button white.

- Playback display updates no longer wait behind audio decoding. Old audio readers
  close in the background, and unchanged or hidden waveforms avoid redundant redraws.
- Auto play and the Next button reuse unchanged replay-boundary plans instead of
  repeatedly searching the incoming song on the UI thread. Changed musical timing
  and seeks still select fresh plans.
- Newly completed downloads enter after the last manually queued song and before
  Auto play suggestions. Arrival order is preserved. Current playback and an active
  transition stay in place; a queued suggestion is promoted without a duplicate.
- The Auto play button is white when enabled in both the player and library views.

Both CPU and NVIDIA editions include these changes and the launcher/shutdown fixes
from 1.0.2. Bundled separation runtimes and models are unchanged.

## Validation

All 77 JavaScript tests pass, along with 222 queue, 153 library/browser, 154 native
player, 133 scrolling and 24 transition-scrolling checks. Both scrolling suites
passed their 60-second stress runs. The full autoplay suite passed 15,927 checks
and a further 30 seek checks; launcher regressions also passed.

Both assembled editions passed private-runtime isolation, model checksum,
selected-library service and native audio checks. The NVIDIA build detected the
CUDA provider and GPU on the test PC. Both complete installer archives passed
integrity checks. These checks do not establish Smart App Control acceptance.

## Updating

Close the player and drop box, install the matching edition over the existing app
folder, and keep/select your existing library. Music, stems, playlists, saved mixes
and preferences are preserved.

## Windows trust

The installers and first-party executables remain unsigned. Windows Application
Control has blocked some builds on the test PC. These playback and queue changes
do not resolve signing-policy blocks; Smart App Control compatibility remains
unverified. See [Windows trust status](CODE-SIGNING.md).
