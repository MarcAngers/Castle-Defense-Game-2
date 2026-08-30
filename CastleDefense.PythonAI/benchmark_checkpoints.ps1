# Periodic checkpoint benchmarking loop for the RL training campaign
# (see TRAINING_CAMPAIGN_LOG.md). Runs independently of train_ai_cluster.py --
# safe to start/stop/kill without touching the live training process.
#
# Each cycle: snapshots current_model.onnx into $benchmarkDir (a folder DEDICATED
# to this script -- see the 2026-07-28 fix below), points BotArena at that folder
# via BOTARENA_MODEL_DIR, runs BotArena's "models" mode filtered to just that
# snapshot (HeuristicBot vs the snapshot, alternating sides), ALSO runs
# "model-diag" (real unforced action distribution/entropy/P(invest) against a
# diverse pool -- see the 2026-07-26 invest-collapse investigation,
# TRAINING_CAMPAIGN_LOG.md) on the same snapshot, and appends a row to
# checkpoint_benchmark_log.csv. Raw output from every check is also kept (one
# file per check) for full detail.
#
# 2026-07-28 fix: snapshots used to be written straight into the LIVE TRAINING
# league (CastleDefense.Simulation/bin/Release/net10.0/league_models), the exact
# folder CastleDefense.Simulation/Program.cs's training-opponent-pool loader
# reads (every .onnx file, no filtering) -- so every one of this script's own
# unvalidated self-checkpoints was, for however long it sat there, ALSO a live
# training opponent. Confirmed 2026-07-28 that this had let 30 snapshots from
# three retired campaigns (v28/v29/v30) silently accumulate and get sampled by
# training arenas (see TRAINING_CAMPAIGN_LOG.md, "league pool bloat"). Per
# Marc's explicit ask, snapshots now go to $benchmarkDir instead -- a folder
# Program.cs's training loader (hardcoded to the literal "league_models" name,
# non-recursive) never reads, so a benchmark snapshot can NEVER become a
# training opponent, even transiently, regardless of any cleanup timing. This
# script tells BotArena where to look via the BOTARENA_MODEL_DIR env var
# (FindLeagueModelsDir() in CastleDefense.BotArena/Program.cs checks it first),
# so the "models"/"model-diag" calls below still resolve $snapTag correctly
# without BotArena ever touching the real league_models folder.
#
# 2026-07-26 (v30): added the model-diag / P(invest) columns. This is now the KEY
# learning metric for this campaign -- the old "invests/game" figure from
# training_progress.csv is contaminated by forced-exploration actions and misled
# the previous two sessions into thinking investing was healthy when the model's
# real, unforced policy had collapsed to never choosing it. Track THIS number to
# know if the invest-collapse fix is actually holding as v30 progresses.
#
# STOPS ITSELF once training has actually finished, instead of idling forever
# re-benchmarking a static model -- the v27 run's benchmark loop ran for ~11 extra
# hours after training completed because nothing was watching for the stop
# condition. Detects this by tracking the training PID (from campaign_run.pid) AND
# training_progress.csv's step count: if the PID is gone and steps haven't moved
# since the last cycle, this does one final confirmatory check and exits.
#
# Usage (from CastleDefense.PythonAI/):
#   powershell -File benchmark_checkpoints.ps1 [-ModelTag v30] [-IntervalMinutes 25] [-GamesPerCheck 150] [-InvestDiagGames 100]

param(
    [string]$ModelTag = "v30",
    [int]$IntervalMinutes = 25,
    [int]$GamesPerCheck = 150,
    [int]$InvestDiagGames = 100
)

$root         = "C:\repos\Castle-Defense-Game-2"
$pyDir        = "$root\CastleDefense.PythonAI"
$benchmarkDir = "$root\CastleDefense.Simulation\bin\Release\net10.0\league_models_benchmark"
$botArenaExe  = "$root\CastleDefense.BotArena\bin\Release\net10.0\CastleDefense.BotArena.exe"
$currentOnnx  = "$pyDir\current_model.onnx"
$logCsv       = "$pyDir\checkpoint_benchmark_log.csv"
$progressCsv  = "$pyDir\training_progress.csv"
$rawLogDir    = "$pyDir\checkpoint_benchmark_raw"
$trainingPidFile = "$pyDir\campaign_run.pid"

New-Item -ItemType Directory -Force -Path $benchmarkDir | Out-Null
$env:BOTARENA_MODEL_DIR = $benchmarkDir

New-Item -ItemType Directory -Force -Path $rawLogDir | Out-Null
if (!(Test-Path $logCsv)) {
    "timestamp_utc,training_steps,games,heuristic_winrate_pct,model_winrate_approx_pct,snapshot_file,invest_p_geomean,invest_legal_decisions,invest_chosen_pct" | Out-File -FilePath $logCsv -Encoding utf8
}

function Test-TrainingAlive {
    if (!(Test-Path $trainingPidFile)) { return $false }
    $trainingPid = Get-Content $trainingPidFile -ErrorAction SilentlyContinue
    if (-not $trainingPid) { return $false }
    return [bool](Get-Process -Id $trainingPid -ErrorAction SilentlyContinue)
}

Write-Output "[Benchmark loop] Started. ModelTag=$ModelTag Interval=$IntervalMinutes min, games/check=$GamesPerCheck"
Write-Output "[Benchmark loop] Logging to $logCsv (raw output per check in $rawLogDir)"

$lastSteps = $null
$sawTrainingStopped = $false

while ($true) {
    Start-Sleep -Seconds ($IntervalMinutes * 60)

    if (!(Test-Path $currentOnnx)) {
        Write-Output "$(Get-Date -Format o)  [skip] current_model.onnx not found yet."
        continue
    }

    $ts = (Get-Date).ToUniversalTime().ToString("yyyyMMdd_HHmmss")
    $steps = "unknown"
    if (Test-Path $progressCsv) {
        $lastLine = Get-Content $progressCsv -Tail 1
        if ($lastLine -match "^(\d+),") { $steps = $matches[1] }
    }

    $snapTag  = "${ModelTag}_snap_$ts"
    $snapPath = Join-Path $benchmarkDir "$snapTag.onnx"
    try {
        Copy-Item -Path $currentOnnx -Destination $snapPath -Force
    } catch {
        Write-Output "$(Get-Date -Format o)  [skip] could not copy snapshot: $_"
        continue
    }

    $rawOut = Join-Path $rawLogDir "$ts.log"
    $output = & $botArenaExe models headstart $GamesPerCheck $snapTag 2>&1 | Out-String
    $output | Out-File -FilePath $rawOut -Encoding utf8

    $heuristicWr = ""
    $modelWrApprox = ""
    if ($output -match "bot wins:\s*\d+/\d+\s*\(\s*([\d.]+)%\)") {
        $heuristicWr = $matches[1]
        $modelWrApprox = [math]::Round(100.0 - [double]$heuristicWr, 1)
    }

    # Real unforced P(invest) -- the key learning metric for this campaign (see
    # header comment). Runs against a diverse mixed pool, zero forced exploration,
    # so this reflects what the policy actually wants to do, not what the training
    # loop's exploration nudge forced it into.
    $diagOutput = & $botArenaExe model-diag $snapTag $InvestDiagGames 2>&1 | Out-String
    $diagOutput | Out-File -FilePath (Join-Path $rawLogDir "$ts`_diag.log") -Encoding utf8

    $investGeomean = ""
    $investLegalN  = ""
    $investChosenPct = ""
    if ($diagOutput -match "P\(invest\) when legal: geometric mean = ([\d.E+-]+).*\(n=(\d+) decisions") {
        $investGeomean = $matches[1]
        $investLegalN  = $matches[2]
        if ($diagOutput -match "invest\s+(\d+)\s+\(") {
            $investChosenCount = [double]$matches[1]
            if ([double]$investLegalN -gt 0) {
                $investChosenPct = [math]::Round(100.0 * $investChosenCount / [double]$investLegalN, 1)
            }
        }
    }

    "$ts,$steps,$GamesPerCheck,$heuristicWr,$modelWrApprox,$snapTag.onnx,$investGeomean,$investLegalN,$investChosenPct" | Out-File -FilePath $logCsv -Append -Encoding utf8
    Write-Output "$(Get-Date -Format o)  steps=$steps  heuristic_wr=$heuristicWr%  model_wr_approx=$modelWrApprox%  P(invest)geomean=$investGeomean  invest_chosen=$investChosenPct% (n=$investLegalN)"

    # Keep only the most recent 10 snapshots in $benchmarkDir -- these are cheap,
    # frequent self-checkpoints, not meant to accumulate indefinitely even in
    # their own dedicated folder. (Filtering across ALL ModelTags, not just this
    # run's own -- a prior version of this cleanup matched only "${ModelTag}_
    # snap_*.onnx" and let 30 files from three retired campaigns accumulate
    # forever, since a new tag's cleanup never revisited an old tag's leftovers.
    # This folder is no longer read by the training opponent-pool loader at all
    # (see the 2026-07-28 header comment), so this cleanup is now purely about
    # disk space, not training-pool purity -- but the global-window fix is kept
    # since there's no reason to let it grow unbounded either.)
    Get-ChildItem $benchmarkDir -Filter "*_snap_*.onnx" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -Skip 10 | Remove-Item -Force -ErrorAction SilentlyContinue

    # Stop condition: training process gone AND step count unchanged since last cycle
    # (unchanged steps alone could just mean a slow interval; process-gone alone could
    # be a transient blip -- require both together before treating it as "really done").
    $trainingAlive = Test-TrainingAlive
    if (-not $trainingAlive -and $steps -eq $lastSteps) {
        if ($sawTrainingStopped) {
            Write-Output "$(Get-Date -Format o)  Training process gone and steps unchanged for two consecutive cycles -- stopping benchmark loop (this run's job is done)."
            break
        }
        $sawTrainingStopped = $true
        Write-Output "$(Get-Date -Format o)  Training process not found and steps unchanged this cycle -- will confirm next cycle before stopping."
    } else {
        $sawTrainingStopped = $false
    }
    $lastSteps = $steps
}

Write-Output "[Benchmark loop] Exiting."
