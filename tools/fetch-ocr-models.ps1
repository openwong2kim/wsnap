# wsnap — fetch the Korean OCR recognition model + dictionary.
#
# The detection (ch_PP-OCRv5_mobile_det) and angle-classification models ship inside the
# RapidOcrNet NuGet package and are restored automatically. Only the Korean recognition model
# (covers KO + EN + digits + symbols) and its matching dictionary are fetched here, into
# models\v5\, where Wsnap.csproj bundles them into the single-file exe.
#
# These two files are committed to the repo so CI builds are hermetic; run this only to
# (re)download or update them.
#
# Usage:  pwsh -File tools\fetch-ocr-models.ps1 [-Force]
param([switch]$Force)

$ErrorActionPreference = 'Stop'
$dir = Join-Path $PSScriptRoot '..\models\v5'
New-Item -ItemType Directory -Force $dir | Out-Null

$base = 'https://huggingface.co/monkt/paddleocr-onnx/resolve/main/languages/korean'
$files = @(
  @{ Url = "$base/rec.onnx";  Dest = 'korean_rec.onnx';  MinBytes = 5MB },
  @{ Url = "$base/dict.txt";  Dest = 'korean_dict.txt';  MinBytes = 10KB }
)

foreach ($f in $files) {
  $dest = Join-Path $dir $f.Dest
  if ((Test-Path $dest) -and -not $Force) {
    Write-Host "skip  $($f.Dest) (exists; use -Force to re-download)" -ForegroundColor DarkGray
    continue
  }
  Write-Host "fetch $($f.Dest) ..."
  Invoke-WebRequest -Uri "$($f.Url)?download=true" -OutFile $dest -TimeoutSec 180 -UseBasicParsing
  $sz = (Get-Item $dest).Length
  if ($sz -lt $f.MinBytes) {
    Remove-Item $dest -Force
    throw "Downloaded $($f.Dest) is too small ($sz bytes) — likely an error page, not the model."
  }
  Write-Host ("  ok  {0}  =>  {1:N2} MB" -f $f.Dest, ($sz / 1MB)) -ForegroundColor Green
}

Write-Host ""
Write-Host "Korean OCR models ready in models\v5\." -ForegroundColor Green
