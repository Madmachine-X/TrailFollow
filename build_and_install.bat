@echo off
setlocal
cd /d "%~dp0"

echo ================================================
echo  TrailFollow - build and install
echo ================================================
echo.

rem ---- .NET SDK: install automatically the first time ----
set "DOTNET=dotnet"
call :hasSdk
if errorlevel 1 if exist "%ProgramFiles%\dotnet\dotnet.exe" set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
call :hasSdk
if not errorlevel 1 goto sdkOk

echo [SETUP] .NET 8 SDK is not installed. Installing it now - one time only, takes a few minutes.
echo         If Windows asks for permission, click Yes.
echo.
where winget >nul 2>nul
if errorlevel 1 goto noWinget
winget install -e --id Microsoft.DotNet.SDK.8 --accept-source-agreements --accept-package-agreements
if exist "%ProgramFiles%\dotnet\dotnet.exe" set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
call :hasSdk
if not errorlevel 1 goto sdkOk
echo.
echo [ERROR] .NET 8 SDK install did not finish. Restart the PC and run this file again.
pause
exit /b 1

:noWinget
echo [ERROR] Automatic install is not available on this PC.
echo         The download page will open. Install ".NET 8.0 SDK" for Windows x64, then run this file again.
start "" "https://dotnet.microsoft.com/download/dotnet/8.0"
pause
exit /b 1

:sdkOk

if not exist "%APPDATA%\Dungeon Helper\VoK.Sdk.dll" (
  echo [ERROR] Dungeon Helper not found at "%APPDATA%\Dungeon Helper"
  pause
  exit /b 1
)

rem ---- Dungeon Helper: close it automatically if it is running (it is started again after install) ----
set "DHEXE="
set "DHWASRUNNING=0"
tasklist /FI "IMAGENAME eq DungeonHelper.exe" 2>nul | find /I "DungeonHelper.exe" >nul
if errorlevel 1 goto dhClosed
set "DHWASRUNNING=1"
for /f "usebackq delims=" %%P in (`powershell -NoProfile -Command "(Get-Process DungeonHelper -ErrorAction SilentlyContinue | Select-Object -First 1).Path"`) do set "DHEXE=%%P"
if defined DHEXE >"dh_path.txt" echo %DHEXE%
echo [0/2] Closing Dungeon Helper...
taskkill /IM DungeonHelper.exe >nul 2>nul
set /a DHWAIT=0
:dhWait
tasklist /FI "IMAGENAME eq DungeonHelper.exe" 2>nul | find /I "DungeonHelper.exe" >nul
if errorlevel 1 goto dhClosed
set /a DHWAIT+=1
if %DHWAIT% EQU 3 taskkill /F /IM DungeonHelper.exe >nul 2>nul
if %DHWAIT% EQU 5 goto dhElevate
if %DHWAIT% GEQ 15 goto dhStuck
timeout /t 1 /nobreak >nul
goto dhWait

:dhElevate
rem Dungeon Helper runs as administrator, so it has to be closed with administrator rights
echo        Dungeon Helper runs as administrator. If Windows asks for permission, click Yes.
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process powershell -Verb RunAs -Wait -WindowStyle Hidden -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File \"%~dp0close_dh.ps1\" -Out \"%~dp0dh_path.txt\"'" >nul 2>nul
goto dhWait

:dhStuck
echo [STOP] Could not close Dungeon Helper. Close it completely ^(including tray icon^) and run again.
pause
exit /b 1

:dhClosed
rem Where to start Dungeon Helper from: the running copy, else the path remembered last time, else the usual places
if not defined DHEXE if exist "dh_path.txt" set /p DHEXE=<"dh_path.txt"
if defined DHEXE if not exist "%DHEXE%" set "DHEXE="
if not defined DHEXE if exist "%APPDATA%\Dungeon Helper\DungeonHelper.exe" set "DHEXE=%APPDATA%\Dungeon Helper\DungeonHelper.exe"
if not defined DHEXE if exist "%LOCALAPPDATA%\Programs\Dungeon Helper\DungeonHelper.exe" set "DHEXE=%LOCALAPPDATA%\Programs\Dungeon Helper\DungeonHelper.exe"
if not defined DHEXE if exist "%ProgramFiles%\Dungeon Helper\DungeonHelper.exe" set "DHEXE=%ProgramFiles%\Dungeon Helper\DungeonHelper.exe"

echo [1/2] Building...
rem Clean previous build files and stop leftover compiler processes (avoids "file is being used by another process")
"%DOTNET%" build-server shutdown >nul 2>nul
if exist "obj" rmdir /s /q "obj" >nul 2>nul
if exist "out" rmdir /s /q "out" >nul 2>nul
"%DOTNET%" build TrailFollow.csproj -c Release -o out -nologo -v q
if errorlevel 1 (
  echo.
  echo [ERROR] Build failed. Copy the red error lines above and send them to the person who shared this plugin.
  pause
  exit /b 1
)

echo [2/2] Installing...
set "DEST=%APPDATA%\Dungeon Helper\plugins\TrailFollow"
if not exist "%DEST%" mkdir "%DEST%"
copy /Y "out\TrailFollow.dll" "%DEST%\" >nul
copy /Y "plugin.metadata" "%DEST%\" >nul
if exist "out\TrailFollow.deps.json" copy /Y "out\TrailFollow.deps.json" "%DEST%\" >nul

echo.
echo Done! Installed to: %DEST%
if not defined DHEXE goto dhNoStart
for %%I in ("%DHEXE%") do set "DHDIR=%%~dpI"
set "DHDIR=%DHDIR:~0,-1%"
echo Starting Dungeon Helper...
start "" /D "%DHDIR%" "%DHEXE%"
timeout /t 3 /nobreak >nul
exit /b 0

:dhNoStart
echo Dungeon Helper could not be started automatically ^(location unknown^). Start it yourself this one time;
echo next time it will be closed and started automatically.
pause
exit /b 0

:hasSdk
"%DOTNET%" --list-sdks 2>nul | findstr /R /C:"^[89]\." /C:"^1[0-9]\." >nul
exit /b %errorlevel%
