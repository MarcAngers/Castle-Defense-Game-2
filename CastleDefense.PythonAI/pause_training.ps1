# Cleanly pauses the RL training campaign so Marc can use the PC, then resume later
# with resume_training.ps1. Safe to run any time -- the worst-case progress loss is
# whatever's happened since the last checkpoint save (every 3 PPO updates now, ~35
# seconds at the measured throughput -- see train_ai_cluster.py's save-cadence
# comment), since model.save() writes the FULL resumable PPO state (weights,
# optimizer, step count), not just an inference export.
#
# Usage (from CastleDefense.PythonAI/):
#   powershell -File pause_training.ps1

$pyDir = "C:\repos\Castle-Defense-Game-2\CastleDefense.PythonAI"

function Stop-IfAlive($pidFile, $label) {
    if (Test-Path $pidFile) {
        $trainingPid = Get-Content $pidFile -ErrorAction SilentlyContinue
        if ($trainingPid) {
            $proc = Get-Process -Id $trainingPid -ErrorAction SilentlyContinue
            if ($proc) {
                Write-Output "Stopping $label (PID $trainingPid)..."
                Stop-Process -Id $trainingPid -Force -ErrorAction SilentlyContinue
            } else {
                Write-Output "$label (PID $trainingPid) already stopped."
            }
        }
    } else {
        Write-Output "No PID file for $label ($pidFile) -- nothing to stop."
    }
}

Stop-IfAlive "$pyDir\campaign_run.pid" "training"
Stop-IfAlive "$pyDir\benchmark_loop.pid" "benchmark loop"
Stop-IfAlive "$pyDir\watchdog.pid" "sanity watchdog"

Start-Sleep -Seconds 2

# ── Command-line sweep: the real safety net (added 2026-07-27) ────────────────
# The PID-file path above ONLY works when training was launched by
# resume_training.ps1, which is what writes those files. A run started any other
# way (directly from a terminal, from an agent/tool session, or an ad-hoc test
# harness like test_invest_fix.py) writes NO pid file -- and a pid file left over
# from an EARLIER run points at a long-dead process, so Stop-IfAlive cheerfully
# reports "already stopped" and moves on.
#
# That is exactly the gap that stranded a live run on 2026-07-27: the arenas got
# cleaned up by name below, but the Python trainer driving them survived, leaving
# no clean way to stop it (killing every `python` by name is too broad to be safe
# -- it would take out unrelated Python work). Matching on the COMMAND LINE finds
# our trainer specifically, regardless of how it was started.
$trainerPatterns = @("*train_ai_cluster.py*", "*test_invest_fix.py*", "*plot_training.py*", "*benchmark_checkpoints.ps1*")
$myPid = $PID
$procs = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
    Where-Object {
        $cl = $_.CommandLine
        $_.ProcessId -ne $myPid -and $cl -and ($trainerPatterns | Where-Object { $cl -like $_ })
    }
foreach ($p in $procs) {
    $label = ($trainerPatterns | Where-Object { $p.CommandLine -like $_ }) -join ","
    Write-Output "Stopping PID $($p.ProcessId) (matched $label)..."
    Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
}

Start-Sleep -Seconds 2

# Arenas last: they exit on their own once the trainer's socket closes, but a
# force-killed trainer can leave them orphaned, so sweep by name to be sure.
$arenas = Get-Process CastleDefense.Simulation -ErrorAction SilentlyContinue
if ($arenas) {
    Write-Output "Stopping $($arenas.Count) orphaned training arena process(es)..."
    $arenas | Stop-Process -Force -ErrorAction SilentlyContinue
}

# Stale pid files cause false "already stopped" reports next time -- clear them.
Remove-Item "$pyDir\campaign_run.pid","$pyDir\benchmark_loop.pid","$pyDir\watchdog.pid" `
    -ErrorAction SilentlyContinue

Start-Sleep -Seconds 1
# Scope the verification to OUR processes only -- a bare `Get-Process python` also
# counts unrelated Python work and would false-alarm.
$stillOurs = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
    Where-Object {
        $cl = $_.CommandLine
        $_.ProcessId -ne $PID -and $cl -and ($trainerPatterns | Where-Object { $cl -like $_ })
    }
$arenasLeft = (Get-Process CastleDefense.Simulation -ErrorAction SilentlyContinue | Measure-Object).Count
$remaining = (($stillOurs | Measure-Object).Count) + $arenasLeft
if ($remaining -eq 0) {
    Write-Output "`nPaused cleanly -- no training/arena processes remain."
} else {
    Write-Output "`nWARNING: $remaining training process(es) still running -- check manually:"
    $stillOurs | Select-Object ProcessId, CommandLine
    Get-Process CastleDefense.Simulation -ErrorAction SilentlyContinue | Select-Object Id, ProcessName
}

Write-Output "Latest saved checkpoint(s):"
Get-ChildItem "$pyDir\castle_defense_p1_v30*.zip" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object Name, LastWriteTime, Length
Write-Output "`nRun resume_training.ps1 when ready to continue."
