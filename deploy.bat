@echo off
rem SpaceGod - Unity C# (SpaceGod.Unity.CSharp). The Desktop shortcut "SpaceGod - Unity C#" opens this.
rem Asks first: 1) Deploy & Run pulls latest, rebuilds even when nothing changed (-Force), publishes to
rem C:\Apps\SpaceGod.Unity.CSharp, refreshes the Desktop shortcut and launches it (the repo-root run.ps1
rem does the work); 2) Run starts the published copy without deploying.
rem "deploy.bat --deploy [flags]" skips the menu and deploys without -Force (the /deploy hook);
rem any other arguments pass through to run.ps1 when deploying.
setlocal
set "APPDIR=C:\Apps\SpaceGod.Unity.CSharp"
set "EXE=%APPDIR%\SpaceGod.Unity.CSharp.exe"
set "FORCE=-Force"
set "ARGS=%*"
if /i not "%~1"=="--deploy" goto menu
set "FORCE="
set "ARGS="
shift
:collect
if "%~1"=="" goto deploy
set "ARGS=%ARGS% %1"
shift
goto collect

:menu
title SpaceGod - Unity C#
echo.
echo   SpaceGod - Unity C#  (SpaceGod.Unity.CSharp)
echo     1^) Deploy ^& Run
echo     2^) Run
echo.
choice /c 12 /n /m "  Choose 1 or 2: "
if errorlevel 2 goto run

:deploy
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run.ps1" -Launch %FORCE% %ARGS%
if errorlevel 1 (
    echo.
    echo   Deploy failed. See Logs\deploy-build.log.
    pause
    exit /b 1
)
timeout /t 5
exit /b 0

:run
if not exist "%EXE%" (
    echo.
    echo   SpaceGod - Unity C# is not deployed yet ^(%EXE% is missing^) - deploying first.
    goto deploy
)
start "" /d "%APPDIR%" "%EXE%" -capture video hq
exit /b 0
