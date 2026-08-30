# Bounded concentrated-pressure validation test (see TRAINING_CAMPAIGN_LOG.md
# HANDOFF STATE 2026-07-27, "Next step" section). Question: with the invest
# probability floor active (mechanically confirmed working, but behaviorally
# unproven at 14M steps under the normal ~30%-Heuristic/~50%-self-play pool),
# does a Heuristic-heavy pool supply enough concentrated economic pressure to
# push real (unforced) invests/game up from the ~2.2-2.55 ceiling toward
# HeuristicBot's own ~5.2?
#
# Fresh warm-start from castle_defense_p1_v25_bc (NOT a resume of the collapsed
# v30/v30_floortest lineage) -- deliberate choice: floortest already carries
# ~517M steps of momentum toward the never-invest rush baked into the rest of
# the policy (not just the invest logit the floor patches), which is exactly
# why 14M more steps under the floor alone didn't move real invests/game. A
# clean base gives the Heuristic-heavy pressure its best real shot, and is
# directly comparable to this project's one genuinely strong positive result
# so far (fresh v25_bc + floor-equivalent forcing hit P(invest) geomean 0.28,
# 56% legal-opportunity invest rate in an earlier 15M-step test).
#
# Pool weights (via CastleDefense.Simulation's new env-var overrides -- see
# Program.cs's CUM_* thresholds, default-preserving when unset):
#   Random Dummy 2%, Anti-Spam 2%, Spam 4%, League 2%, Heuristic 80%, Self-Play 10%
# (production default is Heuristic 30% / Self-Play ~50%.)
#
# Distinct model/onnx/progress-log names throughout so this can NEVER collide
# with or overwrite the real campaign's current_model.onnx, castle_defense_p1_v30*,
# or training_progress*.csv files -- verified safe to run even if a real
# campaign run were active (it never touches any of those paths).
#
# Usage (from CastleDefense.PythonAI/):
#   powershell -File launch_heuristic_pressure_test.ps1

$pyDir = "C:\repos\Castle-Defense-Game-2\CastleDefense.PythonAI"
$pyExe = "$pyDir\ai_env\Scripts\python.exe"

$env:TEST_MODEL_NAME  = "castle_defense_p1_heuristic_pressure_test"
$env:TEST_BASE_MODEL  = "castle_defense_p1_v25_bc"
$env:TEST_ONNX_NAME   = "heuristic_pressure_test_model.onnx"
$env:TEST_PROGRESS_LOG = "training_progress_heuristic_pressure_test.csv"
$env:TEST_TOTAL_STEPS = "20000000"
$env:TEST_N_ENVS      = "10"

$env:POOL_CUM_RANDOM_DUMMY = "0.02"
$env:POOL_CUM_ANTISPAM     = "0.04"
$env:POOL_CUM_SPAM         = "0.08"
$env:POOL_CUM_LEAGUE       = "0.10"
$env:POOL_CUM_HEURISTIC    = "0.90"

$proc = Start-Process -FilePath $pyExe -ArgumentList "-u", "test_invest_fix.py" -WorkingDirectory $pyDir `
    -RedirectStandardOutput "$pyDir\heuristic_pressure_test.log" -RedirectStandardError "$pyDir\heuristic_pressure_test.err.log" `
    -WindowStyle Hidden -PassThru
Write-Output "Launched bounded concentrated-pressure test. PID=$($proc.Id)"
$proc.Id | Out-File -FilePath "$pyDir\heuristic_pressure_test.pid" -Encoding ascii

Write-Output "`nLogs: heuristic_pressure_test.log / .err.log"
Write-Output "Progress CSV: training_progress_heuristic_pressure_test.csv"
Write-Output "Stop with pause_training.ps1 (matches *test_invest_fix.py* by command line) or:"
Write-Output "  Stop-Process -Id $($proc.Id) -Force"
