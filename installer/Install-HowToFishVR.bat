@echo off
setlocal EnableExtensions
title How To Fish VR Mod v1.0.0 - One-Click Installer

:: ============================================================================
:: How To Fish VR Mod - Transparent One-Click Installer (v1.0.0)
:: ----------------------------------------------------------------------------
:: WHAT THIS SCRIPT DOES (100% Offline & Safe to Inspect in Notepad):
::   1. Verifies that you extracted the .zip archive first.
::   2. Finds your Steam "How to Fish" folder automatically by reading Steam's
::      local libraryfolders.vdf (or lets you pick the folder if custom).
::   3. Copies ONLY the VR mod files into your "How to Fish" game folder:
::        - BepInEx\               (Open-source Unity mod loader + HowToFishVR)
::        - winhttp.dll            (Standard BepInEx Unity Doorstop proxy)
::        - doorstop_config.ini    (Standard BepInEx Unity Doorstop config)
::        - Uninstall-HowToFishVR.bat
::   4. Makes ZERO internet connections, ZERO registry edits, and NEVER touches
::      your save files. Prefer manual install? See README.md!
:: ============================================================================

echo.
echo  ============================================================
echo    How To Fish VR Mod v1.0.0  -  One-Click Installer
echo  ============================================================
echo.

set "SOURCE_DIR=%~dp0"

:: 1. Ensure the user extracted the .zip first (not running from inside .zip preview)
if not exist "%SOURCE_DIR%BepInEx\plugins\HowToFishVR\HowToFishVR.dll" (
    echo  [!] Mod files were not found next to this script.
    echo.
    echo      It looks like you opened Install-HowToFishVR.bat directly from inside
    echo      the .zip preview window without extracting it first.
    echo.
    echo      Please right-click the .zip file, choose "Extract All...", and then
    echo      double-click Install-HowToFishVR.bat inside the extracted folder.
    echo.
    pause
    exit /b 1
)

:: 2. If the user already extracted the .zip straight into the game folder, we are done!
if exist "%SOURCE_DIR%How to Fish.exe" (
    echo  [OK] Detected "How to Fish.exe" right inside this folder:
    echo       "%SOURCE_DIR%"
    echo.
    echo  [SUCCESS] How To Fish VR Mod v1.0.0 is already in place and ready to play!
    echo            Start SteamVR / Quest Link / Virtual Desktop, then launch How to Fish.
    echo.
    pause
    exit /b 0
)

:: 3. Auto-detect "How to Fish" across all Steam libraries (or open folder picker if needed)
echo  [*] Searching Steam libraries for "How to Fish"...
set "GAME_DIR="

for /f "usebackq delims=" %%G in (`powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ^
    "$found = $null; " ^
    "$roots = @('C:\Program Files (x86)\Steam', 'C:\Program Files\Steam'); " ^
    "try { $rk = Get-ItemProperty 'HKCU:\Software\Valve\Steam' -EA SilentlyContinue; if ($rk.SteamPath) { $roots = @($rk.SteamPath.Replace('/','\')) + $roots } } catch {}; " ^
    "try { $rk2 = Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam' -EA SilentlyContinue; if ($rk2.InstallPath) { $roots += $rk2.InstallPath } } catch {}; " ^
    "$libs = [System.Collections.Generic.List[string]]::new(); " ^
    "foreach ($r in $roots) { if ($r -and (Test-Path $r)) { $libs.Add($r); $vdf = Join-Path $r 'steamapps\libraryfolders.vdf'; if (Test-Path $vdf) { foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '\"path\"\s+\"([^\"]+)\"')) { $libs.Add(($m.Groups[1].Value -replace '\\\\','\')) } } } }; " ^
    "foreach ($l in ($libs | Select-Object -Unique)) { foreach ($sub in @('steamapps\common\How to Fish\How to Fish', 'steamapps\common\How to Fish')) { $cand = Join-Path $l $sub; if (Test-Path (Join-Path $cand 'How to Fish.exe')) { $found = $cand; break } }; if ($found) { break } }; " ^
    "if (-not $found) { Add-Type -AssemblyName System.Windows.Forms; $dlg = New-Object System.Windows.Forms.FolderBrowserDialog; $dlg.Description = 'Select your How to Fish folder (containing How to Fish.exe)'; if ($dlg.ShowDialog() -eq 'OK') { if (Test-Path (Join-Path $dlg.SelectedPath 'How to Fish.exe')) { $found = $dlg.SelectedPath } elseif (Test-Path (Join-Path $dlg.SelectedPath 'How to Fish\How to Fish.exe')) { $found = (Join-Path $dlg.SelectedPath 'How to Fish') } } }; " ^
    "if ($found) { Write-Output $found }"`) do (
    set "GAME_DIR=%%G"
)

if not defined GAME_DIR (
    echo.
    echo  [!] Could not locate "How to Fish.exe".
    echo      Please make sure How to Fish is installed via Steam, or follow the
    echo      step-by-step Manual Installation guide in README.md.
    echo.
    pause
    exit /b 1
)

if not exist "%GAME_DIR%\How to Fish.exe" (
    echo.
    echo  [!] Selected folder does not contain "How to Fish.exe":
    echo      "%GAME_DIR%"
    echo.
    pause
    exit /b 1
)

echo  [OK] Found game folder:
echo       "%GAME_DIR%"
echo.
echo  [*] Copying BepInEx + HowToFishVR v1.0.0 files...

xcopy /E /I /Y "%SOURCE_DIR%BepInEx" "%GAME_DIR%\BepInEx" >nul
if errorlevel 1 (
    echo  [!] Failed to copy BepInEx folder. Make sure "How to Fish" is closed and try again.
    pause
    exit /b 1
)

copy /Y "%SOURCE_DIR%winhttp.dll" "%GAME_DIR%\winhttp.dll" >nul
if errorlevel 1 (
    echo  [!] Failed to copy winhttp.dll. Make sure "How to Fish" is closed and try again.
    pause
    exit /b 1
)

copy /Y "%SOURCE_DIR%doorstop_config.ini" "%GAME_DIR%\doorstop_config.ini" >nul
if exist "%SOURCE_DIR%Uninstall-HowToFishVR.bat" (
    copy /Y "%SOURCE_DIR%Uninstall-HowToFishVR.bat" "%GAME_DIR%\Uninstall-HowToFishVR.bat" >nul
)

echo.
echo  ============================================================
echo   [SUCCESS] How To Fish VR Mod v1.0.0 installed!
echo  ============================================================
echo.
echo   Next steps:
echo     1. Start your VR runtime (SteamVR, Quest Link, or Virtual Desktop).
echo     2. Launch "How to Fish" normally from Steam.
echo     3. Turn your LEFT WRIST toward your face in-game to open the VR Menu!
echo.
echo   (To uninstall later, run Uninstall-HowToFishVR.bat in your game folder.)
echo.
pause
exit /b 0
