# Generates wsnap.ico (multi-resolution) + site/icon.png from code — no binary assets in git.
# A blue rounded "app tile" with white viewfinder corner-marks = capture/snip identity.
#   pwsh -File tools/make-icon.ps1 -Out <path-to-wsnap.ico>
param([string]$Out = (Join-Path (Split-Path $PSScriptRoot -Parent) 'wsnap.ico'))

Add-Type -AssemblyName System.Drawing
$root = Split-Path $Out -Parent
$sizes = 16,24,32,48,64,128,256

function New-IconBitmap([int]$S) {
  $bmp = New-Object System.Drawing.Bitmap($S, $S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
  $g.Clear([System.Drawing.Color]::Transparent)

  # rounded blue tile
  $m = [double]$S * 0.07
  $w = $S - 2*$m
  $rad = [double]$S * 0.23
  $d = $rad * 2
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $path.AddArc($m,        $m,        $d, $d, 180, 90)
  $path.AddArc($m+$w-$d,  $m,        $d, $d, 270, 90)
  $path.AddArc($m+$w-$d,  $m+$w-$d,  $d, $d,   0, 90)
  $path.AddArc($m,        $m+$w-$d,  $d, $d,  90, 90)
  $path.CloseFigure()
  $rectF = New-Object System.Drawing.RectangleF([single]$m, [single]$m, [single]$w, [single]$w)
  $c1 = [System.Drawing.Color]::FromArgb(255, 0x3B, 0x82, 0xF6)
  $c2 = [System.Drawing.Color]::FromArgb(255, 0x25, 0x63, 0xEB)
  $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rectF, $c1, $c2, 50.0)
  $g.FillPath($brush, $path)

  # white viewfinder corner brackets
  $fm = [double]$S * 0.30
  $L  = [double]$S * 0.15
  $t  = [Math]::Max(2.0, $S * 0.066)
  $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, [single]$t)
  $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
  $pen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
  $lo = $fm; $hi = $S - $fm
  $g.DrawLine($pen, [single]$lo, [single]$lo, [single]($lo+$L), [single]$lo)
  $g.DrawLine($pen, [single]$lo, [single]$lo, [single]$lo, [single]($lo+$L))
  $g.DrawLine($pen, [single]($hi-$L), [single]$lo, [single]$hi, [single]$lo)
  $g.DrawLine($pen, [single]$hi, [single]$lo, [single]$hi, [single]($lo+$L))
  $g.DrawLine($pen, [single]$lo, [single]($hi-$L), [single]$lo, [single]$hi)
  $g.DrawLine($pen, [single]$lo, [single]$hi, [single]($lo+$L), [single]$hi)
  $g.DrawLine($pen, [single]($hi-$L), [single]$hi, [single]$hi, [single]$hi)
  $g.DrawLine($pen, [single]$hi, [single]($hi-$L), [single]$hi, [single]$hi)

  # center focus dot
  $dot = [double]$S * 0.05
  $cx = $S / 2.0
  $g.FillEllipse([System.Drawing.Brushes]::White, [single]($cx-$dot), [single]($cx-$dot), [single]($dot*2), [single]($dot*2))

  $pen.Dispose(); $brush.Dispose(); $path.Dispose(); $g.Dispose()
  return $bmp
}

$frames = @()
foreach ($s in $sizes) {
  $b = New-IconBitmap $s
  $ms = New-Object System.IO.MemoryStream
  $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  $frames += ,@($s, $ms.ToArray())
  $b.Dispose(); $ms.Dispose()
}

# favicon/logo PNG (256) — last frame; avoid pipeline flattening of the inner arrays
$png256 = $frames[$frames.Count - 1][1]
[System.IO.File]::WriteAllBytes((Join-Path $root 'site\icon.png'), $png256)

# pack ICO with PNG frames (Vista+ supports PNG-compressed entries)
$fs = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
  $s = $f[0]; $data = $f[1]
  $dim = [byte]($(if ($s -ge 256) { 0 } else { $s }))
  $bw.Write($dim); $bw.Write($dim); $bw.Write([byte]0); $bw.Write([byte]0)
  $bw.Write([uint16]1); $bw.Write([uint16]32)
  $bw.Write([uint32]$data.Length); $bw.Write([uint32]$offset)
  $offset += $data.Length
}
foreach ($f in $frames) { $bw.Write($f[1]) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($Out, $fs.ToArray())
$bw.Dispose(); $fs.Dispose()
"ICO: $Out ($((Get-Item $Out).Length) bytes, $($frames.Count) frames) + site/icon.png"
