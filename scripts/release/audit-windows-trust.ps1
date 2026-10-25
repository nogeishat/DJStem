param(
    [Parameter(Mandatory=$true)][string]$StageDirectory,
    [string]$Installer,
    [switch]$InventoryNative,
    [string]$ReportPath
)
$ErrorActionPreference = 'Stop'
$stage = (Resolve-Path -LiteralPath $StageDirectory -ErrorAction Stop).ProviderPath
$app = Join-Path $stage 'app'
if (!(Test-Path -LiteralPath $app -PathType Container)) { throw 'StageDirectory must contain the release app folder.' }
function Get-SignatureRecord([string]$Path, [string]$Name) {
    $file = Get-Item -LiteralPath $Path -ErrorAction Stop
    $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
    [ordered]@{
        file=$Name
        bytes=$file.Length
        sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        authenticodeStatus=[string]$signature.Status
        publisher=$(if ($signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { $null })
        timestampPresent=($null -ne $signature.TimeStamperCertificate)
    }
}
$payload = @(foreach ($relative in @('Setup.exe','app/DJStem.exe','app/tools/StemKit.exe','app/tools/StemKitPlayer.exe','app/tools/ReleaseProbe.exe')) {
    Get-SignatureRecord (Join-Path $stage $relative) $relative
})
$outer = if ($Installer) { Get-SignatureRecord $Installer (Split-Path $Installer -Leaf) } else { $null }
$native = @()
if ($InventoryNative) {
    $appRoot = [IO.Path]::GetFullPath($app).TrimEnd('\','/')
    $native = @(foreach ($file in Get-ChildItem -LiteralPath $appRoot -Recurse -File | Where-Object { $_.Extension -in @('.exe','.dll','.pyd','.ocx') } | Sort-Object FullName) {
        Get-SignatureRecord $file.FullName $file.FullName.Substring($appRoot.Length + 1).Replace('\','/')
    })
}
$report = [ordered]@{
    schemaVersion=1
    createdUtc=[DateTime]::UtcNow.ToString('o')
    smartAppControlCompatibility='unverified'
    payload=$payload
    installer=$outer
    nativeInventory=$native
    note='Read-only signature/hash audit. Authenticode status reflects this PC certificate store; it does not prove public publisher trust, malware safety or Smart App Control acceptance. Test with enforcement enabled after Microsoft review or signing.'
}
$json = $report | ConvertTo-Json -Depth 8
if ($ReportPath) {
    $destination = [IO.Path]::GetFullPath($ReportPath)
    New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
    [IO.File]::WriteAllText($destination, $json, [Text.UTF8Encoding]::new($false))
} else { $json }
