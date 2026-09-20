@echo off
setlocal
cd /d "%~dp0"

REM Read the version directly from DolphinNetPlayLauncher.cs.
REM This keeps Build.bat, the window title, and the About text on one source of truth.
set "APP_VERSION="
for /f "tokens=2 delims==" %%V in ('findstr /r /c:"internal const string AppVersion = " "DolphinNetPlayLauncher.cs"') do (
  set "APP_VERSION=%%V"
)
if not defined APP_VERSION (
  echo.
  echo ERROR: Could not read AppVersion from DolphinNetPlayLauncher.cs.
  echo Build stopped so a release cannot be produced with an unknown/stale version.
  echo.
  pause
  exit /b 1
)
set "APP_VERSION=%APP_VERSION:;=%"
set "APP_VERSION=%APP_VERSION:"=%"
set "APP_VERSION=%APP_VERSION: =%"


set "SDL_VERSION=3.4.16"
set "SDL_ZIP=SDL3-%SDL_VERSION%-win32-x64.zip"
set "SDL_URL=https://github.com/libsdl-org/SDL/releases/download/release-%SDL_VERSION%/%SDL_ZIP%"

if not exist "SDL3.dll" (
  echo.
  echo SDL3.dll is required for controller navigation.
  echo Downloading official SDL %SDL_VERSION% x64 runtime...
  powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "$ErrorActionPreference='Stop';" ^
    "Invoke-WebRequest -UseBasicParsing '%SDL_URL%' -OutFile '%SDL_ZIP%';" ^
    "Expand-Archive -Force '%SDL_ZIP%' '.\_sdl_temp';" ^
    "$dll = Get-ChildItem '.\_sdl_temp' -Recurse -Filter 'SDL3.dll' | Select-Object -First 1;" ^
    "if (-not $dll) { throw 'SDL3.dll was not found in the downloaded archive.' };" ^
    "Copy-Item $dll.FullName '.\SDL3.dll' -Force;" ^
    "$license = Get-ChildItem '.\_sdl_temp' -Recurse -Filter 'LICENSE.txt' | Select-Object -First 1;" ^
    "if ($license) { Copy-Item $license.FullName '.\SDL3-LICENSE.txt' -Force }"
  if errorlevel 1 (
    echo.
    echo Could not download/extract SDL3.
    echo Controller navigation will not work without SDL3.dll.
    echo.
    pause
    exit /b 1
  )
  rmdir /s /q "_sdl_temp" >nul 2>nul
  del /q "%SDL_ZIP%" >nul 2>nul
)

set "CSC="
if exist "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if not defined CSC (
  echo.
  echo Could not find the 64-bit .NET Framework C# compiler ^(csc.exe^).
  echo This build is x64 because the bundled SDL runtime is x64.
  echo Install/enable .NET Framework 4.x developer components, then run Build.bat again.
  echo.
  pause
  exit /b 1
)

echo Building Dolphin NetPlay Launcher %APP_VERSION%...
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /out:"DolphinNetPlayLauncher.exe" /win32icon:"Assets\Icons\DolphinNetPlayLauncher.ico" /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:System.Security.dll "DolphinNetPlayLauncher.cs"

if errorlevel 1 (
  echo.
  echo Build failed.
  pause
  exit /b 1
)

echo.
echo Build complete - Dolphin NetPlay Launcher %APP_VERSION%:
echo   "%CD%\DolphinNetPlayLauncher.exe"
echo   "%CD%\SDL3.dll"
echo.
echo Steam ROM Manager executable: DolphinNetPlayLauncher.exe
echo Command Line Arguments: "${filePath}"
echo.
pause
