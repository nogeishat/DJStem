# Third-party components in DJStem 1.0.0

DJStem source is provided under GNU GPL version 3 or later; see LICENSE. Individual
third-party components retain their own licences. Installed Python distributions
include their upstream licence files in Lib/site-packages/*.dist-info and package
directories. NVIDIA runtime components are included only in the NVIDIA edition,
with the notices supplied by PyTorch. They require a compatible NVIDIA driver.

| Component | Version / source |
| --- | --- |
| Ultimate Vocal Remover engine | [5517e0cf0d1acd16a1618eeedec596957523f9e1](https://github.com/Anjok07/ultimatevocalremovergui/tree/5517e0cf0d1acd16a1618eeedec596957523f9e1); source included in tools/uvr; MIT licence declared by its README |
| Demucs htdemucs model | [Demucs](https://github.com/facebookresearch/demucs), model 955717e8-8726e21a.th; MIT, licence included here |
| UVR-MDX-NET Inst HQ 5 | [UVR model repository](https://github.com/TRvlvr/model_repo/releases/tag/all_public_uvr_models); credit to UVR's developers and model authors; see UVR-NOTICE.txt |
| Python | 3.12, [CPython source](https://github.com/python/cpython/tree/3.12); interpreter licence included in tools/stem-runtime |
| PyTorch / torchvision / torchaudio | 2.5.1 / 0.20.1 / 2.5.1; [PyTorch source](https://github.com/pytorch/pytorch/tree/v2.5.1), [vision](https://github.com/pytorch/vision/tree/v0.20.1), [audio](https://github.com/pytorch/audio/tree/v2.5.1) |
| ONNX Runtime | CPU 1.20.1 / GPU 1.20.2; [source](https://github.com/microsoft/onnxruntime), MIT notices in the runtime |
| Node.js | 24.20.0; [source](https://github.com/nodejs/node/tree/v24.20.0), tools/Node-LICENSE.txt |
| yt-dlp | Exact version in dependency-manifest.json; [source and corresponding release](https://github.com/yt-dlp/yt-dlp/releases); its executable contains GPLv3+ components; THIRD_PARTY_LICENSES.txt included here |
| FFmpeg / ffprobe | Gyan 9.0.2 full build, GPLv3; [corresponding source](https://github.com/FFmpeg/FFmpeg/commit/946fcce07b), [build configuration and dependency list](https://www.gyan.dev/ffmpeg/builds/); tools/FFmpeg-LICENSE.txt |
| Rubber Band and native audio dependencies | Binaries from Mixxx 2.6-beta; [Rubber Band source](https://github.com/breakfastquay/rubberband), [SLEEF](https://github.com/shibatch/sleef), [libsamplerate](https://github.com/libsndfile/libsamplerate), [Mixxx build environment](https://github.com/mixxxdj/buildserver); tools/Mixxx-COPYING.txt and tools/Mixxx-LICENSE.txt |
| NAudio | 2.2.1; [source](https://github.com/naudio/NAudio/tree/v2.2.1), MIT; tools/NAudio-LICENSE.txt |
| Self-extracting installer module | LZMA SDK 26.05, public domain; [source and SDK](https://github.com/ip7z/7zip/releases/tag/26.05) |

The native library and executable SHA-256 hashes, actual interpreter versions,
model hashes and Python dependency inventory are recorded with each edition in
dependency-manifest.json. No third-party ownership is claimed by DJStem.
