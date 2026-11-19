# Draws the CoreGlance app icon (dark tile with three bars) and writes a multi-size PNG-in-ICO file.
param([Parameter(Mandatory = $true)][string]$Out)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

$sizes = @(16, 24, 32, 48, 64, 256)
$images = New-Object System.Collections.Generic.List[byte[]]

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $tile = New-RoundRect 0 0 ($s - 0.5) ($s - 0.5) ($s * 0.22)
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.RectangleF 0, 0, $s, $s), ([System.Drawing.Color]::FromArgb(255, 30, 34, 43)), ([System.Drawing.Color]::FromArgb(255, 14, 16, 21)), 90
    $g.FillPath($bg, $tile)

    $colors = @(
        [System.Drawing.Color]::FromArgb(255, 34, 211, 238),
        [System.Drawing.Color]::FromArgb(255, 167, 139, 250),
        [System.Drawing.Color]::FromArgb(255, 52, 211, 153)
    )
    $heights = @(0.42, 0.68, 0.54)
    $pad = $s * 0.2
    $gap = $s * 0.08
    $bw = ($s - 2 * $pad - 2 * $gap) / 3
    $bottom = $s - $pad
    for ($i = 0; $i -lt 3; $i++) {
        $h = ($s - 2 * $pad) * $heights[$i]
        $x = $pad + $i * ($bw + $gap)
        $bar = New-RoundRect $x ($bottom - $h) $bw $h ([Math]::Max(0.5, $bw * 0.3))
        $brush = New-Object System.Drawing.SolidBrush $colors[$i]
        $g.FillPath($brush, $bar)
        $brush.Dispose(); $bar.Dispose()
    }
    $g.Dispose(); $bg.Dispose(); $tile.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $images.Add($ms.ToArray())
    $ms.Dispose(); $bmp.Dispose()
}

$dir = Split-Path -Parent $Out
if ($dir) { New-Item -ItemType Directory -Force $dir | Out-Null }
$fs = [System.IO.File]::Create($Out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$images[$i].Length); $bw.Write([UInt32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Dispose(); $fs.Dispose()
Write-Host "Icon written: $Out"
