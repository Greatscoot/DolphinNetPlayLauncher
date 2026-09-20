@echo off
setlocal EnableExtensions
cd /d "%~dp0"

set "APP_VERSION="
for /f "tokens=2 delims==" %%V in ('findstr /r /c:"internal const string AppVersion = " "DolphinNetPlayLauncher.cs"') do set "APP_VERSION=%%V"
if not defined APP_VERSION (
  echo ERROR: Could not read AppVersion from DolphinNetPlayLauncher.cs.
  pause
  exit /b 1
)

REM Normalize the extracted C# literal after the IF block.
REM Do not place these substitutions inside a parenthesized block: cmd.exe expands
REM %%APP_VERSION%% when the block is parsed, which would make every substitution
REM operate on the original quoted/semicolon-terminated value.
set "APP_VERSION=%APP_VERSION:;=%"
set "APP_VERSION=%APP_VERSION:"=%"
set "APP_VERSION=%APP_VERSION: =%"

REM Release preflight: fail instead of silently producing an incomplete ZIP.
call :require "DolphinNetPlayLauncher.exe" || goto :missing
call :require "SDL3.dll" || goto :missing
call :require "SDL3-LICENSE.txt" || goto :missing
call :require "README.md" || goto :missing
call :require "README.txt" || goto :missing
call :require "LICENSE" || goto :missing
call :require "THIRD-PARTY-NOTICES.txt" || goto :missing
call :require "CHANGELOG.md" || goto :missing
set "RELEASE_NOTES=RELEASE-NOTES-%APP_VERSION%.md"
call :require "%RELEASE_NOTES%" || goto :missing
call :require "SRM-INSTRUCTIONS.txt" || goto :missing
call :require "Documentation\Dolphin-NetPlay-Launcher-SRM-Setup-Guide-v6.pdf" || goto :missing
call :require "Documentation\Dolphin-NetPlay-Launcher-Friend-Groups-Guide-v1.pdf" || goto :missing
call :require "Documentation\FRIEND-GROUPS-AND-DNLGROUP.md" || goto :missing
call :require "Documentation\SESSION-BANNERS.md" || goto :missing
REM GitHub/README public media. Keep GIF + MP4 showcase pairs together so the README
REM can display inline animation while linking to higher-quality video; the hero is a static PNG.
for %%M in (
  "dolphin-netplay-launcher-hero.png"
  "dolphin-netplay-friend-groups-demo.gif"
  "dolphin-netplay-friend-groups-demo.mp4"
  "dolphin-netplay-steam-host-demo.gif"
  "dolphin-netplay-steam-host-demo.mp4"
  "dolphin-netplay-library-grid.gif"
  "dolphin-netplay-library-grid.mp4"
  "dolphin-netplay-sessions-browser.gif"
  "dolphin-netplay-sessions-browser.mp4"
) do call :require "Documentation\Media\%%~M" || goto :missing
call :require "Fonts\Outfit-Regular.ttf" || goto :missing
call :require "Fonts\Outfit-Bold.ttf" || goto :missing
call :require "Fonts\OFL-Outfit.txt" || goto :missing
call :require "Fonts\README-FONTS.txt" || goto :missing
call :require "Sounds\README-SOUNDS.txt" || goto :missing
call :require "Sounds\CUSTOM-SOUND-THEMES.txt" || goto :missing

REM Runtime theme logos. Source assets live under Assets\Icons so the repository
REM root stays clean. Copy only the approved seven runtime PNGs.
for %%I in (
  "DolphinNetPlayLauncher-icon.png"
  "DolphinNetPlayLauncher-icon-adventure.png"
  "DolphinNetPlayLauncher-icon-dark.png"
  "DolphinNetPlayLauncher-icon-indigo.png"
  "DolphinNetPlayLauncher-icon-light.png"
  "DolphinNetPlayLauncher-icon-oled.png"
  "DolphinNetPlayLauncher-icon-spice.png"
) do call :require "Assets\Icons\%%~I" || goto :missing

REM Built-in sound themes. Public releases intentionally include only these three.
for %%T in (Adventure Royal ClassicUI) do (
  for %%C in (navigate switch library_open library_close stage_game use_game launch confirm cancel clear_game error) do (
    call :require "Sounds\%%T\%%C.wav" || goto :missing
  )
)

set "ROOT=Release"
set "PKG=%ROOT%\DolphinNetPlayLauncher"
set "ZIP=%ROOT%\DolphinNetPlayLauncher-%APP_VERSION%-Windows-x64.zip"

if exist "%PKG%" rmdir /s /q "%PKG%"
if exist "%ZIP%" del /q "%ZIP%"
mkdir "%PKG%\Documentation" >nul
mkdir "%PKG%\Documentation\Media" >nul
mkdir "%PKG%\Assets" >nul
mkdir "%PKG%\Assets\Icons" >nul
mkdir "%PKG%\Fonts" >nul
mkdir "%PKG%\Sounds\Adventure" >nul
mkdir "%PKG%\Sounds\Royal" >nul
mkdir "%PKG%\Sounds\ClassicUI" >nul

copy /y "DolphinNetPlayLauncher.exe" "%PKG%\" >nul
copy /y "SDL3.dll" "%PKG%\" >nul
copy /y "SDL3-LICENSE.txt" "%PKG%\" >nul
copy /y "README.md" "%PKG%\" >nul
copy /y "README.txt" "%PKG%\" >nul
copy /y "LICENSE" "%PKG%\" >nul
copy /y "THIRD-PARTY-NOTICES.txt" "%PKG%\" >nul
copy /y "CHANGELOG.md" "%PKG%\" >nul
copy /y "%RELEASE_NOTES%" "%PKG%\" >nul
copy /y "SRM-INSTRUCTIONS.txt" "%PKG%\" >nul
copy /y "Documentation\Dolphin-NetPlay-Launcher-SRM-Setup-Guide-v6.pdf" "%PKG%\Documentation\" >nul
copy /y "Documentation\Dolphin-NetPlay-Launcher-Friend-Groups-Guide-v1.pdf" "%PKG%\Documentation\" >nul
copy /y "Documentation\FRIEND-GROUPS-AND-DNLGROUP.md" "%PKG%\Documentation\" >nul
copy /y "Documentation\SESSION-BANNERS.md" "%PKG%\Documentation\" >nul
for %%M in (
  "dolphin-netplay-launcher-hero.png"
  "dolphin-netplay-friend-groups-demo.gif"
  "dolphin-netplay-friend-groups-demo.mp4"
  "dolphin-netplay-steam-host-demo.gif"
  "dolphin-netplay-steam-host-demo.mp4"
  "dolphin-netplay-library-grid.gif"
  "dolphin-netplay-library-grid.mp4"
  "dolphin-netplay-sessions-browser.gif"
  "dolphin-netplay-sessions-browser.mp4"
) do copy /y "Documentation\Media\%%~M" "%PKG%\Documentation\Media\" >nul

for %%I in (
  "DolphinNetPlayLauncher-icon.png"
  "DolphinNetPlayLauncher-icon-adventure.png"
  "DolphinNetPlayLauncher-icon-dark.png"
  "DolphinNetPlayLauncher-icon-indigo.png"
  "DolphinNetPlayLauncher-icon-light.png"
  "DolphinNetPlayLauncher-icon-oled.png"
  "DolphinNetPlayLauncher-icon-spice.png"
) do copy /y "Assets\Icons\%%~I" "%PKG%\Assets\Icons\" >nul

copy /y "Fonts\Outfit-Regular.ttf" "%PKG%\Fonts\" >nul
copy /y "Fonts\Outfit-Bold.ttf" "%PKG%\Fonts\" >nul
copy /y "Fonts\OFL-Outfit.txt" "%PKG%\Fonts\" >nul
copy /y "Fonts\README-FONTS.txt" "%PKG%\Fonts\" >nul

copy /y "Sounds\README-SOUNDS.txt" "%PKG%\Sounds\" >nul
copy /y "Sounds\CUSTOM-SOUND-THEMES.txt" "%PKG%\Sounds\" >nul
for %%T in (Adventure Royal ClassicUI) do (
  for %%C in (navigate switch library_open library_close stage_game use_game launch confirm cancel clear_game error) do (
    copy /y "Sounds\%%T\%%C.wav" "%PKG%\Sounds\%%T\" >nul
  )
)

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop'; Compress-Archive -Path '%PKG%' -DestinationPath '%ZIP%' -Force"
if errorlevel 1 (
  echo ERROR: Could not create release ZIP.
  pause
  exit /b 1
)

echo.
echo Release package created and runtime assets passed preflight:
echo   %CD%\%ZIP%
echo.
echo Development files, source, TEST history, chat history, diagnostics, and unapproved sound themes were intentionally excluded.
echo.
pause
exit /b 0

:require
if exist "%~1" exit /b 0
echo ERROR: Required release file is missing:
echo   %~1
exit /b 1

:missing
echo.
echo Release packaging stopped. No incomplete release ZIP was created.
echo Run Build.bat first if the missing file is DolphinNetPlayLauncher.exe or SDL3.dll.
echo.
pause
exit /b 1
