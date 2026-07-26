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

# Clean up children the parent processes can't take with them when force-killed:
# the 14 training arenas, and the plot_training.py --watch subprocess chain.
$arenas = Get-Process CastleDefense.Simulation -ErrorAction SilentlyContinue
if ($arenas) {
    Write-Output "Stopping $($arenas.Count) orphaned training arena process(es)..."
    $arenas | Stop-Process -Force -ErrorAction SilentlyContinue
}

$watchers = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -like "*plot_training.py*" }
if ($watchers) {
    Write-Output "Stopping $($watchers.Count) orphaned plot_training.py watcher process(es)..."
    foreach ($w in $watchers) { Stop-Process -Id $w.ProcessId -Force -ErrorAction SilentlyContinue }
}

Start-Sleep -Seconds 1
$remaining = (Get-Process python, CastleDefense.Simulation -ErrorAction SilentlyContinue | Measure-Object).Count
if ($remaining -eq 0) {
    Write-Output "`nPaused cleanly -- no training/arena processes remain."
} else {
    Write-Output "`nWARNING: $remaining process(es) still running -- check manually:"
    Get-Process python, CastleDefense.Simulation -ErrorAction SilentlyContinue | Select-Object Id, ProcessName
}

Write-Output "Latest saved checkpoint:"
Get-Item "$pyDir\castle_defense_p1_v29.zip" -ErrorAction SilentlyContinue | Select-Object Name, LastWriteTime, Length
Write-Output "`nRun resume_training.ps1 when ready to continue."
