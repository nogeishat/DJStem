# Windows trust status

**DJStem 1.0.3 is unsigned, as were 1.0.2, 1.0.0 and the withdrawn 1.0.1. Smart App
Control has blocked setup, player and the link drop box on the test PC.
Compatibility remains unverified.**
The 1.0.2 launcher and shutdown fixes do not resolve Windows trust. A Code
Integrity event 3077 for the installed 1.0.0 drop box confirms that its
`spawn UNKNOWN` startup failure was a signing-policy block.

Smart App Control can allow an unsigned file when Microsoft has enough positive
reputation for it. Otherwise, an unknown unsigned file is blocked. Microsoft
recommends signing with a trusted certificate. Small changes to application
features, icons, names or archive format cannot guarantee acceptance.

- [Microsoft's explanation](https://support.microsoft.com/en-us/windows/security/threat-malware-protection/smart-app-control-frequently-asked-questions)
- [Developer guidance](https://learn.microsoft.com/en-us/windows/apps/develop/smart-app-control/code-signing-for-smart-app-control)

## Review without a signing programme

Microsoft's [file-submission portal](https://www.microsoft.com/en-us/wdsi/filesubmission)
includes **Software developer** and **Smart App Control** options. A review can
be requested for the existing files without enrolling in Artifact Signing or an
open-source signing service. This is a request for analysis, not a promise to
allow the app or a replacement publisher certificate. The maintainer has chosen to keep the review materials prepared only. No review has been submitted.
The [historical review report](SMART-APP-CONTROL-REVIEW.md) covers withdrawn version 1.0.1; its downloads are no longer available.

Keep the original release hashes while requesting review. The portal limits
uploads to 50 MB and asks for specific affected files. The small inner executables
can be submitted individually, with links and hashes for both full installers.
The full installers exceed that limit; do not claim that reviewing an inner file
also approves the outer installer. Ask Microsoft how it wants to receive those.

Microsoft's decision and a successful end-to-end test with Smart App Control
**enforced** are needed before claiming that this route works. New unsigned
versions may need a separate reputation decision. Signing remains the recommended
route if review cannot resolve the block. No signing-provider application or
integration is being pursued for this update.

## Maintainer audit

Run this read-only check against each assembled release:

```powershell
scripts/release/audit-windows-trust.ps1 -StageDirectory releases/1.0.0/staging/cpu -Installer releases/1.0.0/DJStem-1.0.0-Windows-x64-cpu-Setup.exe -InventoryNative -ReportPath releases/1.0.0/cpu-windows-trust.json
```

Repeat for NVIDIA. Reports contain relative binary names, SHA-256 hashes and
Authenticode status. A valid signature on this PC is not proof of public trust;
a clean scan is not proof of Smart App Control acceptance. Audit upstream DLLs,
Python extensions and helpers as well as the installer and first-party code.

Before publishing a compatibility claim, test setup, library selection, player,
Add songs, download, CPU/GPU separation and playback with enforcement enabled.
The app does not change Windows security policies or install trust certificates.
