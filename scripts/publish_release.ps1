param(
    [ValidateSet("none", "patch", "minor", "generation")]
    [string]$Bump = "none",
    [string]$Notes = "",
    [string]$NotesFile = "",
    [switch]$Prerelease,
    [switch]$SkipNative
)

# ==============================================================================
# How To Fish VR Mod - Automated Versioned GitHub Release Publisher
# ------------------------------------------------------------------------------
# Publishes a standalone GitHub Release for every version iteration:
#   - Small / Hotfix update: .\scripts\publish_release.ps1 -Bump patch   (+0.0.1)
#   - Major / Feature update: .\scripts\publish_release.ps1 -Bump minor  (+0.1.0)
#   - Capped at vX.9.9 before rolling over cleanly to v(X+1).0.0
# ==============================================================================

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $RepoRoot

try {
    if ($Bump -ne "none") {
        & "$PSScriptRoot\bump_version.ps1" -BumpType $Bump | Out-Null
    }

    [xml]$csprojXml = Get-Content "$RepoRoot\src\HowToFishVR\HowToFishVR.csproj"
    $Version = ($csprojXml.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version.Trim()
    $Tag = "v$Version"

    & "$PSScriptRoot\build_all.ps1" -SkipNative:$SkipNative

    $ZipPath = "$RepoRoot\dist\HowToFish-VRMod-$Tag.zip"
    if (-not (Test-Path $ZipPath)) {
        throw "Expected release archive not found: $ZipPath"
    }

    $status = git status --porcelain
    if ($status) {
        git add -A
        git commit -m "Release $Tag"
    }

    $existingTag = git tag -l $Tag
    if (-not $existingTag) {
        git tag -a $Tag -m "How To Fish VR Mod $Tag"
    }

    git push origin HEAD
    git push origin $Tag

    $ghArgs = @("release", "create", $Tag, $ZipPath, "--title", "How To Fish VR Mod $Tag")
    if ($Prerelease) { $ghArgs += "--prerelease" }

    if ($NotesFile -and (Test-Path $NotesFile)) {
        $ghArgs += @("--notes-file", $NotesFile)
    }
    elseif ($Notes) {
        $ghArgs += @("--notes", $Notes)
    }
    else {
        $ghArgs += "--generate-notes"
    }

    & gh @ghArgs
    if ($LASTEXITCODE -ne 0) {
        throw "gh release create failed with exit code $LASTEXITCODE"
    }

    Write-Host "Published GitHub Release $Tag successfully!" -ForegroundColor Green
}
finally {
    Pop-Location
}
