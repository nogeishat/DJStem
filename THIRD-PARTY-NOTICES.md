# DJStem 1.0.0 distribution

See `third-party/SOURCES.md` and `dependency-manifest.json` in each installed
edition for bundled components, source locations, versions and checksums. The
application source is available under GPL version 3 or later in `LICENSE`.
Credit to Ultimate Vocal Remover's developers and the Demucs/MDX-Net model authors.

# UI design guide

The player's visual style and interactions were adapted from [danielravina/stemkit](https://github.com/danielravina/stemkit), commit `0be7d6b02bc03cd155204cc5da7585612faac327`.
The native player code is implemented here; the reference's downloader, separator, Electron runtime, and telemetry are not used.

The reference is distributed under the following license:

MIT License

Copyright (c) 2026 Daniel Ravina

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.


# Live audio processing

The live tempo/key implementation uses the C API of [Rubber Band Library](https://github.com/breakfastquay/rubberband) (Copyright Particular Programs Ltd), following the buffer-processing approach of [Mixxx's Rubber Band scaler](https://github.com/mixxxdj/mixxx/blob/main/src/engine/bufferscalers/enginebufferscalerubberband.cpp). The integration here is written for StemKit; Mixxx's application code is not bundled.

Player setup reuses `rubberband-3.dll` and its SLEEF, libsamplerate, and Microsoft Visual C++ runtime dependencies from the local 64-bit Mixxx installation. Rubber Band is available under GPL version 2 or later, or a commercial license from its authors. The installation's supplied notices are retained as `tools/Mixxx-COPYING.txt` and `tools/Mixxx-LICENSE.txt`; binary source locations and SHA-256 hashes are recorded in `tools/player-packages/realtime-receipts.json`. Library source is available from the [Rubber Band project](https://github.com/breakfastquay/rubberband), [SLEEF project](https://github.com/shibatch/sleef), and [libsamplerate project](https://github.com/libsndfile/libsamplerate).
