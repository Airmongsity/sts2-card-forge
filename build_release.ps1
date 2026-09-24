# Builds a portable release folder + zip for mod makers:
#   dist\STS2CardForge\
#     STS2 Card Forge.bat      <- double-click to start
#     app\CardForge.exe        (self-contained .NET + Windows App SDK, no installs needed)
#     backend\                 (Python API server; runs on ComfyUI's embedded Python)
#     examples\                (sample cards + art seeded into the Examples project on first run)
#     README.md, NOTICE, VERSION
# Nothing heavy is bundled: the Setup page checks the machine first and only then downloads ComfyUI (the package
# matching the GPU), the models and the style LoRA (~14 GB), so users without a suitable GPU lose no bandwidth.
#
#   powershell -ExecutionPolicy Bypass -File build_release.ps1 [-NoZip]
# Publish: create a GitHub release tagged v<VERSION> with dist\STS2CardForge.zip attached; installed apps find it
# through the update check.
param([switch]$NoZip)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dist = Join-Path $root "dist\STS2CardForge"
$version = (Get-Content (Join-Path $root "VERSION") -Raw).Trim()

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force $dist | Out-Null

# separate artifacts folder: releases build without touching (or being blocked by) a running dev build
dotnet publish (Join-Path $root "app\CardForge\CardForge.csproj") -c Release -r win-x64 --self-contained `
    --artifacts-path (Join-Path $root "dist\_build") -o (Join-Path $dist "app")
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

New-Item -ItemType Directory -Force (Join-Path $dist "backend") | Out-Null
Copy-Item (Join-Path $root "backend\*.py"), (Join-Path $root "backend\requirements.txt") (Join-Path $dist "backend")
Copy-Item (Join-Path $root "README.md"), (Join-Path $root "NOTICE"), (Join-Path $root "VERSION") $dist
New-Item -ItemType Directory -Force (Join-Path $dist "examples") | Out-Null
Copy-Item (Join-Path $root "examples\cards.json"), (Join-Path $root "examples\*.jpg") (Join-Path $dist "examples")
Set-Content -Encoding ascii (Join-Path $dist "STS2 Card Forge.bat") "@start `"`" `"%~dp0app\CardForge.exe`""

if (-not $NoZip) {
    $zip = Join-Path $root "dist\STS2CardForge.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path $dist -DestinationPath $zip -CompressionLevel Optimal
    "zip: $zip ($([math]::Round((Get-Item $zip).Length / 1MB)) MB), attach it to release tag v$version"
}
"release folder: $dist"
