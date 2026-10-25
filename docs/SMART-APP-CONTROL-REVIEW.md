# Historical Smart App Control review — DJStem 1.0.1

**Version 1.0.1 has been withdrawn from GitHub. Its release, downloads and tag have been removed. This document records previously prepared evidence only; the draft below is not a current submission.**

Status: prepared on 2026-10-25; the maintainer chose preparation only. Nothing has
been submitted or approved. No signing-service
application is in progress. The application and released binaries have not changed.

The setup, player and link drop box have been blocked on the test PC. Windows
CodeIntegrity event 3077 recorded the downloaded 1.0.1 NVIDIA setup being blocked.
The files are unsigned; the evidence does not identify a particular app feature
as the cause. The log-sharing error fixed in 1.0.1 is a separate startup problem.

## Audit results

| Edition | Native files in app | NotSigned | Valid on audit PC |
| --- | ---: | ---: | ---: |
| CPU | 388 | 321 | 67 |
| NVIDIA | 415 | 337 | 78 |


Each outer installer and all five first-party executables report `NotSigned`.
The inventory includes upstream helpers, DLLs and Python extensions. Some runtimes
are already signed; unsigned dependencies must also be considered. These counts
are signature observations, not malware scan results or an acceptance prediction.

## Released file identifiers

These exact files were extracted from the existing release archives and matched
to the staging files by SHA-256. No executables were rebuilt for review.

| Edition | Released path | Bytes | SHA-256 |
| --- | --- | ---: | --- |
| CPU | `DJStem-1.0.1-Windows-x64-cpu-Setup.exe` | 456860074 | `b3d647e50140af83752b461370f2542e52dfe2e00c412ec643279bca59deab59` |
| CPU | `Setup.exe` | 30720 | `c31625a6754d02ff77725dd69caed2f6745637ca9b9b6adc05b06c0a928e248f` |
| CPU | `app/DJStem.exe` | 31232 | `10ba2893e3191e7e147965b1b0ae1a6128a6e19feb9bc623cd397fbb75884bf3` |
| CPU | `app/tools/StemKit.exe` | 44032 | `6c5bf27af702d0e43016b1063c621d52eff4e7352934d128e8b84022e22d3339` |
| CPU | `app/tools/StemKitPlayer.exe` | 482304 | `70aaaf0303e19a0d1791570c3251dfe864e7b0fb93f724aa136fa44343f2e6b6` |
| CPU | `app/tools/ReleaseProbe.exe` | 466432 | `34253d29a992d49b8a74285d50747d9192cdce7949cdc7a1d94d616d62a7058b` |
| NVIDIA | `DJStem-1.0.1-Windows-x64-nvidia-Setup.exe` | 1850876380 | `afb855403e74d78ebc5f3744fdd4482cd937e6e90206f8c49d8cd3eaf4d9cd51` |
| NVIDIA | `Setup.exe` | 30720 | `540540db4bf97f90046fe8d1a0c1d3151f0a873a6cae776499b3e1fcea8345d1` |
| NVIDIA | `app/DJStem.exe` | 31232 | `9690aa02dd978acb5c2e0ed24f4e11f9df9b2958c142ddaf34f2a71e6a807ca5` |
| NVIDIA | `app/tools/StemKit.exe` | 44032 | `90d080738c4e3a175f33a7dedb8813a89ad9514f2499f335a5ca8fcca8a25e5f` |
| NVIDIA | `app/tools/StemKitPlayer.exe` | 482304 | `1679c2fc189679ad6835f3b1f9e74c947f8d918e9b7abfdb2290b9c59bcdb4c4` |
| NVIDIA | `app/tools/ReleaseProbe.exe` | 466432 | `02f260a158d8ef48063046d0f83ac5f053a13354fb90fb162dfb12be23ac0f30` |


## Proposed review

Use the official [Microsoft Security Intelligence portal](https://www.microsoft.com/en-us/wdsi/filesubmission),
select **Software developer**, then **Smart App Control**. Submit only the specific
affected executables. The portal lists a 50 MB upload limit, so supply the existing
recorded filenames and hashes
for the full installers and ask Microsoft how to provide them for analysis.
Review of an inner executable must not be treated as approval of its installer.

The proposed description below fits the portal additional-information field.
The maintainer must approve sending the files/report to Microsoft and complete
any required account, consent or human-verification steps. No submission or
security-policy change has been made.

```text
DJStem 1.0.1 (GPL-3.0-or-later), https://github.com/nogeishat/DJStem
Request: review Windows 11 Smart App Control blocks of this legitimate music application. Please determine whether the files can be recognized as safe and advise if additional evidence is required. We are not requesting a security-policy exemption or a code-signing certificate.

The app plays local audio, downloads user-requested YouTube/SoundCloud tracks and separates stems locally with UVR. CPU and NVIDIA editions bundle Node.js, Python, FFmpeg, yt-dlp and model/runtime dependencies. User music/settings stay in a selected local library. Both setup and native app executables are unsigned. CodeIntegrity event 3077 records the setup being blocked; the player and link drop box have also been blocked. No antivirus detection name was supplied. The 1.0.1 patch fixes a separate launcher log-sharing bug.

Files are unchanged from the public release. Full installers exceed the portal's 50 MB limit; exact inner executable samples and SHA-256 details are provided separately. Please advise how to submit the full installers:
[Withdrawn release: public downloads removed.]
CPU SHA256 b3d647e50140af83752b461370f2542e52dfe2e00c412ec643279bca59deab59
NVIDIA SHA256 afb855403e74d78ebc5f3744fdd4482cd937e6e90206f8c49d8cd3eaf4d9cd51

Source, affected binary paths and trust audit:
https://github.com/nogeishat/DJStem/blob/main/docs/SMART-APP-CONTROL-REVIEW.md
```
