# wsnap — produce a single-file .exe in .\publish\
# Usage:  pwsh -File publish.ps1
#         pwsh -File publish.ps1 -SelfContained   # legacy fat build (~170 MB, no runtime dep)
param(
    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'

$proj = Join-Path $PSScriptRoot 'Wsnap.csproj'
$out  = Join-Path $PSScriptRoot 'publish'

# Default (v2.0+): framework-dependent. The exe drops from ~170 MB to ~9 MB by depending on the
# Microsoft .NET 8 Desktop Runtime (one-time system install, like ShareX depends on .NET Framework).
# The big native (libSkiaSharp.dll ~11 MB) still ships loose next to the exe; OCR's ONNX Runtime
# is fetched on first OCR. Net: ~21 MB total vs ~170 MB.
# Pass -SelfContained for the legacy all-in-one build (no runtime prerequisite).
if ($SelfContained) {
    dotnet publish $proj `
      -c Release -r win-x64 --self-contained true `
      -p:PublishSingleFile=true `
      -p:IncludeNativeLibrariesForSelfExtract=true `
      -p:EnableCompressionInSingleFile=false `
      -o $out
} else {
    dotnet publish $proj `
      -c Release -r win-x64 --self-contained false `
      -p:PublishSingleFile=true `
      -o $out
}

Write-Host ""
if ($SelfContained) {
    Write-Host "Built (self-contained, no runtime dep): $(Join-Path $out 'wsnap.exe')" -ForegroundColor Green
} else {
    $mb = [math]::Round((Get-ChildItem $out -Recurse | Measure-Object -Property Length -Sum).Sum / 1MB, 1)
    Write-Host "Built (framework-dependent, needs .NET 8 Desktop Runtime): $out  ($mb MB total)" -ForegroundColor Green
}
Write-Host "NOTE: ship it code-signed to avoid SmartScreen warnings (see ROADMAP v1.0)." -ForegroundColor Yellow
