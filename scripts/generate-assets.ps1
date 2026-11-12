# Generates the app icon (.ico) and the MSIX logo PNGs: a black rounded "notch" pill on transparent.
# Run once; outputs are committed alongside the source.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$pkgAssets = Join-Path $root 'Package\Assets'
$appAssets = Join-Path $root 'src\DynamicNotch\Assets'
New-Item -ItemType Directory -Force $pkgAssets, $appAssets | Out-Null

function New-NotchBitmap([int]$w, [int]$h, [double]$fill = 0.62) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = [Math]::Min($w, $h)
    $pw = $s * $fill; $ph = $pw * 0.42
    $x = ($w - $pw) / 2; $y = ($h - $ph) / 2
    $r = $ph / 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($x, $y, 2 * $r, 2 * $r, 90, 180)
    $path.AddArc($x + $pw - 2 * $r, $y, 2 * $r, 2 * $r, 270, 180)
    $path.CloseFigure()
    $g.FillPath([System.Drawing.Brushes]::Black, $path)
    # small white "camera" dot + bars accent
    $d = $ph * 0.36
    $g.FillEllipse([System.Drawing.Brushes]::White, $x + $pw - $r - $d / 2, $y + ($ph - $d) / 2, $d, $d)
    $g.Dispose()
    return $bmp
}

$pngs = @{
    'Square44x44Logo.png' = @(44, 44); 'Square150x150Logo.png' = @(150, 150)
    'Wide310x150Logo.png' = @(310, 150); 'StoreLogo.png' = @(50, 50)
    'Square44x44Logo.targetsize-32.png' = @(32, 32); 'Square44x44Logo.targetsize-32_altform-unplated.png' = @(32, 32)
}
foreach ($k in $pngs.Keys) {
    $b = New-NotchBitmap $pngs[$k][0] $pngs[$k][1] 0.8
    $b.Save((Join-Path $pkgAssets $k), [System.Drawing.Imaging.ImageFormat]::Png)
    $b.Dispose()
}

# Multi-size ICO (PNG-compressed entries)
$sizes = 16, 24, 32, 48, 64, 256
$streams = foreach ($sz in $sizes) {
    $b = New-NotchBitmap $sz $sz 0.95
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose()
    , $ms.ToArray()
}
$icoPath = Join-Path $appAssets 'AppIcon.ico'
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $data = $streams[$i]
    $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$data.Length); $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($data in $streams) { $bw.Write($data) }
$bw.Close()
Write-Host "Assets written to $pkgAssets and $icoPath"
