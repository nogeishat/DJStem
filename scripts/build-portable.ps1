param([string]$Destination = '', [switch]$WithoutMusic)
$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if (!$Destination) { $Destination = Join-Path $source 'StemKit-USB' }
$target = [IO.Path]::GetFullPath($Destination)
if ($target -eq $source -or !$target.StartsWith($source + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Build the separate USB folder inside this project.' }
if (Test-Path -LiteralPath $target) { throw "Destination already exists; choose a new folder to preserve its music and settings: $target" }

function Copy-Tree([string]$From, [string]$To, [string[]]$Exclude = @()) {
    if (!(Test-Path -LiteralPath $From)) { throw "Missing source folder: $From" }
    New-Item -ItemType Directory -Path $To -Force | Out-Null
    $copyArguments = @($From, $To, '/E', '/COPY:DAT', '/DCOPY:DAT', '/R:1', '/W:1', '/XJ', '/NFL', '/NDL', '/NJH', '/NJS', '/NP', '/XD', '.git', '__pycache__')
    $copyArguments += $Exclude
    $copyArguments += @('/XF', '*.pyc', '*.pyo')
    & robocopy.exe @copyArguments | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copy failed: $From (robocopy $LASTEXITCODE)" }
}
function Write-Utf8([string]$File, [string]$Text) {
    [IO.File]::WriteAllText($File, $Text, [Text.UTF8Encoding]::new($false))
}
function Replace-Checked([string]$File, [string]$Before, [string]$After) {
    $text = [IO.File]::ReadAllText($File)
    if (!$text.Contains($Before)) { throw "Portable patch no longer matches $File : $Before" }
    Write-Utf8 $File ($text.Replace($Before, $After))
}

$node = (Get-Command node.exe).Source
$pythonInfo = & (Join-Path $source 'tools/stem-runtime/Scripts/python.exe') -c 'import sys; print(sys.base_prefix)'
if ($LASTEXITCODE) { throw 'The existing stem runtime must be usable before making a USB copy.' }
$basePython = $pythonInfo.Trim()
$ffmpeg = Get-Item -LiteralPath (Get-Command ffmpeg.exe).Source
$ffprobe = Get-Item -LiteralPath (Get-Command ffprobe.exe).Source
$ffmpegFile = if ($ffmpeg.LinkType) { $ffmpeg.Target } else { $ffmpeg.FullName }
$ffprobeFile = if ($ffprobe.LinkType) { $ffprobe.Target } else { $ffprobe.FullName }
$settings = Get-Content -LiteralPath (Join-Path $source 'settings.json') -Raw | ConvertFrom-Json
$library = [IO.Path]::GetFullPath($settings.libraryRoot)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'

New-Item -ItemType Directory -Path $target | Out-Null
foreach ($dir in @('tools', 'data', 'library/mp3', 'library/stems', 'docs')) { New-Item -ItemType Directory -Path (Join-Path $target $dir) -Force | Out-Null }
Write-Host 'Copying application sources and player libraries...'
foreach ($dir in @('desktop', 'lib', 'public', 'scripts', 'test')) { Copy-Tree (Join-Path $source $dir) (Join-Path $target $dir) }
foreach ($file in @('server.mjs', 'package.json', 'package-lock.json', 'requirements-stems.txt', 'THIRD-PARTY-NOTICES.md')) { Copy-Item -LiteralPath (Join-Path $source $file) -Destination (Join-Path $target $file) }
Copy-Item -LiteralPath (Join-Path $source 'README.md') -Destination (Join-Path $target 'docs/README-original.md')
Get-ChildItem -LiteralPath (Join-Path $source 'tools') -File | Where-Object { $_.Extension -eq '.dll' -or $_.Name -in @('yt-dlp.exe','NAudio-LICENSE.txt','Mixxx-COPYING.txt','Mixxx-LICENSE.txt') } | Copy-Item -Destination (Join-Path $target 'tools')
Copy-Item -LiteralPath $node -Destination (Join-Path $target 'tools/node.exe')
$nodeVersion = (& $node --version).Trim()
$licenseCache = Join-Path $source "scripts/portable/Node-$nodeVersion-LICENSE.txt"
if (Test-Path -LiteralPath $licenseCache) { Copy-Item -LiteralPath $licenseCache -Destination (Join-Path $target 'tools/Node-LICENSE.txt') }
else { Invoke-WebRequest -UseBasicParsing -Uri "https://raw.githubusercontent.com/nodejs/node/$nodeVersion/LICENSE" -OutFile (Join-Path $target 'tools/Node-LICENSE.txt') }
Copy-Item -LiteralPath $ffmpegFile -Destination (Join-Path $target 'tools/ffmpeg.exe')
Copy-Item -LiteralPath $ffprobeFile -Destination (Join-Path $target 'tools/ffprobe.exe')
$ffmpegRoot = Split-Path (Split-Path $ffmpegFile -Parent) -Parent
Copy-Item -LiteralPath (Join-Path $ffmpegRoot 'LICENSE') -Destination (Join-Path $target 'tools/FFmpeg-LICENSE.txt')
Copy-Tree (Join-Path $source 'tools/player-packages') (Join-Path $target 'tools/player-packages')
Copy-Tree (Join-Path $source 'tools/stem-models') (Join-Path $target 'tools/stem-models')
Copy-Tree (Join-Path $source 'tools/uvr') (Join-Path $target 'tools/uvr')

Write-Host 'Building a standalone Python runtime (no installed Python required)...'
$pythonTarget = Join-Path $target 'tools/stem-runtime'
# Copy the interpreter, DLLs and standard library, excluding the host's unrelated packages.
Copy-Tree $basePython $pythonTarget @('site-packages', 'Scripts', 'include', 'libs')
Copy-Tree (Join-Path $source 'tools/stem-runtime/Lib/site-packages') (Join-Path $pythonTarget 'Lib/site-packages')
# The base interpreter has no pyvenv.cfg; it discovers its library relative to python.exe.
Copy-Item -LiteralPath (Join-Path $source 'tools/MSVCP140.dll') -Destination $pythonTarget

if (!$WithoutMusic) {
    Write-Host 'Copying existing MP3s, stems and artist artwork...'
    foreach ($dir in @('mp3', 'stems', '.artist-covers')) {
        $from = Join-Path $library $dir
        if (Test-Path -LiteralPath $from) { Copy-Tree $from (Join-Path $target "library/$dir") @('.work') }
    }
    if (Test-Path -LiteralPath (Join-Path $library '.artist-folders.json')) { Copy-Item -LiteralPath (Join-Path $library '.artist-folders.json') -Destination (Join-Path $target 'library') }
    foreach ($file in @('queue.json','links.txt','mp3-download-archive.txt','player-settings.json')) {
        $from = Join-Path $source "data/$file"
        if (Test-Path -LiteralPath $from) { Copy-Item -LiteralPath $from -Destination (Join-Path $target "data/$file") }
    }
}
Write-Utf8 (Join-Path $target 'settings.json') (@{ libraryRoot = 'library'; matchedStemsRoot = 'library/matched-stems' } | ConvertTo-Json)
# Include the library's former drive letter for saved mixes created before it moved to D:.
$oldRoots = @($library, 'X:\Music\StemKit') | Select-Object -Unique
Write-Utf8 (Join-Path $target 'data/portable-location.json') (@{ projectRoot = $source; libraryRoots = @($oldRoots) } | ConvertTo-Json)

Write-Host 'Configuring portable launchers and paths...'
# Build on this PC; the USB launch path never invokes a compiler, Git, pip or setup downloads.
Copy-Item -LiteralPath (Join-Path $target 'scripts/start.mjs') -Destination (Join-Path $target 'scripts/run-downloader.mjs')
Copy-Item -LiteralPath (Join-Path $target 'scripts/start-player.mjs') -Destination (Join-Path $target 'scripts/run-player.mjs')
Write-Utf8 (Join-Path $target 'scripts/start.mjs') "import { launch } from './portable/boot.mjs';`nawait launch('downloader');`n"
Write-Utf8 (Join-Path $target 'scripts/start-player.mjs') "import { launch } from './portable/boot.mjs';`nawait launch('player');`n"
Replace-Checked (Join-Path $target 'scripts/run-downloader.mjs') 'const executable = buildDesktop();' "const executable = path.join(ROOT, 'tools', 'StemKit.exe');"
Replace-Checked (Join-Path $target 'scripts/run-downloader.mjs') '  await setup();' ''
Replace-Checked (Join-Path $target 'scripts/run-downloader.mjs') '  await buildPlayer();' ''
Replace-Checked (Join-Path $target 'scripts/run-downloader.mjs') 'spawn(buildDesktop(),' "spawn(path.join(ROOT, 'tools', 'StemKit.exe'),"
Replace-Checked (Join-Path $target 'scripts/run-player.mjs') "import { spawn } from 'node:child_process';" "import { spawn } from 'node:child_process';`nimport path from 'node:path';"
Replace-Checked (Join-Path $target 'scripts/run-player.mjs') 'const executable = await buildPlayer();' "const executable = path.join(ROOT, 'tools', 'StemKitPlayer.exe');"
Write-Utf8 (Join-Path $target 'scripts/setup-stems.mjs') @'
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { verifyStems } from './portable/verify.mjs';
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
export async function setupStems() { return verifyStems(root); }
'@
foreach ($file in @('lib/analysis.mjs', 'lib/stems.mjs')) {
    Replace-Checked (Join-Path $target $file) "'stem-runtime', 'Scripts', 'python.exe'" "'stem-runtime', 'python.exe'"
}
foreach ($file in @('test/metadata.test.mjs', 'test/stems.test.mjs')) {
    Replace-Checked (Join-Path $target $file) "'stem-runtime', 'Scripts', 'python.exe'" "'stem-runtime', 'python.exe'"
}
Replace-Checked (Join-Path $target 'desktop/PlayerMatching.cs') '"stem-runtime", "Scripts", "python.exe"' '"stem-runtime", "python.exe"'
# Relative library settings also work when a native executable is started from another directory.
# PlayerLibrary resolves relative paths against PlayerData.Root itself.
Replace-Checked (Join-Path $target 'lib/library.mjs') 'return path.resolve(process.env.STEMKIT_LIBRARY || settings.libraryRoot || path.join(root, ''downloads''));' 'return path.resolve(root, process.env.STEMKIT_LIBRARY || settings.libraryRoot || ''downloads'');'
foreach ($entry in @(@('desktop/Player.cs', 'Start-Player.exe'), @('desktop/StemKit.cs', 'Start-StemKit.exe'))) {
    $guard = @"
        string portableProject = Directory.GetParent(Path.GetDirectoryName(Application.ExecutablePath)).FullName;
        if (!String.Equals(Environment.GetEnvironmentVariable("STEMKIT_PORTABLE_ROOT"), portableProject, StringComparison.OrdinalIgnoreCase)) {
            Process.Start(Path.Combine(portableProject, "$($entry[1])")); return 0;
        }
"@
    Replace-Checked (Join-Path $target $entry[0]) 'private static int Main(string[] args)' 'private static int Main(string[] args)'
    $file = Join-Path $target $entry[0]
    $text = [IO.File]::ReadAllText($file)
    $text = [regex]::Replace($text, '(private static int Main\(string\[\] args\)\s*\{)', ('$1' + "`n" + $guard))
    $text = $text.Replace('Local\\StemKitMusicPlayer', 'Local\\StemKitUSBMusicPlayer').Replace('Local\\StemKitDesktopDropBox', 'Local\\StemKitUSBDesktopDropBox')
    Write-Utf8 $file $text
}
Replace-Checked (Join-Path $target 'desktop/Player.cs') 'process.Id != Process.GetCurrentProcess().Id && process.MainWindowHandle != IntPtr.Zero' 'process.Id != Process.GetCurrentProcess().Id && process.MainWindowHandle != IntPtr.Zero && String.Equals(process.MainModule.FileName, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase)'

foreach ($mode in @('StemKit','Player')) {
    Write-Utf8 (Join-Path $target "Start-$mode.cmd") "@echo off`r`nstart `"`" `"%~dp0Start-$mode.exe`"`r`n"
    Write-Utf8 (Join-Path $target "Start-$mode.vbs") @"
Option Explicit
Dim shell, files, root
Set shell = CreateObject("WScript.Shell")
Set files = CreateObject("Scripting.FileSystemObject")
root = files.GetParentFolderName(WScript.ScriptFullName)
shell.Run Chr(34) & root & "\Start-$mode.exe" & Chr(34), 0, False
"@
}
& $compiler /nologo /target:winexe /platform:x64 /optimize+ "/out:$target/Start-StemKit.exe" /reference:System.dll /reference:System.Windows.Forms.dll (Join-Path $target 'scripts/portable/Launcher.cs')
if ($LASTEXITCODE) { throw 'Portable launcher compilation failed.' }
Copy-Item -LiteralPath (Join-Path $target 'Start-StemKit.exe') -Destination (Join-Path $target 'Start-Player.exe')
Copy-Item -LiteralPath (Join-Path $target 'Start-StemKit.exe') -Destination (Join-Path $target 'Check-USB.exe')
& $node (Join-Path $target 'scripts/build-desktop.mjs')
if ($LASTEXITCODE) { throw 'Portable drop box compilation failed.' }
& $node (Join-Path $target 'scripts/build-player.mjs')
if ($LASTEXITCODE) { throw 'Portable player compilation failed.' }
$probeBuild = @'
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { PLAYER_SOURCES, playerReferences } from './scripts/build-player.mjs';
const root = process.cwd();
const result = spawnSync(path.join(process.env.WINDIR, 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'), ['/nologo', '/target:exe', '/optimize+', '/platform:x64', '/main:PortableProbe', '/out:' + path.join(root, 'tools/PortableProbe.exe'), '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll', '/reference:System.Web.Extensions.dll', ...playerReferences().map(file => '/reference:' + file), ...PLAYER_SOURCES.map(file => path.join(root, 'desktop', file)), path.join(root, 'scripts/portable/NativeProbe.cs')], { encoding: 'utf8', windowsHide: true });
console.log(result.stdout || result.stderr); process.exitCode = result.status ?? 1;
'@
Push-Location $target
try { $probeBuild | & $node --input-type=module; if ($LASTEXITCODE) { throw 'Native probe compilation failed.' } } finally { Pop-Location }

Copy-Item -LiteralPath (Join-Path $source 'scripts/portable/README-USB.txt') -Destination (Join-Path $target 'README-USB.txt')
Copy-Item -LiteralPath (Join-Path $source 'scripts/portable/README-USB.txt') -Destination (Join-Path $target 'README.md')
Write-Utf8 (Join-Path $target 'portable-build.json') (@{ createdAt = [DateTime]::UtcNow.ToString('o'); platform = 'win32-x64'; includesMusic = !$WithoutMusic; library = 'library'; uvrCommit = '5517e0cf0d1acd16a1618eeedec596957523f9e1'; python = '3.12 standalone'; excludedCaches = @('matched-stems', 'pip-cache', 'numba-cache') } | ConvertTo-Json)
Write-Host "Portable folder created: $target"
Write-Host 'Run Check-USB.exe, then copy this entire folder onto the USB drive.'
