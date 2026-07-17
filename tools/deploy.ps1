# Builds the plugin and copies it into HDT's Plugins folder.
# Close (or restart) Hearthstone Deck Tracker afterwards to pick up the new version.
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

& "$PSScriptRoot\build.ps1" -Configuration $Configuration

$dll = "$root\src\MMRadar\bin\$Configuration\MMRadar.dll"
$pluginsDir = Join-Path $env:APPDATA "HearthstoneDeckTracker\Plugins"
New-Item -ItemType Directory -Force $pluginsDir | Out-Null

$hdtRunning = Get-Process "HearthstoneDeckTracker" -ErrorAction SilentlyContinue
if ($hdtRunning) {
    Write-Warning "Hearthstone Deck Tracker is running — the DLL may be locked. Close HDT and re-run if the copy fails."
}

Copy-Item $dll $pluginsDir -Force
Write-Host "Deployed to $pluginsDir\MMRadar.dll"
Write-Host "Enable it in HDT: Options -> Tracker -> Plugins."
