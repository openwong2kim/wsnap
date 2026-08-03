# Generates wsnap.ico (multi-resolution) + site/icon.png from code — no binary assets in git.
# macOS-native app icon: a systemBackground squircle (dark-mode grey) carrying a slim white "W"
# drawn as a stroked path — no font dependency, identical on every build machine. Reads instantly
# as "wsnap" at every size (16px taskbar to 256px store tile). Supersedes the systemBlue +
# selection-region mark, which read as busy next to macOS system chrome.
#   pwsh -File tools/make-icon.ps1 -Out <path-to-wsnap.ico>
param([string]$Out = (Join-Path (Split-Path $PSScriptRoot -Parent) 'wsnap.ico'))

Add-Type -AssemblyName System.Drawing
$root = Split-Path $Out -Parent
$sizes = 16,24,32,48,64,128,256

# macOS systemBackground greys (HIG dark mode). Lifted very slightly top-left for a soft specular
# feel — flat grey reads dead next to macOS app icons, which all carry a subtle gradient.
$GreyTop = [System.Drawing.Color]::FromArgb(255, 0x2C, 0x2C, 0x2E)  # secondarySystemBackground
$GreyBot = [System.Drawing.Color]::FromArgb(255, 0x1C, 0x1C, 0x1E)  # systemBackground

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
    $GreyTop, $GreyBot)
  $g.FillPath($brush, $tile)
  $brush.Dispose()

  # W glyph: four connected diagonals stroked in white — a slim, native-feel mark instead of a
  # heavy font glyph (no font dependency, identical output on every build machine). Same inset
  # the old selection-region mark used, so the icon keeps its visual weight in the toolbar.
  $mx = [double]$S * 0.30      # glyph inset
  $mw = [double]$S * 0.40      # glyph side
  $wstroke = [Math]::Max(2.0, $S * 0.07)
  $wpen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, [single]$wstroke)
  $wpen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
  $wpen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
  $wpen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
  [System.Drawing.PointF[]]$pts = @(
    (New-Object System.Drawing.PointF([single]($mx + $mw*0.00), [single]($mx + $mw*0.00))),
    (New-Object System.Drawing.PointF([single]($mx + $mw*0.25), [single]($mx + $mw*1.00))),
    (New-Object System.Drawing.PointF([single]($mx + $mw*0.50), [single]($mx + $mw*0.35))),
    (New-Object System.Drawing.PointF([single]($mx + $mw*0.75), [single]($mx + $mw*1.00))),
    (New-Object System.Drawing.PointF([single]($mx + $mw*1.00), [single]($mx + $mw*0.00)))
  )
  $g.DrawLines($wpen, $pts)

  $wpen.Dispose(); $tile.Dispose(); $g.Dispose()
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
