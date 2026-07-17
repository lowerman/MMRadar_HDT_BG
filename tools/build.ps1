# Builds the MMRadar plugin (and harness).
# Auto-detects the newest Hearthstone Deck Tracker app folder for assembly references.
param(
    [string]$Configuration = "Release",
    [string]$HdtDir = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

if (-not $HdtDir) {
    $hdtRoot = Join-Path $env:LOCALAPPDATA "HearthstoneDeckTracker"
    $appDirs = Get-ChildItem $hdtRoot -Directory -Filter "app-*" -ErrorAction SilentlyContinue |
        Where-Object { Test-Path (Join-Path $_.FullName "HearthstoneDeckTracker.exe") } |
        Sort-Object { [version]($_.Name -replace '^app-', '') } -Descending
    if (-not $appDirs) {
        throw "Hearthstone Deck Tracker not found under $hdtRoot. Install HDT or pass -HdtDir."
    }
    $HdtDir = $appDirs[0].FullName
}

Write-Host "Using HDT assemblies from: $HdtDir"
dotnet build "$root\MMRadar.sln" -c $Configuration -p:HdtDir=$HdtDir
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

Write-Host ""
Write-Host "Plugin DLL: $root\src\MMRadar\bin\$Configuration\MMRadar.dll"
