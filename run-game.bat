@echo off
setlocal
rem ============================================================================
rem  RUN THE GAME, PICKING UP CSV AND ENGINE EDITS
rem
rem      run-game
rem
rem  Stops whatever is already serving port 5168, rebuilds in DEBUG, and starts
rem  the server.
rem
rem  WHY IT STOPS THE SERVER FIRST. `dotnet run` holds CastleDefense.Engine.dll
rem  open, so rebuilding while the game is running fails with:
rem
rem      error MSB3027: Could not copy ... CastleDefense.Engine.dll ...
rem      The file is locked by: "CastleDefenseGame2 (NNNNN)"
rem
rem  The build then fails, bin\Debug\net10.0\Data\master_gadgets.csv never
rem  updates, and the game keeps playing by the OLD numbers while the shop
rem  button shows the new ones. That is not hypothetical -- it cost a debugging
rem  session on 2026-09-03.
rem
rem  WHY DEBUG. `dotnet run` uses Debug by default, so a Release build leaves the
rem  output the game actually runs untouched. Building Release and wondering why
rem  nothing changed is the other half of the same trap.
rem
rem  Recordings are NOT redirected here: this is Marc's own server and its games
rem  belong in the human corpus. Agent-driven runs use .claude/launch.json, which
rem  passes --RecordingsDir=recordings-agent.
rem ============================================================================

cd /d "%~dp0"

echo Stopping anything already serving port 5168...
for /f "tokens=5" %%p in ('netstat -ano ^| findstr /r /c:":5168 .*LISTENING"') do (
    echo   stopping PID %%p
    taskkill /PID %%p /F >nul 2>&1
)

rem ---------------------------------------------------------------------------
rem  A CSV-ONLY EDIT DOES NOT PROPAGATE THROUGH AN INCREMENTAL BUILD.
rem
rem  With no .cs change MSBuild considers CastleDefense.Engine up to date and
rem  skips its content copy, so the Engine's own Data\master_gadgets.csv keeps
rem  the OLD file -- and the web project then copies that stale copy forward.
rem  The game plays by the old numbers while the source CSV shows the new ones:
rem  the same symptom as the MSB3027 trap above, a different cause. Cost a cycle
rem  on 2026-09-08, and the [data] line below is what caught it.
rem
rem  Deleting the Engine's copies forces the copy step to run regardless of
rem  timestamps.
rem ---------------------------------------------------------------------------
set "ENGDATA=CastleDefense.Engine\bin\Debug\net10.0\Data"
if exist "%ENGDATA%\master_gadgets.csv" del /q "%ENGDATA%\master_gadgets.csv"
if exist "%ENGDATA%\master_roster.csv" del /q "%ENGDATA%\master_roster.csv"

echo Building CastleDefenseGame2 (Debug)...
dotnet build CastleDefenseGame2 -c Debug --nologo -v quiet
if errorlevel 1 (
    echo.
    echo BUILD FAILED - the old server is still running by the old numbers.
    echo If this says "file is locked", something is still holding the DLL:
    echo    netstat -ano ^| findstr :5168
    exit /b 1
)

echo.
echo Starting. Watch for the [data] line - if its timestamp is older than your
echo CSV edit, the edit did not make it into this build.
echo.
dotnet run --project CastleDefenseGame2 -c Debug --no-build --urls http://localhost:5168
