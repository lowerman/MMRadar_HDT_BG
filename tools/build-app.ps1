# Builds the standalone MMRadar app (1.1.x) — no Hearthstone Deck Tracker needed.
# The plugin keeps its own build (tools/build.ps1); this script never touches it.
param(
    [string]$Configuration = "Release",
    [switch]$Zip
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $root "src\MMRadar.App\MMRadar.App.csproj"

dotnet build $proj -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

$out = Join-Path $root "src\MMRadar.App\bin\$Configuration"
Write-Host ""
Write-Host "App: $out\MMRadar.exe"

if ($Zip) {
    [xml]$csproj = Get-Content $proj
    $version = ($csproj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
    $stage = Join-Path $out "package"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory $stage | Out-Null
    # Only what the app needs at runtime, plus the vendored reader's license.
    foreach ($f in "MMRadar.exe", "MMRadar.exe.config", "Newtonsoft.Json.dll") {
        Copy-Item (Join-Path $out $f) $stage
    }
    Copy-Item (Join-Path $root "vendor\UnitySpy\LICENSE") (Join-Path $stage "THIRD-PARTY-LICENSE-UnitySpy.txt")
    Copy-Item (Join-Path $PSScriptRoot "app-readme.txt") (Join-Path $stage "README.txt")
    $zipPath = Join-Path $out "MMRadar-v$version.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zipPath
    Write-Host "Zip: $zipPath"
    (Get-FileHash $zipPath -Algorithm SHA256).Hash
}
