param(
    [switch]$SkipNative
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$CsprojPath = "$RepoRoot\src\HowToFishVR\HowToFishVR.csproj"

[xml]$csprojXml = Get-Content $CsprojPath
$Version = ($csprojXml.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version.Trim()

Write-Host "=== Building How To Fish VR Mod v$Version ===" -ForegroundColor Cyan

if (-not $SkipNative) {
    Write-Host "[1/3] Building Native C++17 OpenXR+D3D11 Bridge..." -ForegroundColor Yellow
    & cmd.exe /c "`"$RepoRoot\native\build_native.bat`""
    if ($LASTEXITCODE -ne 0) {
        throw "Native build failed with exit code $LASTEXITCODE"
    }
}

Write-Host "[2/3] Building Managed C# BepInEx Plugin..." -ForegroundColor Yellow
dotnet build $CsprojPath -c Release
if ($LASTEXITCODE -ne 0) {
    throw "Managed C# build failed with exit code $LASTEXITCODE"
}

Write-Host "[3/3] Packaging Release Archive (v$Version)..." -ForegroundColor Yellow
$StageDir = "$RepoRoot\build\package_stage"
$DistDir = "$RepoRoot\dist"
$ZipPath = "$DistDir\HowToFish-VRMod-v$Version.zip"

if (Test-Path $StageDir) { Remove-Item $StageDir -Recurse -Force }
New-Item -ItemType Directory -Path "$StageDir\BepInEx\plugins\HowToFishVR" -Force | Out-Null
New-Item -ItemType Directory -Path $DistDir -Force | Out-Null

# Copy BepInEx 5.4.23.6 x64 base files (doorstop_config.ini, winhttp.dll, BepInEx/core/*)
Copy-Item "$RepoRoot\third_party\BepInEx\*" -Destination $StageDir -Recurse -Force

# Copy VR Mod binaries + OpenXR loader
Copy-Item "$RepoRoot\build\managed\HowToFishVR.dll" -Destination "$StageDir\BepInEx\plugins\HowToFishVR\" -Force
Copy-Item "$RepoRoot\build\native\HowToFishVRNative.dll" -Destination "$StageDir\BepInEx\plugins\HowToFishVR\" -Force
Copy-Item "$RepoRoot\third_party\openxr\x64\bin\openxr_loader.dll" -Destination "$StageDir\BepInEx\plugins\HowToFishVR\" -Force

# Copy One-Click Installer, Uninstaller, and README to root of .zip archive
if (Test-Path "$RepoRoot\installer\Install-HowToFishVR.bat") {
    Copy-Item "$RepoRoot\installer\Install-HowToFishVR.bat" -Destination "$StageDir\Install-HowToFishVR.bat" -Force
}
if (Test-Path "$RepoRoot\installer\Uninstall-HowToFishVR.bat") {
    Copy-Item "$RepoRoot\installer\Uninstall-HowToFishVR.bat" -Destination "$StageDir\Uninstall-HowToFishVR.bat" -Force
}
if (Test-Path "$RepoRoot\README.md") {
    Copy-Item "$RepoRoot\README.md" -Destination "$StageDir\README.md" -Force
    Copy-Item "$RepoRoot\README.md" -Destination "$StageDir\BepInEx\plugins\HowToFishVR\README.md" -Force
}

if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
Compress-Archive -Path "$StageDir\*" -DestinationPath $ZipPath -CompressionLevel Optimal

Write-Host "Successfully packaged: $ZipPath" -ForegroundColor Green
