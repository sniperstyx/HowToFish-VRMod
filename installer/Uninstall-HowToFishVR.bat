@echo off
setlocal EnableExtensions
title How To Fish VR Mod - Uninstaller

:: ============================================================================
:: How To Fish VR Mod - Clean Uninstaller
:: ----------------------------------------------------------------------------
:: Removes HowToFishVR and BepInEx loader files so the game launches in 100%
:: stock flatscreen mode again. Never touches your How to Fish save files!
:: ============================================================================

echo.
echo  ============================================================
echo    How To Fish VR Mod  -  Clean Uninstaller
echo  ============================================================
echo.

set "TARGET_DIR=%~dp0"

if not exist "%TARGET_DIR%How to Fish.exe" (
    for /f "usebackq delims=" %%G in (`powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ^
        "$found = $null; " ^
        "$roots = @('C:\Program Files (x86)\Steam', 'C:\Program Files\Steam'); " ^
        "try { $rk = Get-ItemProperty 'HKCU:\Software\Valve\Steam' -EA SilentlyContinue; if ($rk.SteamPath) { $roots = @($rk.SteamPath.Replace('/','\')) + $roots } } catch {}; " ^
        "$libs = [System.Collections.Generic.List[string]]::new(); " ^
        "foreach ($r in $roots) { if ($r -and (Test-Path $r)) { $libs.Add($r); $vdf = Join-Path $r 'steamapps\libraryfolders.vdf'; if (Test-Path $vdf) { foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '\"path\"\s+\"([^\"]+)\"')) { $libs.Add(($m.Groups[1].Value -replace '\\\\','\')) } } } }; " ^
        "foreach ($l in ($libs | Select-Object -Unique)) { foreach ($sub in @('steamapps\common\How to Fish\How to Fish', 'steamapps\common\How to Fish')) { $cand = Join-Path $l $sub; if (Test-Path (Join-Path $cand 'How to Fish.exe')) { $found = $cand; break } }; if ($found) { break } }; " ^
        "if ($found) { Write-Output $found }"`) do (
        set "TARGET_DIR=%%G\"
    )
)

if not exist "%TARGET_DIR%How to Fish.exe" (
    echo  [!] Could not locate "How to Fish.exe".
    echo      Place this script next to "How to Fish.exe" or delete winhttp.dll,
    echo      doorstop_config.ini, and the BepInEx folder manually.
    echo.
    pause
    exit /b 1
)

echo  [*] Removing VR Mod files from:
echo      "%TARGET_DIR%"
echo.

if exist "%TARGET_DIR%BepInEx\plugins\HowToFishVR" rd /S /Q "%TARGET_DIR%BepInEx\plugins\HowToFishVR"
if exist "%TARGET_DIR%winhttp.dll" del /F /Q "%TARGET_DIR%winhttp.dll"
if exist "%TARGET_DIR%doorstop_config.ini" del /F /Q "%TARGET_DIR%doorstop_config.ini"
if exist "%TARGET_DIR%.doorstop_version" del /F /Q "%TARGET_DIR%.doorstop_version"

:: Remove BepInEx folder only if no other third-party BepInEx plugins are installed
set "OTHER_PLUGINS="
if exist "%TARGET_DIR%BepInEx\plugins" (
    for /f "delims=" %%P in ('dir /b "%TARGET_DIR%BepInEx\plugins" 2^>nul') do set "OTHER_PLUGINS=1"
)
if not defined OTHER_PLUGINS (
    if exist "%TARGET_DIR%BepInEx" rd /S /Q "%TARGET_DIR%BepInEx"
)

echo  [SUCCESS] How To Fish VR Mod has been cleanly uninstalled!
echo            Your save games were left completely untouched.
echo.
pause
exit /b 0
