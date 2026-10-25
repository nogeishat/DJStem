param([ValidateSet('cpu','nvidia')][string]$Edition='cpu',[switch]$Package)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$version = (Get-Content -LiteralPath (Join-Path $projectRoot 'package.json') -Raw | ConvertFrom-Json).version
$releaseRoot = Join-Path $projectRoot "releases/$version"
$stage = Join-Path $releaseRoot "staging/$Edition"
$app = Join-Path $stage 'app'
$support = Join-Path $projectRoot 'tools/release-support'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$node = (Get-Command node.exe).Source
function Copy-Tree([string]$From,[string]$To,[string[]]$Exclude=@()) {
    New-Item -ItemType Directory -Path $To -Force | Out-Null
    $arguments = @($From,$To,'/E','/COPY:DAT','/DCOPY:DAT','/R:1','/W:1','/XJ','/NFL','/NDL','/NJH','/NJS','/NP','/XD','.git','__pycache__') + $Exclude + @('/XF','*.pyc','*.pyo','*.lib','*.pdb','*.exp')
    & robocopy.exe @arguments | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copy failed: $From ($LASTEXITCODE)" }
}
function Write-Json([string]$File,$Value) { [IO.File]::WriteAllText($File,($Value | ConvertTo-Json -Depth 30),[Text.UTF8Encoding]::new($false)) }
if (!(Test-Path -LiteralPath $app)) {
    New-Item -ItemType Directory -Path $app -Force | Out-Null
    foreach ($folder in @('desktop','lib','scripts','assets')) { Copy-Tree (Join-Path $projectRoot $folder) (Join-Path $app $folder) }
    foreach ($file in @('server.mjs','package.json','package-lock.json','requirements-stems.txt','THIRD-PARTY-NOTICES.md','LICENSE')) { Copy-Item -LiteralPath (Join-Path $projectRoot $file) -Destination $app }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/RELEASE-README.md') -Destination (Join-Path $app 'README.txt')
    New-Item -ItemType Directory -Path (Join-Path $app 'defaults'),(Join-Path $app 'tools'),(Join-Path $app 'third-party') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectRoot 'defaults/settings.json'),(Join-Path $projectRoot 'defaults/player-settings.json') -Destination (Join-Path $app 'defaults')
    Copy-Tree (Join-Path $projectRoot 'third-party') (Join-Path $app 'third-party')
    $libraries = @('NAudio.Core.dll','NAudio.WinMM.dll','NAudio.Wasapi.dll','rubberband-3.dll','sleefdft.dll','sleef.dll','samplerate.dll','MSVCP140.dll','VCRUNTIME140.dll','VCRUNTIME140_1.dll','NAudio-LICENSE.txt','Mixxx-LICENSE.txt','Mixxx-COPYING.txt','yt-dlp.exe')
    foreach ($file in $libraries) { Copy-Item -LiteralPath (Join-Path $projectRoot "tools/$file") -Destination (Join-Path $app 'tools') }
    Copy-Item -LiteralPath $node -Destination (Join-Path $app 'tools/node.exe')
    $nodeLicense = Get-ChildItem -LiteralPath (Join-Path $projectRoot 'scripts/portable') -Filter 'Node-*-LICENSE.txt' | Select-Object -First 1
    Copy-Item -LiteralPath $nodeLicense.FullName -Destination (Join-Path $app 'tools/Node-LICENSE.txt')
    foreach ($name in @('ffmpeg','ffprobe')) { $file = Get-Item -LiteralPath (Get-Command "$name.exe").Source; $actual = if ($file.LinkType) { $file.Target } else { $file.FullName }; Copy-Item -LiteralPath $actual -Destination (Join-Path $app "tools/$name.exe"); if($name -eq 'ffmpeg') { Copy-Item -LiteralPath (Join-Path (Split-Path (Split-Path $actual -Parent) -Parent) 'LICENSE') -Destination (Join-Path $app 'tools/FFmpeg-LICENSE.txt') } }
    foreach ($name in @('uvr','stem-models')) { Copy-Tree (Join-Path $projectRoot "tools/$name") (Join-Path $app "tools/$name") }
    $basePython = (& (Join-Path $projectRoot 'tools/stem-runtime/Scripts/python.exe') -c 'import sys; print(sys.base_prefix)').Trim()
    $runtime = Join-Path $app 'tools/stem-runtime'
    Copy-Tree $basePython $runtime @('site-packages','Scripts','include','libs','idlelib','turtledemo','test','ensurepip')
    $packages = Join-Path $projectRoot 'tools/stem-runtime/Lib/site-packages'
    $exclude = @('torch/include','torch/share','torch/test') | ForEach-Object { Join-Path $packages $_ }
    if ($Edition -eq 'cpu') { $exclude += Get-ChildItem -LiteralPath $packages -Directory | Where-Object { $_.Name -match '^(torch|torchvision|torchaudio|functorch|onnxruntime)(-|$|\.)|^onnxruntime_gpu-' } | Select-Object -ExpandProperty FullName }
    Copy-Tree $packages (Join-Path $runtime 'Lib/site-packages') $exclude
    Copy-Item -LiteralPath (Join-Path $projectRoot 'tools/MSVCP140.dll') -Destination $runtime
    if ($Edition -eq 'cpu') {
        & (Join-Path $runtime 'python.exe') -m pip install --isolated --no-index --no-deps --disable-pip-version-check --find-links (Join-Path $support 'cpu-wheels') torch==2.5.1 torchvision==0.20.1 torchaudio==2.5.1 onnxruntime==1.20.1
        if($LASTEXITCODE) { throw 'CPU runtime installation failed.' }
    }
}
Write-Json (Join-Path $app 'release-build.json') @{version=$version;edition=$Edition;platform='win32-x64';uvrCommit='5517e0cf0d1acd16a1618eeedec596957523f9e1';createdAt=[DateTime]::UtcNow.ToString('o')}
# Refresh app sources on every build while keeping the large assembled runtimes.
foreach ($folder in @('desktop','lib','scripts','assets','defaults','third-party')) { Copy-Tree (Join-Path $projectRoot $folder) (Join-Path $app $folder) }
foreach ($file in @('server.mjs','package.json','package-lock.json','THIRD-PARTY-NOTICES.md','LICENSE')) { Copy-Item -LiteralPath (Join-Path $projectRoot $file) -Destination $app }
# Native Python fallback and entry points use the release's private runtime.
foreach ($file in @('lib/analysis.mjs','lib/stems.mjs')) { $targetFile=Join-Path $app $file; $text=[IO.File]::ReadAllText($targetFile).Replace("'stem-runtime', 'Scripts', 'python.exe'","'stem-runtime', 'python.exe'"); [IO.File]::WriteAllText($targetFile,$text,[Text.UTF8Encoding]::new($false)) }
$matching = Join-Path $app 'desktop/PlayerMatching.cs'; [IO.File]::WriteAllText($matching,[IO.File]::ReadAllText($matching).Replace('"stem-runtime", "Scripts", "python.exe"','"stem-runtime", "python.exe"'),[Text.UTF8Encoding]::new($false))
foreach ($entry in @(@('Player',''),@('StemKit',' --downloader'))) { $mode=$entry[0]; $text="Option Explicit`r`nDim shell, files, root`r`nSet shell = CreateObject(`"WScript.Shell`")`r`nSet files = CreateObject(`"Scripting.FileSystemObject`")`r`nroot = files.GetParentFolderName(WScript.ScriptFullName)`r`nshell.Run Chr(34) & root & `"\DJStem.exe`" & Chr(34) & `"$($entry[1])`", 0, False`r`n"; [IO.File]::WriteAllText((Join-Path $app "Start-$mode.vbs"),$text) }
$playerSource=Join-Path $app 'desktop/Player.cs'; $playerText=[IO.File]::ReadAllText($playerSource); $playerText=$playerText.Replace('Local\\StemKitMusicPlayer','Local\\DJStemMusicPlayer'); $playerText=$playerText.Replace('Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); bool fresh;', 'if (Environment.GetEnvironmentVariable("STEMKIT_RELEASE_ROOT") == null) { Process.Start(Path.Combine(Directory.GetParent(Path.GetDirectoryName(Application.ExecutablePath)).FullName,"DJStem.exe")); return 0; } Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); bool fresh;'); [IO.File]::WriteAllText($playerSource,$playerText,[Text.UTF8Encoding]::new($false))
& $node (Join-Path $app 'scripts/build-desktop.mjs'); if($LASTEXITCODE) { throw 'Drop box build failed.' }
& $node (Join-Path $app 'scripts/build-player.mjs'); if($LASTEXITCODE) { throw 'Player build failed.' }
$icon=Join-Path $app 'assets/app.ico'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ "/win32icon:$icon" "/out:$app/DJStem.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll (Join-Path $app 'scripts/release/Launcher.cs'); if($LASTEXITCODE) { throw 'Launcher build failed.' }
& $compiler /nologo /target:winexe /platform:x64 /optimize+ "/win32icon:$icon" "/out:$stage/Setup.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll (Join-Path $app 'scripts/release/Installer.cs'); if($LASTEXITCODE) { throw 'Installer build failed.' }
$probeScript=@'
import {spawnSync} from 'node:child_process'; import path from 'node:path'; import {PLAYER_SOURCES,playerReferences} from './scripts/build-player.mjs';
const root=process.cwd(); const args=['/nologo','/target:exe','/optimize+','/platform:x64','/main:ReleaseProbe','/out:'+path.join(root,'tools','ReleaseProbe.exe'),'/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.Web.Extensions.dll',...playerReferences().map(file=>'/reference:'+file),...PLAYER_SOURCES.map(file=>path.join(root,'desktop',file)),path.join(root,'scripts/release/ReleaseProbe.cs')]; const r=spawnSync(path.join(process.env.WINDIR,'Microsoft.NET/Framework64/v4.0.30319/csc.exe'),args,{encoding:'utf8',windowsHide:true}); console.log(r.stdout||r.stderr); process.exitCode=r.status??1;
'@
Push-Location $app; try { $probeScript | & $node --input-type=module; if($LASTEXITCODE) { throw 'Native probe build failed.' } } finally { Pop-Location }
Write-Host "Built $Edition at $app"
$runtime = [IO.Path]::GetFullPath((Join-Path $app 'tools/stem-runtime'))
if (!$runtime.StartsWith([IO.Path]::GetFullPath($releaseRoot) + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Runtime cleanup target is outside the release staging folder.' }
Get-ChildItem -LiteralPath $runtime -Recurse -File | Where-Object { $_.Extension -in @('.lib','.pdb','.exp','.pyc','.pyo') } | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
foreach ($relative in @('Lib/site-packages/torch/include','Lib/site-packages/torch/share','Lib/site-packages/torch/test')) {
    $unused = [IO.Path]::GetFullPath((Join-Path $runtime $relative)); if (!$unused.StartsWith($runtime+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid runtime cleanup path.' }
    if(Test-Path -LiteralPath $unused) { Remove-Item -LiteralPath $unused -Recurse -Force }
}
& $node (Join-Path $PSScriptRoot 'manifest.mjs') $app
if($LASTEXITCODE) { throw 'Dependency manifest generation failed.' }
if($Package) {
    $archive=Join-Path $releaseRoot "DJStem-$version-Windows-x64-$Edition.7z"
    & (Join-Path $support '7zip/x64/7za.exe') a -t7z $archive (Join-Path $stage '*') -mx=9 -m0=LZMA2 -md=128m -mfb=64 -ms=256m -mmt=4 -bsp0 -bb0
    if($LASTEXITCODE) { throw 'Release compression failed.' }
    $stub=Join-Path $releaseRoot "sfx-$Edition.exe"; Copy-Item -LiteralPath (Join-Path $support 'lzma/bin/7zSD.sfx') -Destination $stub -Force
    & (Join-Path $PSScriptRoot 'set-exe-icon.ps1') -Executable $stub -Icon $icon
    $configuration=[Text.Encoding]::UTF8.GetBytes(";!@Install@!UTF-8!`nTitle=`"DJStem $version — $Edition`"`nRunProgram=`"Setup.exe`"`n;!@InstallEnd@!`n")
    $output=Join-Path $releaseRoot "DJStem-$version-Windows-x64-$Edition-Setup.exe"
    $stream=[IO.File]::Create($output)
    try { foreach($part in @($stub,$null,$archive)) { if($null -eq $part) { $stream.Write($configuration,0,$configuration.Length) } else { $inputStream=[IO.File]::OpenRead($part); try { $inputStream.CopyTo($stream) } finally { $inputStream.Dispose() } } } } finally { $stream.Dispose() }
    if((Get-Item -LiteralPath $output).Length -ge 2GB) { throw 'The single installer exceeds GitHub release asset limit (2 GiB).' }
    Write-Host "Packaged $output"
}
