param(
    [string]$GameDir = "D:\SteamLibrary\steamapps\common\How to Fish\How to Fish",
    [switch]$RemoveBepInEx
)

$ErrorActionPreference = "Stop"

$PluginDest = Join-Path $GameDir "BepInEx\plugins\HowToFishVR"
if (Test-Path $PluginDest) {
    Remove-Item $PluginDest -Recurse -Force
    Write-Host "Removed $PluginDest" -ForegroundColor Yellow
}

if ($RemoveBepInEx) {
    foreach ($item in @("winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt", "BepInEx")) {
        $target = Join-Path $GameDir $item
        if (Test-Path $target) {
            Remove-Item $target -Recurse -Force
            Write-Host "Removed $target" -ForegroundColor Yellow
        }
    }
}

Write-Host "Uninstall complete." -ForegroundColor Green
