# Periodic checkpoint benchmarking loop for the RL training campaign
# (see TRAINING_CAMPAIGN_LOG.md). Runs independently of train_ai_cluster.py --
# safe to start/stop/kill without touching the live training process.
#
# Each cycle: snapshots current_model.onnx into the training arenas' league_models
# folder (BotArena's FindLeagueModelsDir() already looks there), runs BotArena's
# "models" mode filtered to just that snapshot (HeuristicBot vs the snapshot,
# alternating sides), ALSO runs "model-diag" (real unforced action distribution/
# entropy/P(invest) against a diverse pool -- see the 2026-07-26 invest-collapse
# investigation, TRAINING_CAMPAIGN_LOG.md) on the same snapshot, and appends a row
# to checkpoint_benchmark_log.csv. Raw output from every check is also kept (one
# file per check) for full detail.
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
$leagueDir    = "$root\CastleDefense.Simulation\bin\Release\net10.0\league_models"
$botArenaExe  = "$root\CastleDefense.BotArena\bin\Release\net10.0\CastleDefense.BotArena.exe"
$currentOnnx  = "$pyDir\current_model.onnx"
$logCsv       = "$pyDir\checkpoint_benchmark_log.csv"
$progressCsv  = "$pyDir\training_progress.csv"
$rawLogDir    = "$pyDir\checkpoint_benchmark_raw"
$trainingPidFile = "$pyDir\campaign_run.pid"

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
    $snapPath = Join-Path $leagueDir "$snapTag.onnx"
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

    # Keep only the most recent 10 snapshots in league_models -- these are cheap,
    # frequent self-checkpoints, not meant to accumulate as permanent league anchors.
    #
    # 2026-07-28 fix: this used to filter by "${ModelTag}_snap_*.onnx" (only THIS
    # campaign's own tag), so every time a campaign was retired and a new one
    # started under a new ModelTag (v28 -> v29 -> v30 -> ...), the previous tag's
    # 10 snapshots were never revisited by anything and sat there forever --
    # confirmed 30 orphaned v28_snap_*/v29_snap_*/v30_snap_* files had
    # accumulated in league_models, silently tripling the size of what's supposed
    # to be a small curated "best models + spam bots" league (Marc's stated
    # intent) and getting loaded as full in-memory ONNX opponents by every
    # training arena regardless of relevance. Filtering across ALL tags here
    # means the rolling keep-10 window is global, so an old campaign's snapshots
    # get swept away automatically as soon as any newer campaign's benchmark loop
    # runs, instead of requiring a manual cleanup like this session's.
    Get-ChildItem $leagueDir -Filter "*_snap_*.onnx" -ErrorAction SilentlyContinue |
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
