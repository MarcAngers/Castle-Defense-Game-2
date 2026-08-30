# verify_invest_metric.ps1 -- 2026-07-28 metric audit
#
# Re-measures the three checkpoints in the campaign log's headline table using the
# corrected invest-stats output, which now separates invests the policy EARNED from
# invests handed out for free by the headstart time machine (E[timeSkip] = 2.118).
#
# No training compute. Pure evaluation. Expect a few minutes per block.
#
# Run from:  C:\repos\Castle-Defense-Game-2\CastleDefense.PythonAI
#   powershell -File verify_invest_metric.ps1

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$arena = Join-Path $repo "CastleDefense.BotArena"
$out = Join-Path $PSScriptRoot "invest_metric_audit.log"

Write-Host "Building BotArena (Release)..." -ForegroundColor Cyan
dotnet build -c Release $arena | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Build failed -- fix before running the audit." }

$exe = Get-ChildItem -Path (Join-Path $arena "bin\Release") -Recurse -Filter "CastleDefense.BotArena.exe" |
       Select-Object -First 1 -ExpandProperty FullName
if (-not $exe) { throw "Could not locate CastleDefense.BotArena.exe under bin\Release." }
Write-Host "Using $exe" -ForegroundColor DarkGray

"=== invest metric audit, $(Get-Date -Format s) ===" | Out-File $out

# The three checkpoints from the campaign log's headline table.
$checkpoints = @("v25_bc", "v30_floortest", "heuristic_pressure_test")

foreach ($ck in $checkpoints) {
    foreach ($mode in @("headstart", "nostart")) {
        $args = if ($mode -eq "headstart") { @("invest-stats", $ck, "headstart", "300") }
                else                        { @("invest-stats", $ck, "none",      "300") }

        $header = "`n--- $ck [$mode, 300 games] ---"
        Write-Host $header -ForegroundColor Yellow
        $header | Out-File $out -Append

        & $exe @args 2>&1 | Tee-Object -Variable res | Out-Host
        $res | Out-File $out -Append
    }
}

Write-Host "`nDone. Full transcript: $out" -ForegroundColor Green
Write-Host @"

WHAT TO LOOK FOR
  * 'free from time machine' should read ~2.12 under headstart and 0.00 under nostart.
  * Compare checkpoints on the EARNED line, not the raw avg.
  * The nostart EARNED number and the headstart EARNED number should broadly agree.
    If they diverge a lot, the policy's investing is headstart-dependent -- i.e. it
    only invests when the time machine hands it a grown economy, which is itself the
    finding.
"@ -ForegroundColor Gray
