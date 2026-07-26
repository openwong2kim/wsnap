# Generates wsnap.ico (multi-resolution) + site/icon.png from code — no binary assets in git.
# macOS-style app icon: a systemBlue squircle with a white "selection region" mark — a rounded
# rectangle outline with a corner handle on each corner — the same visual language as the live
# capture overlay. Reads instantly as "screen region capture" at every size (16px taskbar to
# 256px store tile). Supersedes the old black-tile + "W" mark.
#   pwsh -File tools/make-icon.ps1 -Out <path-to-wsnap.ico>
param([string]$Out = (Join-Path (Split-Path $PSScriptRoot -Parent) 'wsnap.ico'))

Add-Type -AssemblyName System.Drawing
$root = Split-Path $Out -Parent
$sizes = 16,24,32,48,64,128,256

# macOS systemBlue (dark mode). Lightly lifted (G→B) in the top-left for a soft specular feel —
# flat blue reads dull next to macOS app icons, which all carry a subtle gradient.
$BlueTop = [System.Drawing.Color]::FromArgb(255, 0x2A, 0x9C, 0xFF)
$BlueBot = [System.Drawing.Color]::FromArgb(255, 0x0A, 0x84, 0xFF)

function RoundedRectPath([double]$x, [double]$y, [double]$w, [double]$h, [double]$r) {
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $d = $r * 2
  $p.AddArc($x,         $y,         $d, $d, 180, 90)
  $p.AddArc($x + $w-$d, $y,         $d, $d, 270, 90)
  $p.AddArc($x + $w-$d, $y + $h-$d, $d, $d,   0, 90)
  $p.AddArc($x,         $y + $h-$d, $d, $d,  90, 90)
  $p.CloseFigure()
  return $p
}

function New-IconBitmap([int]$S) {
  $bmp = New-Object System.Drawing.Bitmap($S, $S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
  $g.Clear([System.Drawing.Color]::Transparent)

  # squircle tile — macOS app-icon proportion (~22.5% corner radius, 7% margin).
  $m   = [double]$S * 0.07
  $w   = $S - 2*$m
  $rad = [double]$S * 0.225
  $tile = RoundedRectPath $m $m $w $w $rad
  $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.PointF(0, 0)),
    (New-Object System.Drawing.PointF([single]$S, [single]$S)),
    $BlueTop, $BlueBot)
  $g.FillPath($brush, $tile)
  $brush.Dispose()

  # selection-region mark: white rounded-rectangle outline + a filled dot at each corner.
  # Same geometry the live overlay draws around the user's drag, so the app icon and the
  # capture UX share one vocabulary.
  $mx = [double]$S * 0.30      # selection rect inset
  $mw = [double]$S * 0.40      # selection rect side
  $mr = [double]$S * 0.055     # selection rect corner radius
  $outline = RoundedRectPath $mx $mx $mw $mw $mr
  $stroke = [Math]::Max(1.4, $S * 0.075)
  $wpen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, [single]$stroke)
  $wpen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
  $wpen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
  $wpen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
  $g.DrawPath($wpen, $outline)

  # Corner handles only when the icon is big enough that a dot won't blob into the outline.
  # Below 48px the outline alone carries the symbol; dots would just smear.
  if ($S -ge 48) {
    $dotR = [double]$S * 0.055
    $corners = @(
      (New-Object System.Drawing.PointF([single]$mx,            [single]$mx)),
      (New-Object System.Drawing.PointF([single]($mx + $mw),    [single]$mx)),
      (New-Object System.Drawing.PointF([single]$mx,            [single]($mx + $mw))),
      (New-Object System.Drawing.PointF([single]($mx + $mw),    [single]($mx + $mw)))
    )
    $wfill = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    foreach ($c in $corners) {
      $g.FillEllipse($wfill, [single]($c.X - $dotR), [single]($c.Y - $dotR), [single]($dotR*2), [single]($dotR*2))
    }
    $wfill.Dispose()
  }

  $wpen.Dispose(); $outline.Dispose(); $tile.Dispose(); $g.Dispose()
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
