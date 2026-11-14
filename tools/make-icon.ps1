# Generates Assets\vytrix.ico (16-256 px) and Assets\vytrix.png from the VYTRIX logo geometry.
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$assets = Join-Path $root 'src\Vytrix\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null

function New-LogoBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $r = [single]($size * 0.22)
    $rect = New-Object System.Drawing.RectangleF 0, 0, ($size - 1), ($size - 1)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($rect.X, $rect.Y, $r * 2, $r * 2, 180, 90)
    $path.AddArc($rect.Right - $r * 2, $rect.Y, $r * 2, $r * 2, 270, 90)
    $path.AddArc($rect.Right - $r * 2, $rect.Bottom - $r * 2, $r * 2, $r * 2, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $r * 2, $r * 2, $r * 2, 90, 90)
    $path.CloseFigure()
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 24, 22, 32)), ([System.Drawing.Color]::FromArgb(255, 10, 10, 13)), 90
    $g.FillPath($bg, $path)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 48, 44, 64)), ([single][Math]::Max(1, $size / 64))
    $g.DrawPath($pen, $path)
    # V mark (same geometry as LogoMark in Theme.xaml, 24-unit grid)
    $s = $size / 24.0
    $o = 0
    $pts = @(
        (New-Object System.Drawing.PointF (3 * $s), (5 * $s)),
        (New-Object System.Drawing.PointF (12 * $s), (20.5 * $s)),
        (New-Object System.Drawing.PointF (21 * $s), (5 * $s)),
        (New-Object System.Drawing.PointF (16.6 * $s), (5 * $s)),
        (New-Object System.Drawing.PointF (12 * $s), (13.2 * $s)),
        (New-Object System.Drawing.PointF (7.4 * $s), (5 * $s))
    )
    $markRect = New-Object System.Drawing.RectangleF (3 * $s), (5 * $s), (18 * $s), (15.5 * $s)
    $mb = New-Object System.Drawing.Drawing2D.LinearGradientBrush $markRect, ([System.Drawing.Color]::FromArgb(255, 167, 139, 250)), ([System.Drawing.Color]::FromArgb(255, 34, 211, 238)), 45
    $blend = New-Object System.Drawing.Drawing2D.ColorBlend 3
    $blend.Colors = @([System.Drawing.Color]::FromArgb(255, 167, 139, 250), [System.Drawing.Color]::FromArgb(255, 139, 92, 246), [System.Drawing.Color]::FromArgb(255, 34, 211, 238))
    $blend.Positions = @([single]0, [single]0.55, [single]1)
    $mb.InterpolationColors = $blend
    $g.FillPolygon($mb, $pts)
    $g.Dispose()
    return $bmp
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($sz in $sizes) {
    $b = New-LogoBitmap $sz
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($sz -eq 256) { $b.Save((Join-Path $assets 'vytrix.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $b.Dispose()
    , $ms.ToArray()
}

# ICO container with embedded PNG frames
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $len = $pngs[$i].Length
    $w.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $w.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$len); $w.Write([UInt32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assets 'vytrix.ico'), $out.ToArray())
Write-Output "Icon written to $assets"

