# Builds a portable release folder + zip for mod makers:
#   dist\STS2CardForge\
#     STS2 Card Forge.bat      <- double-click to start
#     app\CardForge.exe        (self-contained .NET + Windows App SDK, no installs needed)
#     app\python\              (embeddable Python + the backend's packages; runs the backend)
#     backend\                 (Python API server)
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

# The backend's own Python (python.org embeddable + backend\requirements.txt), so cloud mode works right after
# unzipping; local mode still downloads ComfyUI, which keeps its own Python. Same minor version as ComfyUI's.
$pyVersion = "3.13.14"
$pySha256 = "90b4e5b9898b72d744650524bff92377c367f44bd5fbd09e3148656c080ad907"   # from python.org's release page

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force $dist | Out-Null

# separate artifacts folder: releases build without touching (or being blocked by) a running dev build
dotnet publish (Join-Path $root "app\CardForge\CardForge.csproj") -c Release -r win-x64 --self-contained `
    --artifacts-path (Join-Path $root "dist\_build") -o (Join-Path $dist "app")
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

New-Item -ItemType Directory -Force (Join-Path $dist "backend") | Out-Null
Copy-Item (Join-Path $root "backend\*.py"), (Join-Path $root "backend\requirements.txt") (Join-Path $dist "backend")
if (Test-Path (Join-Path $root "backend\spine_renderer")) {
    Push-Location (Join-Path $root "backend\spine_renderer")
    try {
        & npm.cmd ci --omit=dev
        if ($LASTEXITCODE -ne 0) { throw "npm ci for spine renderer failed" }
    } finally { Pop-Location }
    Copy-Item (Join-Path $root "backend\spine_renderer") (Join-Path $dist "backend") -Recurse
}
$cache = Join-Path $root "dist\_cache"
New-Item -ItemType Directory -Force $cache | Out-Null
$pyZip = Join-Path $cache "python-$pyVersion-embed-amd64.zip"
if (-not (Test-Path $pyZip)) {
    Invoke-WebRequest "https://www.python.org/ftp/python/$pyVersion/python-$pyVersion-embed-amd64.zip" -OutFile $pyZip -UseBasicParsing
}
if ((Get-FileHash $pyZip -Algorithm SHA256).Hash -ne $pySha256) { Remove-Item $pyZip; throw "embeddable Python checksum mismatch" }
# inside app\ on purpose: every updater, including 0.4.0's, mirrors app\ wholesale, so existing installs receive it
$py = Join-Path $dist "app\python"
Expand-Archive $pyZip $py
# the embeddable build ignores site-packages until "import site" is enabled in its ._pth file
$pth = Get-ChildItem $py -Filter "python*._pth" | Select-Object -First 1
(Get-Content $pth.FullName) -replace '^#\s*import site', 'import site' | Set-Content -Encoding ascii $pth.FullName
# pip runs from the build machine's Python; packages are pinned in requirements.txt
& python -m pip install --disable-pip-version-check --no-warn-script-location --only-binary=:all: `
    --platform win_amd64 --python-version $pyVersion --implementation cp `
    --target (Join-Path $py "Lib\site-packages") -r (Join-Path $root "backend\requirements.txt")
if ($LASTEXITCODE -ne 0) { throw "pip install for the bundled Python failed" }
& (Join-Path $py "python.exe") -s -c "import aiohttp, PIL, anthropic"
if ($LASTEXITCODE -ne 0) { throw "bundled Python cannot import the backend packages" }

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
