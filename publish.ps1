# wsnap — produce a single self-contained .exe in .\publish\
# Usage:  pwsh -File publish.ps1
$ErrorActionPreference = 'Stop'

$proj = Join-Path $PSScriptRoot 'Wsnap.csproj'
$out  = Join-Path $PSScriptRoot 'publish'

dotnet publish $proj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o $out

Write-Host ""
Write-Host "Built: $(Join-Path $out 'wsnap.exe')" -ForegroundColor Green
Write-Host "NOTE: ship it code-signed to avoid SmartScreen warnings (see ROADMAP v1.0)." -ForegroundColor Yellow
