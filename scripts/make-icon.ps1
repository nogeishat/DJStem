$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetDirectory = Join-Path (Split-Path $PSScriptRoot -Parent) 'assets'
New-Item -ItemType Directory -Path $assetDirectory -Force | Out-Null
$images = @()
foreach ($size in @(16,24,32,48,64,128,256)) {
    $large = [Drawing.Bitmap]::new($size * 4, $size * 4)
    $canvas = [Drawing.Graphics]::FromImage($large)
    $canvas.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $brush = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#F49BBF'))
    $margin = $size / 8
    $canvas.FillEllipse($brush, [single]$margin, [single]$margin, [single]($size * 4 - $margin * 2), [single]($size * 4 - $margin * 2))
    $bitmap = [Drawing.Bitmap]::new($size,$size)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.DrawImage($large,0,0,$size,$size)
    $memory = [IO.MemoryStream]::new(); $bitmap.Save($memory,[Drawing.Imaging.ImageFormat]::Png)
    if ($size -eq 256) { [IO.File]::WriteAllBytes((Join-Path $assetDirectory 'app-icon.png'),$memory.ToArray()) }
    $images += ,@($size,$memory.ToArray())
    $graphics.Dispose(); $bitmap.Dispose(); $brush.Dispose(); $canvas.Dispose(); $large.Dispose(); $memory.Dispose()
}
$stream = [IO.File]::Create((Join-Path $assetDirectory 'app.ico')); $writer = [IO.BinaryWriter]::new($stream)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($entry in $images) {
    $dimension = if ($entry[0] -eq 256) { 0 } else { $entry[0] }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$entry[1].Length); $writer.Write([uint32]$offset)
    $offset += $entry[1].Length
}
foreach ($entry in $images) { $writer.Write([byte[]]$entry[1]) }
$writer.Dispose(); $stream.Dispose()
Write-Output 'Created assets/app.ico and assets/app-icon.png (#F49BBF).'
