# Generates wsnap.ico (multi-resolution) + site/icon.png from code — no binary assets in git.
# A black rounded tile with a white "W" = wsnap mark. The W is drawn as a vector polyline
# (not a font glyph) so it stays crisp at 16px. A faint border keeps the dark tile visible
# on a dark taskbar.
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

  # rounded black tile
  $m = [double]$S * 0.07
  $w = $S - 2*$m
  $rad = [double]$S * 0.22
  $d = $rad * 2
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $path.AddArc($m,        $m,        $d, $d, 180, 90)
  $path.AddArc($m+$w-$d,  $m,        $d, $d, 270, 90)
  $path.AddArc($m+$w-$d,  $m+$w-$d,  $d, $d,   0, 90)
  $path.AddArc($m,        $m+$w-$d,  $d, $d,  90, 90)
  $path.CloseFigure()
  $fill = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 0x11, 0x13, 0x17))
  $g.FillPath($fill, $path)
  # faint border so the dark tile stays visible on a dark taskbar
  $bpen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 0x33, 0x37, 0x3D), [single]([Math]::Max(1.0, $S * 0.035)))
  $g.DrawPath($bpen, $path)

  # white "W" as a vector polyline (crisp at small sizes): two V's
  $lx = [double]$S * 0.26; $rx = [double]$S * 0.74
  $ty = [double]$S * 0.34; $by = [double]$S * 0.66; $mid = [double]$S * 0.50
  $span = $rx - $lx
  $pts = @(
    (New-Object System.Drawing.PointF([single]$lx,                  [single]$ty)),
    (New-Object System.Drawing.PointF([single]($lx + $span * 0.25), [single]$by)),
    (New-Object System.Drawing.PointF([single]($S * 0.5),           [single]$mid)),
    (New-Object System.Drawing.PointF([single]($rx - $span * 0.25), [single]$by)),
    (New-Object System.Drawing.PointF([single]$rx,                  [single]$ty))
  )
  $wpen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, [single]([Math]::Max(1.6, $S * 0.11)))
  $wpen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
  $wpen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
  $wpen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
  $g.DrawLines($wpen, [System.Drawing.PointF[]]$pts)

  $wpen.Dispose(); $bpen.Dispose(); $fill.Dispose(); $path.Dispose(); $g.Dispose()
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
