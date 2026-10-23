<#
    Draws the Compare Manager logo and writes it as CompareManager.ico (16–256 px) and
    Resources\logo.png (256 px).

    The logo is two script pages side by side on an indigo-to-sky tile, with the same line
    lit amber on both: two copies of one thing, and the line where they part.

    Run it again after changing the drawing; the build only reads the files it writes.

        pwsh -File CompareManager\Tools\MakeLogo.ps1
#>
param(
    [string]$ProjectDir = (Split-Path -Parent $PSScriptRoot)
)

Add-Type -AssemblyName System.Drawing

function New-RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Draw-Logo([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    # Everything is laid out on a 256 grid and scaled down.
    $s = $size / 256.0
    $g.ScaleTransform($s, $s)

    # The tile.
    $tile = New-RoundedPath 8 8 240 240 52
    $tileBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(8, 8)), (New-Object System.Drawing.PointF(248, 248)),
        [System.Drawing.Color]::FromArgb(255, 79, 70, 229), [System.Drawing.Color]::FromArgb(255, 14, 165, 233))
    $g.FillPath($tileBrush, $tile)

    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 255, 255))
    $shade = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(60, 15, 23, 42))
    $line  = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 165, 180, 252))
    $amber = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 245, 158, 11))

    # Two pages, the right one a little lower, each with a soft shadow.
    $pages = @(@{ X = 44; Y = 50 }, @{ X = 136; Y = 66 })
    foreach ($p in $pages) {
        $g.FillPath($shade, (New-RoundedPath ($p.X + 4) ($p.Y + 6) 76 140 14))
        $g.FillPath($white, (New-RoundedPath $p.X $p.Y 76 140 14))
    }

    # Script lines. Small sizes get fewer, thicker lines so they still read.
    if ($size -le 24) {
        $rows = @(@{ Y = 36; W = 48 }, @{ Y = 70; W = 48; Lit = $true }, @{ Y = 104; W = 36 })
        $thick = 18
    } else {
        $rows = @(@{ Y = 24; W = 50 }, @{ Y = 44; W = 38 }, @{ Y = 64; W = 50; Lit = $true },
                  @{ Y = 84; W = 30 }, @{ Y = 104; W = 44 })
        $thick = 10
    }
    foreach ($p in $pages) {
        foreach ($r in $rows) {
            $brush = if ($r.Lit) { $amber } else { $line }
            $width = if ($r.Lit) { 56 } else { $r.W }
            $g.FillPath($brush, (New-RoundedPath ($p.X + 10) ($p.Y + $r.Y) $width $thick ($thick / 2)))
        }
    }

    $g.Dispose()
    return $bmp
}

function Get-DibBytes([System.Drawing.Bitmap]$bmp) {
    $size = $bmp.Width
    $rect = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                          [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    [byte[]]$pixels = New-Object byte[] ($stride * $size)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    $bmp.UnlockBits($data)

    $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
    $ms = New-Object System.IO.MemoryStream
    $w  = New-Object System.IO.BinaryWriter($ms)

    # BITMAPINFOHEADER: the height counts the colour rows and the AND mask together.
    $w.Write([int]40); $w.Write([int]$size); $w.Write([int]($size * 2))
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]0)
    $w.Write([int]($size * $size * 4 + $maskStride * $size))
    $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)

    # Colour rows, bottom-up, BGRA — which is how Format32bppArgb sits in memory.
    for ($row = $size - 1; $row -ge 0; $row--) { $w.Write($pixels, $row * $stride, $size * 4) }
    # The AND mask: all zero, the alpha channel does the work.
    $w.Write([byte[]](New-Object byte[] ($maskStride * $size)))

    $w.Flush()
    # The comma stops PowerShell unrolling the array into loose bytes on the way out.
    return ,$ms.ToArray()
}

$sizes  = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($size in $sizes) {
    $bmp = Draw-Logo $size
    if ($size -eq 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        [byte[]]$bytes = $ms.ToArray()
        New-Item -ItemType Directory -Force (Join-Path $ProjectDir 'Resources') | Out-Null
        $bmp.Save((Join-Path $ProjectDir 'Resources\logo.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    } else {
        $bytes = Get-DibBytes $bmp
    }
    $bmp.Dispose()
    [pscustomobject]@{ Size = $size; Bytes = $bytes }
}

$out = New-Object System.IO.MemoryStream
$w   = New-Object System.IO.BinaryWriter($out)
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$images.Count)

$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $dim = if ($img.Size -ge 256) { 0 } else { $img.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]$img.Bytes.Length); $w.Write([int]$offset)
    $offset += $img.Bytes.Length
}
foreach ($img in $images) { $w.Write([byte[]]$img.Bytes) }
$w.Flush()

[System.IO.File]::WriteAllBytes((Join-Path $ProjectDir 'CompareManager.ico'), $out.ToArray())
Write-Host "Wrote CompareManager.ico ($($images.Count) sizes) and Resources\logo.png"
