param(
    [string]$GameDir = "D:\SteamLibrary\steamapps\common\How to Fish\How to Fish"
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot

if (-not (Test-Path "$GameDir\How to Fish.exe")) {
    throw "Could not find 'How to Fish.exe' in '$GameDir'. Pass -GameDir to specify your install directory."
}

$ManagedDll = "$RepoRoot\build\managed\HowToFishVR.dll"
$NativeDll  = "$RepoRoot\build\native\HowToFishVRNative.dll"
$OpenXrDll  = "$RepoRoot\third_party\openxr\x64\bin\openxr_loader.dll"

if (-not (Test-Path $ManagedDll)) { throw "Missing $ManagedDll - run scripts\build_all.ps1 first." }
if (-not (Test-Path $NativeDll))  { throw "Missing $NativeDll - run scripts\build_all.ps1 first." }
if (-not (Test-Path $OpenXrDll))  { throw "Missing $OpenXrDll" }

Write-Host "Installing BepInEx 5.4.23.6 x64 to '$GameDir'..." -ForegroundColor Cyan
Copy-Item "$RepoRoot\third_party\BepInEx\*" -Destination $GameDir -Recurse -Force

$PluginDest = Join-Path $GameDir "BepInEx\plugins\HowToFishVR"
New-Item -ItemType Directory -Path $PluginDest -Force | Out-Null

Write-Host "Installing HowToFishVR plugin & OpenXR bridge to '$PluginDest'..." -ForegroundColor Cyan
Copy-Item $ManagedDll -Destination $PluginDest -Force
Copy-Item $NativeDll  -Destination $PluginDest -Force
Copy-Item $OpenXrDll  -Destination $PluginDest -Force

if (Test-Path "$RepoRoot\README.md") {
    Copy-Item "$RepoRoot\README.md" -Destination "$PluginDest\README.md" -Force
}

Write-Host "How To Fish VR Mod installed successfully!" -ForegroundColor Green
