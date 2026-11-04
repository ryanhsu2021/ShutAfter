# Generates src/ShutAfter/Assets/ShutAfter.ico  (download arrow + clock badge)
# ASCII only on purpose: Windows PowerShell 5.1 misreads UTF-8 without BOM.
param([switch]$PreviewOnly)

Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$assetDir = Join-Path $root 'src\ShutAfter\Assets'
$prevDir = Join-Path $PSScriptRoot 'preview'
New-Item -ItemType Directory -Force -Path $assetDir | Out-Null
New-Item -ItemType Directory -Force -Path $prevDir | Out-Null

function RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconBitmap([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    $f = [single]$s
    $m = $f * 0.035                                    # outer margin, keeps icon off the edge
    $tile = RoundRect $m $m ($f - 2 * $m) ($f - 2 * $m) ($f * 0.235)

    $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)),
        (New-Object System.Drawing.PointF($f, $f)),
        [System.Drawing.Color]::FromArgb(255, 96, 158, 255),
        [System.Drawing.Color]::FromArgb(255, 32, 74, 190))
    $g.FillPath($br, $tile)

    # subtle top highlight so the tile does not look flat at large sizes
    if ($s -ge 48) {
        $hi = New-Object System.Drawing.Drawing2D.GraphicsPath
        $hi.AddEllipse(-$f * 0.25, -$f * 0.62, $f * 1.30, $f * 1.15)
        $hb = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            (New-Object System.Drawing.PointF(0, 0)),
            (New-Object System.Drawing.PointF(0, $f * 0.55)),
            [System.Drawing.Color]::FromArgb(46, 255, 255, 255),
            [System.Drawing.Color]::FromArgb(0, 255, 255, 255))
        $old = $g.Clip
        $g.SetClip($tile, [System.Drawing.Drawing2D.CombineMode]::Intersect)
        $g.FillPath($hb, $hi)
        $g.Clip = $old
        $hb.Dispose(); $hi.Dispose()
    }

    $simple = $s -le 24            # tiny sizes: drop the badge, keep only the arrow
    $k = if ($simple) { 1.22 } else { 1.0 }
    $cx = if ($simple) { 0.5 } else { 0.462 }
    $cy = if ($simple) { 0.5 } else { 0.458 }

    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)

    # --- download arrow ---
    $stemW = $f * 0.163 * $k
    $stemX = $f * $cx - $stemW / 2
    $stemY = $f * $cy - $f * 0.300 * $k
    $stemH = $f * 0.352 * $k
    $stem = RoundRect $stemX $stemY $stemW $stemH ($stemW * 0.45)
    $g.FillPath($white, $stem)

    $headW = $f * 0.420 * $k
    $headTop = $f * $cy + $f * 0.030 * $k
    $apexY = $f * $cy + $f * 0.238 * $k
    $head = New-Object System.Drawing.Drawing2D.GraphicsPath
    $head.AddPolygon(@(
        (New-Object System.Drawing.PointF(($f * $cx - $headW / 2), $headTop)),
        (New-Object System.Drawing.PointF(($f * $cx + $headW / 2), $headTop)),
        (New-Object System.Drawing.PointF(($f * $cx), $apexY))
    ))
    $g.FillPath($white, $head)

    # --- clock badge (bottom right) ---
    if (-not $simple) {
        $bx = $f * 0.722
        $by = $f * 0.722
        $r = $f * 0.180

        # gap ring filled with the very same gradient: invisible on the tile,
        # cleanly cuts the arrow where the badge sits on top of it
        $gr = $f * 0.042
        $g.FillEllipse($br, $bx - $r - $gr, $by - $r - $gr, 2 * ($r + $gr), 2 * ($r + $gr))

        $g.FillEllipse($white, $bx - $r, $by - $r, 2 * $r, 2 * $r)

        $hand = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 32, 74, 190), [single]($r * 0.30))
        $hand.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $hand.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $g.DrawLine($hand, $bx, $by, $bx, ($by - $r * 0.56))                 # minute hand -> 12
        $g.DrawLine($hand, $bx, $by, ($bx + $r * 0.50), ($by + $r * 0.16))    # hour hand -> ~4
        $hand.Dispose()

        if ($s -ge 48) {
            $tick = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 32, 74, 190))
            foreach ($a in @(0, 90, 180, 270)) {
                $rad = $a * [math]::PI / 180
                $tx = $bx + [single]([math]::Cos($rad) * $r * 0.80)
                $ty = $by + [single]([math]::Sin($rad) * $r * 0.80)
                $ts = [single]($r * 0.13)
                $g.FillEllipse($tick, $tx - $ts, $ty - $ts, 2 * $ts, 2 * $ts)
            }
            $tick.Dispose()
        }
    }

    $white.Dispose(); $br.Dispose(); $stem.Dispose(); $head.Dispose(); $tile.Dispose()
    $g.Dispose()
    return $bmp
}

function Get-DibBytes($bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([int]40); $bw.Write([int]$w); $bw.Write([int]($h * 2))
    $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]0)
    $bw.Write([int]($w * $h * 4)); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($y = $h - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $w; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
        }
    }
    $rowBytes = [int]([math]::Ceiling($w / 32.0) * 4)
    $bw.Write((New-Object byte[] ($rowBytes * $h)))
    $bw.Flush()
    $out = $ms.ToArray()
    $bw.Dispose(); $ms.Dispose()
    return , $out
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = @()
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    if ($s -ge 128) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bytes = $ms.ToArray()
        $ms.Dispose()
    }
    else {
        $bytes = Get-DibBytes $bmp
    }
    $frames += [pscustomobject]@{ Size = $s; Data = $bytes }
    if ($s -in @(16, 32, 48, 256)) { $bmp.Save((Join-Path $prevDir "icon_$s.png"), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
}

if (-not $PreviewOnly) {
    $icoPath = Join-Path $assetDir 'ShutAfter.ico'
    $fs = [System.IO.File]::Create($icoPath)
    $bw = New-Object System.IO.BinaryWriter($fs)
    $bw.Write([int16]0); $bw.Write([int16]1); $bw.Write([int16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($fr in $frames) {
        $dim = if ($fr.Size -ge 256) { 0 } else { $fr.Size }
        $bw.Write([byte]$dim); $bw.Write([byte]$dim)
        $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([int16]1); $bw.Write([int16]32)
        $bw.Write([int]$fr.Data.Length); $bw.Write([int]$offset)
        $offset += $fr.Data.Length
    }
    foreach ($fr in $frames) { $bw.Write($fr.Data) }
    $bw.Flush(); $bw.Dispose(); $fs.Dispose()
    "ICO written: $icoPath  ($((Get-Item $icoPath).Length) bytes, $($frames.Count) frames)"
}
else {
    "preview only -> $prevDir"
}
