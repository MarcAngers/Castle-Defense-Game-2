# Resumes the RL training campaign after pause_training.ps1. train_ai_cluster.py
# auto-detects castle_defense_p1_v29.zip (the periodic checkpoint) and resumes from
# its exact saved step count / weights / optimizer state -- no special resume flag
# needed, this just relaunches the same three detached processes pause_training.ps1
# stopped. Safe to run repeatedly; if training is already running, PID checks below
# will just report that.
#
# Usage (from CastleDefense.PythonAI/):
#   powershell -File resume_training.ps1

$pyDir  = "C:\repos\Castle-Defense-Game-2\CastleDefense.PythonAI"
$pyExe  = "$pyDir\ai_env\Scripts\python.exe"

# --- Training ---
$existingTrainingPid = if (Test-Path "$pyDir\campaign_run.pid") { Get-Content "$pyDir\campaign_run.pid" } else { $null }
if ($existingTrainingPid -and (Get-Process -Id $existingTrainingPid -ErrorAction SilentlyContinue)) {
    Write-Output "Training already running (PID $existingTrainingPid) -- not relaunching."
} else {
    $checkpoint = Get-Item "$pyDir\castle_defense_p1_v29.zip" -ErrorAction SilentlyContinue
    if ($checkpoint) {
        Write-Output "Found checkpoint from $($checkpoint.LastWriteTime) -- training will resume from there."
    } else {
        Write-Output "No castle_defense_p1_v29.zip found -- this will warm-start fresh from castle_defense_p1_v25_bc instead."
    }
    $proc = Start-Process -FilePath $pyExe -ArgumentList "-u", "train_ai_cluster.py" -WorkingDirectory $pyDir `
        -RedirectStandardOutput "$pyDir\campaign_run.log" -RedirectStandardError "$pyDir\campaign_run.err.log" `
        -WindowStyle Hidden -PassThru
    Write-Output "Launched training. PID=$($proc.Id)"
    $proc.Id | Out-File -FilePath "$pyDir\campaign_run.pid" -Encoding ascii
}

# --- Benchmark loop ---
$existingBenchPid = if (Test-Path "$pyDir\benchmark_loop.pid") { Get-Content "$pyDir\benchmark_loop.pid" } else { $null }
if ($existingBenchPid -and (Get-Process -Id $existingBenchPid -ErrorAction SilentlyContinue)) {
    Write-Output "Benchmark loop already running (PID $existingBenchPid) -- not relaunching."
} else {
    $proc = Start-Process -FilePath "powershell.exe" -ArgumentList "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "$pyDir\benchmark_checkpoints.ps1", "-ModelTag", "v29", "-IntervalMinutes", "25", "-GamesPerCheck", "150" -WorkingDirectory $pyDir `
        -RedirectStandardOutput "$pyDir\benchmark_loop.log" -RedirectStandardError "$pyDir\benchmark_loop.err.log" `
        -WindowStyle Hidden -PassThru
    Write-Output "Launched benchmark loop. PID=$($proc.Id)"
    $proc.Id | Out-File -FilePath "$pyDir\benchmark_loop.pid" -Encoding ascii
}

# --- Sanity watchdog (re-run its fast+slow checks fresh each resume, since it's a
# one-shot script -- cheap and confirms the resumed run is healthy too) ---
Remove-Item "$pyDir\watchdog.log" -ErrorAction SilentlyContinue
$proc = Start-Process -FilePath $pyExe -ArgumentList "-u", "sanity_watchdog.py" -WorkingDirectory $pyDir `
    -RedirectStandardOutput "$pyDir\watchdog_stdout.log" -RedirectStandardError "$pyDir\watchdog_stderr.log" `
    -WindowStyle Hidden -PassThru
Write-Output "Launched sanity watchdog. PID=$($proc.Id)"
$proc.Id | Out-File -FilePath "$pyDir\watchdog.pid" -Encoding ascii

Write-Output "`nResume complete. Check campaign_run.log / training_progress.csv in a minute to confirm it's actually progressing."
