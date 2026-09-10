@echo off
setlocal
rem ============================================================================
rem  UPDATE THE BALANCE DASHBOARD
rem
rem  Rebuilds the arena, sweeps every loadout against every other loadout in both
rem  seats (HeuristicBot on both sides), and opens the result.
rem
rem      update-dashboard              16,384 games, about 2 minutes
rem      update-dashboard --games 4    4x the games, 4x the time, half the noise
rem
rem  Anything you pass is handed straight to the sweep, so --games / --threads /
rem  --out all work. See BalanceMatrix.cs.
rem
rem  IT BUILDS ONLY CastleDefense.BotArena, ON PURPOSE. A full `dotnet build`
rem  also builds the web project, which FAILS while the game's dev server is
rem  running because the server holds CastleDefense.Engine.dll open. The arena
rem  does not depend on the web project, so building just it picks up engine and
rem  CSV edits with the game still running.
rem ============================================================================

cd /d "%~dp0"

set "ARENA=%~dp0CastleDefense.BotArena\bin\Release\net10.0\CastleDefense.BotArena.exe"
set "DASH=%~dp0CastleDefense.BotArena\dashboard\dashboard.html"

echo Building CastleDefense.BotArena (Release)...
dotnet build CastleDefense.BotArena -c Release --nologo -v quiet
if errorlevel 1 (
    echo.
    echo BUILD FAILED - dashboard not updated, nothing was overwritten.
    exit /b 1
)
echo.

rem  Strip --no-open before forwarding: balance-matrix does not know that flag and would
rem  reject the whole command line.
set "OPEN=1"
set "SWEEPARGS="
:parseargs
if "%~1"=="" goto parsedone
if /i "%~1"=="--no-open" (
    set "OPEN=0"
) else (
    set "SWEEPARGS=%SWEEPARGS% %~1"
)
shift
goto parseargs
:parsedone

"%ARENA%" balance-matrix%SWEEPARGS%
if errorlevel 1 (
    echo.
    echo SWEEP FAILED - see the output above.
    exit /b 1
)

if "%OPEN%"=="0" (
    echo.
    echo Dashboard updated: %DASH%
    exit /b 0
)

echo.
echo Opening %DASH%
start "" "%DASH%"
exit /b 0
