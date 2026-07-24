# RL Training Campaign Log

Started 2026-07-24 while Marc is away for the weekend, per his explicit go-ahead to
work autonomously on: (1) increasing simulated games/minute, (2) tuning training
parameters, (3) building a workflow to push the PPO model's performance further,
using the now-strong HeuristicBot (~90%+ vs the old league checkpoints, see
`project_ai_opponent_heuristic` memory) as a training-league anchor.

## Starting-state findings (before any changes)

**The training pipeline is far more sophisticated than the CLAUDE.md summary or prior
memory entries described** — `train_ai_cluster.py` is a hand-rolled architecture, not
a plain `SubprocVecEnv` + `model.learn()` setup: a custom binary TCP batch protocol
per arena, GPU-batched log-prob/value re-inference, direct rollout-buffer writes
(avoiding 8192 individual `.add()` calls), N-step board-eval reward shaping from a
hand-fit linear evaluator (`GameState.EvaluateBoard()`, coefficients from
`train_evaluator.py` → `evaluator_weights.json`), a GA-based reward-hyperparameter
search (`ga_runner.py`, multiple `ga_runs/` going back to May), and a BC pretraining
pipeline (`bc_pretrain.py`) from human replay recordings. This already reflects a lot
of real engineering effort — the plateau is not from a naive setup.

**`training_progress.csv` from the last full run (10M steps, target reached) showed a
genuine plateau/slight decline**: win rate vs the training opponent pool sat at
36.6%–39.2% for the final ~3M steps, trending slightly DOWN by the end. Matches
Marc's own description exactly. Archived as
`training_progress_ARCHIVE_pre_campaign.csv` before starting fresh.

### Three real, well-evidenced bugs/gaps found and fixed before touching anything else

1. **`INVEST_EXPLORE` was 0.90, not the 0.05 its own comment describes**
   (`CastleDefense.Simulation/Program.cs`). This forces the training policy to invest
   on 90% of ticks where investing is legal, regardless of what the model itself would
   choose. Confirmed via `git log -p` that this was wrong from the very first commit
   that introduced it (not a later regression) — the comment always said "~5%" while
   the constant was always `0.90f`. This directly forecloses the single highest-
   leverage judgment call this economy has (invest now vs. defend first) — exactly
   what HeuristicBot's entire multi-session tuning history found to be the hardest,
   most important thing to get right (see `project_ai_opponent_heuristic` memory, the
   whole "money-pinning" and "time-to-death" sagas). A model that's forced to invest
   90% of the time it legally can never gets to learn when NOT to. **Fixed: reverted
   to 0.05f** (real exploration nudge, not a behavioral override).

2. **GA-tuned reward params were never actually being applied.** `reward_params_{port}.json`
   files (which `RewardParams.LoadFromJson` reads per-arena, falling back silently to
   `RewardParams.Default` if missing) did not exist anywhere in the repo, despite a
   fully-populated `ga_best_params.json` from a real search (CombatScale 1.0→0.509,
   WinReward 54000→35164, AntiSpend 700→1113, etc. — not small deltas). So the last
   full run very likely trained against the untuned defaults, not the GA winner.
   **Fixed: ran `apply_ga_params.py`**, which writes all 14 `reward_params_{port}.json`
   files (and confirmed it's a no-op on `train_ai_cluster.py`'s `BOARD_SHAPING_*`
   constants — those were already patched to match `ga_best_params.json` from a prior
   run).

3. **The Python venv's `torch` was CPU-only.** `ai_env/` existed but was missing its
   `Scripts/` folder entirely (broken/incomplete venv, no `python.exe`/`pip`/`activate`
   inside it) — had to be recreated from scratch with the system Python 3.11.5. Plain
   `pip install torch==2.10.0` from `requirements.txt` silently grabs the `+cpu` wheel
   on Windows (no error, no warning) — `train_ai_cluster.py` hardcodes `device="cuda"`
   everywhere, which would either crash outright or (worse) silently degrade if SB3/PyTorch
   handled it more gracefully. GPU-batched inference is a load-bearing part of this
   architecture's whole throughput design, so this alone could have devastated both
   correctness and speed for the *whole* prior run if it happened before too — not yet
   confirmed either way. **Fixed: reinstalled `torch==2.10.0+cu128`** from
   `https://download.pytorch.org/whl/cu128` (matches driver's CUDA 13.2, backward compatible)
   with `--force-reinstall` (plain re-run of the pinned-version install silently no-ops
   as "already satisfied" even when the installed build is the wrong variant — worth
   remembering for any future venv rebuild on this project).

### Opponent-pool / league redesign (`CastleDefense.Simulation/Program.cs`)

Old pool: 16-way uniform roll — 1/16 Random Dummy, 1/16 AntiSpam, 4/16 SpamBot
(tier ≈ headstart ± 2), 10/16 → uniform over the 10 old static league ONNX checkpoints
(v3/4/7/14/16/20/21/22/23/25). **No self-play, no HeuristicBot at all.**

New weighted pool (`OpponentKind` enum + explicit cumulative-probability roll):
- 3% Random Dummy, 3% AntiSpam, 6% SpamBot, 8% old static league models (kept for
  exposure to weaker/simpler play styles — a stale memory note flagged Random Dummy
  win rate declining over training from league overfitting, so this isn't zeroed out)
- 30% HeuristicBot (`CastleDefense.Engine.Bot.HeuristicBot`, side=2, fresh instance
  per episode) — a genuinely strong, non-ONNX anchor. Wired in by calling
  `heuristicBot.Update(engine)` once per real tick (mirrors the exact call convention
  `CastleDefense.BotArena`'s trace/hunt/spam modes already use — it self-paces its own
  5-tick decision cadence internally), immediately before `engine.Step(p1Action, 0, ...)`
  for that tick, since HeuristicBot acts directly on `PlayerState`/`Units` rather than
  through the discrete 14-action space.
- 50% Self-play: P2 uses the SAME live `trainingBrain` reference P1 uses (already kept
  current via the existing ACK-based reload mechanism) — a live mirror, not a frozen
  snapshot, so difficulty is always automatically calibrated to the model's own current
  skill. Falls back to Random Dummy in the (startup-only) edge case where
  `trainingBrain` is still null.

This is a pragmatic ~80/20 "current/past" split (current = HeuristicBot + self-play =
80%; past = dummy/antispam/spam/old-static-models = 20%), per Marc's OpenAI-Five framing,
using HeuristicBot as the fixed hard anchor and self-play as the adaptive one. **Judgment
call, not something Marc confirmed** — flagged here for review. A literal 80/20 was
chosen over something gentler because HeuristicBot is genuinely beatable in places
(v4 got HeuristicBot down to 72% per the latest dashboard sweep) and self-play difficulty
is self-calibrating by construction, so this shouldn't be able to produce a "can never
win, no gradient signal" dead end the way e.g. 80% HeuristicBot alone might early on.

No literal PPO `.zip` checkpoints survive anywhere in the repo (only inference-only
ONNX exports for the 10 kept models) — almost certainly the same/an adjacent cleanup
incident that destroyed ~144 replay recordings on 2026-07-14 (see
`project_recordings_data_loss` memory). This forecloses literally resuming from v25 or
v4's PPO weights.

## Warm-start decision (executed)

Since no `.zip` survives for v25 (or v4, the strongest model per the dashboard — see
below), literal PPO resume is impossible. **Decision: regenerate `castle_defense_p1_v25_bc.zip`
via `bc_pretrain.py`** (this is exactly what `TRAINING_BASE_MODEL` already pointed to)
from the 69 human replays that currently exist — all singleplayer (human vs. AI),
zero multiplayer (none survived the 2026-07-14 loss). `find_recordings_root()` doesn't
find the real location (`CastleDefenseGame2/recordings/`, not the `bin/{Debug,Release}/net10.0/recordings`
paths it checks) — had to pass `--replay-dir` explicitly, and as an ABSOLUTE path (a
relative one resolves against the wrong cwd, since the C# exporter subprocess runs
with `cwd=NET10_DIR`, not the caller's cwd — cost one failed attempt before catching
this).

Result: 69 replays → 45 P1-wins (24 skipped, P2 won) → 2,096 raw (obs, action) pairs →
1,270 after dropping 826 (39.4%) where the recorded action is invalid under the
re-simulated mask (a known, documented artifact of time-machine-started games being
re-simulated from tick 0 — see `filter_valid()`'s docstring). BC-trained a fresh
MaskablePPO (no `castle_defense_p1_v25.zip` to fine-tune from either — confirmed via
the script's own "[BC] Could not load ... starting from scratch" fallback) to 69.3%
action-prediction accuracy over 20 epochs. Saved as `castle_defense_p1_v25_bc.zip`,
matching `TRAINING_BASE_MODEL`'s existing name exactly — no config change needed there.

This is a thin dataset (1,270 examples, single-perspective, P1-wins-only, no
multiplayer gold-standard data) compared to whatever produced the ORIGINAL
`v25_bc.zip` before it was lost — expectations should be modest, but it's still a real
behavioral prior (invest 18% of actions, sensible tier-spawn distribution) rather than
pure random initialization, and costs nothing to include. Training run renamed from
`v26` (the prior plateaued attempt) to **`v27`** to keep this campaign's results
cleanly separable from the old, differently-configured run's history.

## Throughput measurement

With all three bugs above fixed (real CUDA, GA reward params applied, invest-explore
at 5%) and `batch_size=4096`, measured wall-clock throughput directly from live
`training_progress.csv` deltas (two independent Bash-only timed samples, since mixing
timestamps across different tool sandboxes turned out to have clock skew — one
apparent "589,824 steps in 6 seconds" reading was a measurement artifact from that,
not real):

- **N_ENVS=14 (unchanged from before): ~25,000-28,000 steps/sec, roughly 1,700-2,000
  games/minute.**
- **N_ENVS=18 (tried, since the CPU — i5-13600K, 14C/20T — showed headroom, bursting
  to 27-99% across samples rather than pegged): ~22,300 steps/sec — essentially flat
  or slightly worse, not the ~29% gain proportional scaling would predict.** GPU
  utilization was only ~45% during the N_ENVS=14 run, so the GPU isn't the bottleneck
  either. Most likely explanation: Python-side per-update work (batch reshaping, GPU
  re-inference, direct rollout-buffer writes, `model.train()`, ONNX export, and now a
  bigger total rollout to process — 147,456 samples/update at N_ENVS=18 vs 114,688 at
  N_ENVS=14) scales with total step count regardless of how many arenas produced it,
  and/or Python-side thread coordination overhead across more arena-receiver threads
  offsets the extra parallel C# simulation. **Reverted to N_ENVS=14** — 18 added
  processes/RAM/open ports for no measured benefit. Not deeply profiled further
  (e.g. no per-phase timing breakdown of collect vs. train vs. export within a single
  update) given the time budget for this campaign — worth a real profiler pass in a
  future session if throughput becomes the limiting factor again.

At ~1,800 games/minute, a multi-day unattended run should accumulate on the order of
millions of games — not literally "tens of thousands in minutes" but in the right
neighborhood over the course of the weekend, and a large step up from whatever the
prior (CPU-only-torch, wrong invest-explore, untuned-reward) runs were actually
achieving.

## Batch size

`batch_size` raised from 1024 → 4096 (114,688-sample rollout per update ÷ 4096 = 28
even minibatches/epoch × 6 epochs). Chosen as a "substantial but not reckless" first
step per Marc's ask — his old runs plateauing suggests noisy small-batch gradients
compounding with a weak opponent pool; `learning_rate` was already dropped to 0.0001
specifically "for large-batch training" per that comment's own history, so this should
already be anticipated/stable. Can be pushed further (8192) if the run looks stable
and GPU headroom allows — RTX 3070 Ti is trivially large for a [512,512] net regardless
of batch size in this range.

## Launch

Started 2026-07-24 ~14:16 EDT as two independent detached processes (both survive
this Claude Code session pausing/ending — launched via PowerShell `Start-Process`,
not a shell job tied to any one tool call):

1. **Training** — `ai_env\Scripts\python.exe -u train_ai_cluster.py`, cwd
   `CastleDefense.PythonAI/`, stdout/stderr → `campaign_run.log` /
   `campaign_run.err.log`. PID recorded in `campaign_run.pid`. `-u` (unbuffered) was
   necessary — the first launch attempt without it produced a completely empty log
   for the first ~20s+ despite the process running fine (Windows fully buffers
   redirected-to-file stdout by default), which would have left this whole campaign
   unobservable for a multi-day run. Model name `castle_defense_p1_v27`, warm-started
   from the freshly-regenerated `castle_defense_p1_v25_bc.zip`, `total_timesteps`
   raised to 2,000,000,000 (see rationale above), `batch_size=4096`, `N_ENVS=14`,
   GA-tuned reward params applied, new weighted opponent pool (HeuristicBot + self-play
   + old league + spam/antispam/dummy) live, `INVEST_EXPLORE=0.05`.

2. **Checkpoint benchmarking** — `benchmark_checkpoints.ps1` (same directory), a
   separate detached PowerShell process. Every 25 minutes: copies
   `current_model.onnx` (re-exported by the training loop after every PPO update) into
   `CastleDefense.Simulation/bin/Release/net10.0/league_models/` as
   `v27_snap_<UTC-timestamp>.onnx`, runs
   `CastleDefense.BotArena.exe models headstart 150 v27_snap_<timestamp>` (HeuristicBot
   vs that exact snapshot, sides alternated, time-machine head starts), and appends
   `(timestamp, training_steps, games, heuristic_winrate_pct, model_winrate_approx_pct,
   snapshot_file)` to `checkpoint_benchmark_log.csv` — full raw BotArena output also
   kept per-check under `checkpoint_benchmark_raw/`. Keeps only the 10 most recent
   snapshot files in `league_models/` (these are frequent, cheap self-checkpoints, not
   meant to accumulate as permanent league anchors — pruned automatically). **Caught
   and fixed one real bug before trusting this unattended**: the win-rate regex didn't
   account for `BotArena`'s fixed-width `{decisiveWinRate,5:F1}` padding (e.g.
   `( 70.0%)` has a leading space inside the parens) — verified against real captured
   output before launching, not just eyeballing the regex.

Both processes confirmed healthy after launch: 14 arena processes running, training
progress climbing (`training_progress.csv` growing every ~114k steps as expected),
benchmark loop's startup banner written cleanly.

## How to check on this later

- `tail campaign_run.log` / `tail training_progress.csv` — training progress
  (win rate is against the whole new weighted pool, not directly comparable to any
  pre-campaign number since HeuristicBot alone is ~15-30% for a weak/fresh model).
- `tail checkpoint_benchmark_log.csv` — the metric that actually matters: model's
  win rate specifically vs HeuristicBot over time, unaffected by opponent-pool mix
  changes.
- `Get-Process -Id (Get-Content campaign_run.pid)` / `...benchmark_loop.pid` — confirm
  both are still alive.
- To stop either: `Stop-Process -Id <pid> -Force`, then also
  `Get-Process CastleDefense.Simulation | Stop-Process -Force` to clean up arena
  children (they don't self-terminate if the parent Python process is killed
  ungracefully) — matches this project's standing "always kill stray game/arena
  processes" habit (see `feedback_style` memory).
- Periodic checkpoints: `castle_defense_p1_v27.zip` (saved every 10 PPO updates,
  skipped if a degenerate-policy check fires) is the safe resume point;
  `castle_defense_p1_v27_last.zip` only exists after a graceful stop (Ctrl+C), which a
  forceful `Stop-Process` won't produce — resume from the periodic `.zip`, not `_last`,
  if this gets killed hard.

## Secondary task: snipe|wall tactical pass (tried, rejected)

While training ran in the background, investigated the snipe|wall loadout — the
single weakest offense/defense combo per the latest dashboard sweep (82.6% overall,
74.5-94.5% by team, vs. HeuristicBot's usual 90%+). Added a small reusable tool first
(`hunt <opponent> [headstart] <offense> <defense>` in `CastleDefense.BotArena`, kept
even though the fix built with it was rejected — forces P1's loadout instead of
waiting for it to come up by chance across 200 rerolls) and traced real snipe|wall
losses against Tier4 spam and v4. Both traces showed the standard, already-understood
"genuinely overwhelmed, lost the economic race" pattern, not an obvious snipe-specific
bug — but reading `TryUseOffenseGadget`'s snipe case turned up a real structural
asymmetry: `TargetValueJustified` requires the ONE sniped unit's cost to clear
snipe's own cost, whereas nuke/firebomb's splash sums value across every unit hit —
so against cheap early swarms (many teams' tier1-3 units cost $1-10), snipe almost
never clears its ~$30 bar and sits unused for exactly the low-income phase wall (a
passive, non-clearing defense) also can't help with.

**Fix tried:** added `|| inDanger` to snipe's cast gate, mirroring the already-
validated freeze fix (`buyTimeJustifies = inDanger`, commit `2d92d53f`) — once
genuinely in danger, snipe the nearest threat regardless of its raw dollar value,
since it's stopping real ongoing chip damage right now (exactly what the case's own
doc comment already claimed snipe was for).

**Rejected after full two-replicate validation** (spam n=400x2, models n=300x2,
headstart, benchmark loop paused during both replicate pairs to avoid CPU
contention with the validation runs, training left running throughout since game
simulation is deterministic/tick-based, not wall-clock-paced, so contention only
affects validation speed, not correctness):
- Spam: flat to slightly positive, no regression (Tier1 ~86%, Tier4 ~80.4% avg,
  everything else 91-99.5%).
- Models: **v4, the intended beneficiary, showed no consistent gain** (+2.7 then
  -1.7 across the two replicates, netting to +0.5 — noise, not a real improvement).
  Meanwhile **v16 (-8.55 avg), v21 (-6.4 avg), and v25 (-6.4 avg) all regressed
  consistently in BOTH independent replicates** — a real signal per this project's
  own established discipline (same-direction movement across two independent runs,
  not just one), not noise.

Reverted, documented as a rejected experiment in `HeuristicBot.cs` matching this
project's established pattern (same fate as the earlier-rejected snipe
`DeferForInvestment` attempt from an earlier session — see
[[project_ai_opponent_heuristic]]). **Lesson reinforced:** snipe is apparently a
genuinely hard gadget to tune here — two independent attempts motivated by sound,
specific reasoning (one about investment timing, one about danger-gated firing) have
now both been net losses against adaptive opponents despite each targeting a real,
correctly-diagnosed mechanism. A third attempt should use a genuinely different
lever, not another variant of loosening/tightening snipe's firing condition.

Not deployed to the live training run's HeuristicBot opponent (moot, since it was
reverted) — worth noting for the future: any HeuristicBot.cs change that IS kept
would need the training arenas restarted to pick up the new `CastleDefense.Engine.dll`
(each project has its own separate build output copy; a `dotnet build` doesn't affect
already-running processes), which has a real cost (loses the current in-flight batch,
though not the saved model checkpoint) — not something to do casually mid-campaign
without a specific reason.

## Checkpoint-vs-HeuristicBot benchmark, first 9 readings (97.7M-587M of 2B steps)

| training_steps | heuristic WR | model WR (approx) |
|---|---|---|
| 97.7M | 82.7% | 17.3% |
| 142.1M | 75.3% | 24.7% |
| 192.2M | 79.3% | 20.7% |
| 246.3M | 72.7% | 27.3% |
| 306.9M | 79.3% | 20.7% |
| 372.2M | 82.0% | 18.0% |
| 440.6M | 76.7% | 23.3% |
| 510.5M | 70.7% | 29.3% |
| 587.0M | 72.0% | 28.0% |

n=150/reading, noisy (this project's own established noise band is ~8-12 points at
this sample size), but the model's win rate vs HeuristicBot averages ~20.9% across
the first 3 readings vs ~26.9% across the most recent 3 -- a real, if modest, upward
drift. HeuristicBot itself beats ~70-95% of every prior best RL checkpoint this
project has ever produced, so a fresh/BC-only-warm-started model already winning
20-29% of games against it (at only ~29% of the total training budget) is a
meaningfully different, more encouraging signal than the old flat ~50%-vs-a-much-
weaker-opponent-pool plateau this campaign was launched to fix.

## Traced Marc's new Blue+snipe/wall wins, found and diagnosed a real trace-human bug

Marc recorded 2 fresh human wins (`883B91`, `A25FBF` in
`CastleDefenseGame2/recordings/singleplayer/`, `opponent_type=heuristic`, both
Blue/snipe/wall/**wave** as his loadout) specifically to see if his own tidal-wave
usage revealed anything actionable for the snipe|wall weak matchup or the wave-as-
time-bridge valuation. Ran `--trace-human` (single, cheap process -- did not need to
pause the benchmark loop or touch training, since replaying 2 short recordings is
near-instant and the underlying simulation is deterministic/tick-based, not
wall-clock-paced, so it can't corrupt any concurrent CPU-bound benchmark numbers even
under contention).

**Found a real, previously-undocumented bug in `--trace-human`'s replay fidelity for
any game where a `HeuristicBot`-driven side is present** (i.e. every `sp`/`practice`
recording tagged `opponent_type=heuristic` -- confirmed by testing 5 different
existing recordings, 4 of which diverged from their recorded outcome, only one
happened to land on the same winner by chance while still cutting hundreds of ticks
short). Root cause, confirmed by reading the code directly (not guessed):
- HeuristicBot casts gadgets via a direct `engine.UseGadget(side, gadgetId, position)`
  call with a REAL, meaningful position (`myCastlePos` for defensive intent -- e.g.
  snipe's doc comment: "aiming at our own castle makes it snipe whichever enemy is
  closest to reaching it").
- The `.replay` format only ever records the discrete action ID (0-13) per tick via
  `GameEngine.LastActionP1/P2` -- there is no field for a gadget's target position at
  all.
- `TraceOneHumanReplay` (and any other replay-driven re-simulation) can only replay
  the recorded discrete ID back through `engine.ApplyAction(side, actionId)`, and
  `ApplyAction`'s gadget cases (`GameEngine.cs` ~line 912-918) always pass position
  `-1`, which triggers a DIFFERENT "Bot Auto-Targeting" heuristic (~line 304-325):
  targets the closest enemy unit with a ±300 lead offset -- not `myCastlePos`.
- So any HeuristicBot-driven gadget cast gets silently RETARGETED during replay to a
  different unit/position than it really hit live, which can cascade into a
  completely different game trajectory and, eventually, a different winner/ending
  tick than what's stored in `game_records.db`/the `.replay` header.

Human-cast gadgets (Marc's own, as P1) are NOT affected by this specific bug --
the live web game's human-cast path also goes through `ApplyAction(1, actionId)` with
the same `-1` auto-targeting, so there's no information LOST for human casts
specifically (same targeting logic both live and in replay). The divergence is
entirely attributable to the OPPONENT side's (HeuristicBot's) gadget casts.

**Practical effect on this analysis:** the recorded action IDENTITY/TIMING (e.g. "Marc
cast his signature gadget at tick 2771") is reliable straight from the raw byte
stream regardless of this bug -- but the surrounding SIMULATED economic state
(money/HP%/army composition) in the trace printout is only as reliable as however
much of the game preceded the FIRST HeuristicBot gadget mistargeting event, which
isn't visible in the human-focused trace log (it only prints P1's non-wait actions,
not P2's). Both traced games showed clear symptoms: game `883B91`'s re-simulation
ended at tick 2888 (96s) vs. the recorded 6872 (229s); game `A25FBF`'s ended at 2572
(85s) vs. the recorded 7236 (241s) -- meaning the trace for `A25FBF` cut off BEFORE
any wave cast appears in the log at all, right as HP had already dropped to 25%
(exactly the kind of moment a wave cast would matter most, per Marc's own framing).

**What the reliable portion actually showed (game `883B91` only, since `A25FBF`'s
wave cast never appears before the trace's cutoff):** Marc invested 5 times reaching
income 59.9 by ~86s, then cast `wall_2` (tick 2667/88s, P2 army already at 8 units),
`wave_2` (tick 2771/92s, HP had just ticked down to 98%, P2 army had surged to 29
units), then `snipe_2` immediately after (tick 2817/93s, HP 80%, P2 army at 38 units).
**This is consistent with Marc's own framing of wave as a PROACTIVE stall cast** --
thrown right as a swarm was visibly forming (8→29 units in ~4 seconds) and HP had
JUST started dropping, not as a last-ditch reactive save at a much lower HP -- rather
than HeuristicBot's own reactive-only design (which only spends defensively once
`inDanger` is already true, i.e., after real damage has already started accumulating
past a threshold). This lines up with the standing, unresolved "genuinely overwhelmed"
failure pattern documented throughout [[project_ai_opponent_heuristic]] -- a bot that
only reaches for its own wall/wave-equivalent tools AFTER `inDanger` fires is
structurally always a beat late compared to a human who fires proactively the moment
a swarm is visibly forming.

**Not fixed this session** (out of scope for what was asked, and a real fix -- adding
a target-position field to the `.replay` binary format, a version bump, and updating
every writer/reader -- is a nontrivial, separate piece of engineering, not a quick
patch). Flagged as a follow-up task (see below) since it affects the trustworthiness
of `--trace-human` for EVERY `opponent_type=heuristic` recording that has ever been
or will be made, not just these two.

**If this general "proactive stall gadget, cast before `inDanger`, not after" idea is
pursued as an actual HeuristicBot change** (a genuinely different lever than either
of the two already-rejected snipe-tuning attempts -- see above -- since it's about
WHEN defensive gadgets fire relative to `inDanger`, not about snipe's specific value
gate): note that freeze already has exactly this shape (`buyTimeJustifies = inDanger`,
committed and validated) but wall/wave still gate on `inDanger` too (per
`TryUseDefenseGadget`/wave's signature-slot handling) -- so "cast proactively on a
forming swarm, not just once already in danger" would need a DIFFERENT, earlier
trigger than `inDanger` itself (something like "enemy unit count crossed N in the last
few decisions," mirroring the swarm-formation signal Marc's own account describes),
not just reusing `inDanger` again. Worth trying, but per this project's whole
snipe/wall tuning history, needs full two-replicate validation before trusting any
one-off benchmark read.

---
*(Log continues below as the campaign progresses — periodic benchmark results,
plateau diagnosis if one occurs, and any further tuning.)*
