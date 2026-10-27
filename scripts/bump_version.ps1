param(
    [ValidateSet("patch", "minor", "generation", "set")]
    [string]$BumpType = "patch",
    [string]$SetVersion = ""
)

# ==============================================================================
# How To Fish VR Mod - Version Bumper
# ------------------------------------------------------------------------------
# Versioning Rules:
#   - Small / Hotfix update (-BumpType patch):  +0.0.1  (e.g. v1.0.0 -> v1.0.1)
#   - Major / Feature update (-BumpType minor): +0.1.0  (e.g. v1.0.1 -> v1.1.0)
#   - Each minor/patch digit caps at 9 and rolls over cleanly:
#       v1.0.9 + patch -> v1.1.0
#       v1.9.9 + patch -> v2.0.0
#       v1.9.0 + minor -> v2.0.0
# ==============================================================================

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$CsprojPath = "$RepoRoot\src\HowToFishVR\HowToFishVR.csproj"
$PluginPath = "$RepoRoot\src\HowToFishVR\Plugin.cs"
$InstallerPath = "$RepoRoot\installer\Install-HowToFishVR.bat"

[xml]$csprojXml = Get-Content $CsprojPath
$currentVer = ($csprojXml.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version.Trim()

if ($currentVer -notmatch '^(\d+)\.([0-9])\.([0-9])$') {
    throw "Current version '$currentVer' in HowToFishVR.csproj does not match X.Y.Z (with Y,Z in 0..9)."
}

[int]$maj = $Matches[1]
[int]$min = $Matches[2]
[int]$pat = $Matches[3]

switch ($BumpType) {
    "patch" {
        $pat++
        if ($pat -gt 9) {
            $pat = 0
            $min++
            if ($min -gt 9) {
                $min = 0
                $maj++
            }
        }
    }
    "minor" {
        $pat = 0
        $min++
        if ($min -gt 9) {
            $min = 0
            $maj++
        }
    }
    "generation" {
        $maj++
        $min = 0
        $pat = 0
    }
    "set" {
        if ($SetVersion -notmatch '^(\d+)\.([0-9])\.([0-9])$') {
            throw "Invalid -SetVersion '$SetVersion'. Expected X.Y.Z where Y and Z are single digits 0..9 (e.g. 1.0.0, 1.9.9, 2.0.0)."
        }
        $maj = [int]$Matches[1]
        $min = [int]$Matches[2]
        $pat = [int]$Matches[3]
    }
}

$newVer = "$maj.$min.$pat"

# 1. Update HowToFishVR.csproj
$csprojRaw = Get-Content $CsprojPath -Raw
$csprojRaw = [regex]::Replace($csprojRaw, '<Version>[^<]+</Version>', "<Version>$newVer</Version>")
Set-Content -Path $CsprojPath -Value $csprojRaw -NoNewline -Encoding UTF8

# 2. Update Plugin.cs (drives Main Menu 3D badge, Wrist Menu stamp, and BepInPlugin version)
$pluginRaw = Get-Content $PluginPath -Raw
$pluginRaw = [regex]::Replace($pluginRaw, 'PluginVersion\s*=\s*"[^"]+"', "PluginVersion = `"$newVer`"")
Set-Content -Path $PluginPath -Value $pluginRaw -NoNewline -Encoding UTF8

# 3. Update Install-HowToFishVR.bat display banners
if (Test-Path $InstallerPath) {
    $instRaw = Get-Content $InstallerPath -Raw
    $instRaw = [regex]::Replace($instRaw, 'v\d+\.[0-9]\.[0-9]', "v$newVer")
    Set-Content -Path $InstallerPath -Value $instRaw -NoNewline -Encoding UTF8
}

Write-Host "Version bumped: v$currentVer -> v$newVer" -ForegroundColor Green
Write-Output $newVer
