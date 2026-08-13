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

## POST-TRAINING QUEUE (do NOT implement until the v27 training run finishes)

Marc queued two HeuristicBot changes explicitly deferred until after training
completes (both need the training arenas' CPU headroom free, and both should go
through the standard two-replicate validation discipline once implemented):

**1. Wave/wall "swarm is forming" trigger.** From the tidal-wave trace above: fire
proactively (independent of `inDanger`) once the enemy army is rapidly growing, the
way Marc's own play does it -- not only after `inDanger` (real accumulated damage)
already fires, which is structurally always a beat late. Candidate signal: something
like "enemy unit count rose by N over the last few decisions" (mirrors the swarm-
formation read from the `883B91` trace: 8->29 units in ~4 seconds right as HP first
ticked down). Needs its own new trigger, not a reuse of `inDanger` -- freeze already
has an `inDanger`-gated proactive fix (`buyTimeJustifies`, commit `2d92d53f`), but
that's a different shape of fix (still `inDanger`-based) than what this asks for.

**2. Snipe targeting radius -- CORRECTED 2026-07-25: this is a GAME MECHANIC change,
not a HeuristicBot-only change.** Marc's explicit correction: "the sniper targeting
change im requesting is also for human players. When the gadget is targeted from a
click on the screen, there should be an automatic search in the near vicinity to find
the highest HP target." So the fix belongs in `SnipeEffect.Execute()` itself
(`CastleDefense.Engine/Gadgets/SnipeEffect.cs`, confirmed/read this session) -- it
resolves EVERY snipe cast's target from a `position` argument regardless of whether
that position came from a human's screen click or `HeuristicBot`'s `myCastlePos` call,
so fixing the effect fixes both simultaneously; no separate bot-side change needed.

Current implementation (confirmed by reading it): picks the single enemy unit whose
position is closest to the cast `position`, with a tie-break on `MaxHealth` that only
fires on an EXACT distance tie (`==` on a continuous position value -- essentially
dead code in practice, matches Marc's "rare that you're able to exactly target the
unit you want" framing exactly). Fix: within some radius of `position`, pick the
HIGHEST-HP unit in that window instead of strictly the nearest one -- so a click near
a high-tier/high-HP target surrounded by cheap fodder lands on the intended target
instead of whichever tier-1 unit happens to be a pixel closer.

**This is a real GAME BALANCE/mechanics change** (affects human play, not just bot
decision quality) -- **validate accordingly**: standard two-replicate
HeuristicBot-vs-benchmark discipline still applies (since HeuristicBot also casts
snipe and will benefit/change too), but this also changes what a HUMAN's snipe clicks
do, so it's worth Marc's own playtest feel-check in addition to the automated
win-rate validation, not just the BotArena numbers alone. Radius size is a new tunable
constant with no existing precedent to copy -- pick a first-guess value (e.g. on the
order of a unit-cluster's typical spacing, would need reading `master_roster.csv`/unit
`Width` or checking typical inter-unit spacing empirically), validate, and be ready to
sweep it if the first guess is clearly too wide (grabs targets across the whole field)
or too narrow (barely different from today's exact-nearest behavior). This is
distinct in kind from both prior rejected snipe attempts (`DeferForInvestment` gating
and the `inDanger` firing-condition change above), since it doesn't touch WHEN/WHETHER
snipe fires or WHERE it's aimed, only WHICH unit gets hit once it's aimed at a
region -- the "genuinely different angle" this project's snipe-tuning history needed.

Both are explicitly deferred -- do not implement until the v27 run reaches its
stopping point (or is deliberately stopped) and the post-training evaluation plan
below has run.

## Post-training plan (drafted 2026-07-25, before the run finished)

Marc asked whether an autonomous plan exists for after training completes, given
~30h estimated remaining (though observed throughput this run -- 1.66B/2B steps in
~10.3h as of this writing -- suggests it may finish well before that estimate; will
update this section once it actually stops). Concrete steps, in order:

1. **Confirm the run's actual stopping condition.** Either it reaches 2B steps and
   exits cleanly (`train_ai_cluster.py`'s own `total_timesteps` check), or it needs to
   be stopped deliberately (e.g. if the checkpoint-vs-HeuristicBot trend clearly
   plateaus well before 2B, no need to burn the full budget). Either way: confirm
   `castle_defense_p1_v27.zip` (the periodic checkpoint, saved every 10 updates) is
   present and not from a degenerate-policy rollback, and stop the benchmark loop +
   arena processes cleanly (`Stop-Process` on the tracked PIDs, then confirm no
   stray `CastleDefense.Simulation.exe` processes remain, per the standing
   "kill stray game processes" habit).

2. **Full evaluation via BotArena, not just the periodic 150-game spot-checks.**
   Export `castle_defense_p1_v27.zip` to ONNX (`export_onnx.py`/
   `export_league_models.py` pattern), drop it into `league_models/` alongside the
   existing v3/4/7/14/16/20/21/22/23/25, then run the full `dashboard` mode (team x
   offense x defense cross-tab, all 8 spam tiers + every model including v27) for a
   real, high-confidence read -- not just aggregate win rate, the same team/loadout
   breakdown that already found Tier4's roster-imbalance and snipe|wall's weakness
   for the heuristic side. Compare v27's aggregate HeuristicBot win rate against v4's
   72.0% (the current strongest model) -- that's the real bar for "did this campaign
   actually produce a better model."

3. **Branch on outcome:**
   - **If v27 is a clear step up (materially beats v4's ~72% heuristic-win-rate
     bar, i.e. HeuristicBot wins LESS than ~72% against v27):** export it into the
     live `league_models/` sets (training arenas' AND the web game's, per
     `export_league_models.py`'s existing copy-to-both-locations behavior) so it
     becomes a real league anchor going forward. Consider a SECOND warm-started run
     from v27 itself (now that a real, better PPO `.zip` checkpoint exists to resume
     from, unlike this run's BC-only warm start) -- likely the single highest-leverage
     next step, since resuming from an actually-decent policy rather than a thin BC
     prior should compound faster. Exploiter-style opponents (per the original
     campaign brief's stretch goal) become worth building at that point too, once
     there's a genuinely strong model worth trying to break.
   - **If v27 plateaued or regressed (still losing badly to HeuristicBot, similar to
     the old ~50%-vs-weak-pool pattern):** diagnose before re-running blind. Check
     the full `training_progress.csv`/`training_progress_opponents.csv` history for
     where/whether it plateaued, check `checkpoint_benchmark_log.csv`'s trend for the
     same, and specifically check per-opponent-type win rate (self-play vs
     HeuristicBot vs old-league-models) to see whether the 80/20 weighting needs
     rebalancing, or whether self-play collapsed into a degenerate strategy, or
     whether batch_size=4096 needs to go further. This is exactly the kind of
     diagnosis-over-blind-retry the original campaign brief asked for.

4. **Implement and validate the two queued HeuristicBot changes above** (wave/wall
   swarm trigger, snipe targeting radius) at standard two-replicate discipline, same
   as every other change in [[project_ai_opponent_heuristic]].

5. **Keep the records current**: update `TRAINING_CAMPAIGN_LOG.md` and
   `project_ai_training.md`/`project_ai_opponent_heuristic.md` memory with the final
   outcome, same discipline as the rest of this campaign.

## Exploiter degeneracy gate (Marc green-lit both warm-start continuation AND a full exploiter run, 2026-07-25)

Marc approved building a full exploiter-training loop (not just the basic single-run
probe), on the condition of an automated degeneracy check so exploiter results can be
evaluated autonomously without him having to watch every game. His method, verbatim:
"A great way we can look to see if the exploiter is a degenerate strategy or not is to
compare action distributions. You can take a second to analyze the human games we have
recorded and compute the mean non-wait (non-zero) action distribution. Then you can
compare the exploiters action distribution to see if its completely degenerate. I'd
say any action reaching 90+% of total actions would probably be a degenerate
strategy."

**Human baseline computed (prep step, done now, doesn't touch training):** loaded
`bc_sp.bin` (fresh, 2026-07-24, P1-wins-only singleplayer human games) and
`bc_mp.bin` (STALE -- dated 2026-07-09, predates the 2026-07-14 recordings data-loss
incident, kept only as a secondary cross-check since no real multiplayer replays
currently exist) directly -- both already contain ONLY non-wait actions (the BC
exporter itself only records significant actions, not idle "wait" ticks, so no extra
filtering was needed to isolate "non-wait" specifically).

| action | sp (2026-07-24, n=2096) | mp (STALE, n=417) | combined (n=2513) |
|---|---|---|---|
| Tier1 | 9.4% | 29.3% | 12.7% |
| Tier2 | 1.6% | 2.2% | 1.7% |
| Tier3 | 3.3% | 4.1% | 3.4% |
| Tier4 | 10.3% | 5.0% | 9.4% |
| Tier5 | 8.4% | 4.1% | 7.7% |
| Tier6 | 5.1% | 0.2% | 4.3% |
| Tier7 | 12.3% | 0% | 10.2% |
| Tier8 | 2.4% | 0% | 2.0% |
| Invest | 28.4% | 19.2% | 26.9% |
| Repair | 2.4% | 3.1% | 2.5% |
| OffGadget | 3.8% | 7.7% | 4.4% |
| DefGadget | 9.0% | 20.4% | 10.9% |
| SigGadget | 3.6% | 4.8% | 3.8% |

**Reference: max single non-wait action across all three views is Invest at 28.4%
(sp) / 26.9% (combined) -- well under Marc's 90% degenerate threshold, with real
weight spread across every tier and every gadget category.** This is the healthy-play
reference distribution. Invest being the single largest category matches this
project's whole standing narrative (investment timing is the highest-leverage,
hardest-to-learn decision in this economy) -- not a red flag, expected.

**Post-training exploiter gate (to apply once an exploiter run exists):** compute the
exploiter's own non-wait action distribution the same way (straightforward from the
training arena's own per-action tallies, or a quick post-hoc pass over its recorded
games) and compare against this table.
- **Any single action >= 90% of the exploiter's own non-wait actions -> flag as
  degenerate.** Likely exploiting an engine/balance artifact (e.g. spamming one
  specific gadget or tier nonstop rather than playing a real strategy) -- report it to
  Marc as a found bug/imbalance rather than feeding it back into v27's training pool.
- **A spread broadly similar in SHAPE to the human reference (no single category
  anywhere near 90%, real representation across multiple tiers/gadgets/economy
  actions) -> passes the degeneracy check.** Combined with traces of actual games
  (not just the aggregate win rate) looking like real, sensible play, this is the
  autonomous go/no-go gate for whether an exploiter's win is worth feeding back into
  target training: **non-degenerate by this test AND traces look real -> candidate to
  feed back; degenerate by this test -> report as a found exploit, do not train
  against it.**

## TRAINING RUN COMPLETE (discovered 2026-07-25 ~13:27 EDT, had finished hours earlier)

`train_ai_cluster.py` reached its full 2,000,000,000-step budget and exited cleanly
at **2026-07-25 02:39-02:40 EDT** (log: "Reached 2,000,000,000 timesteps. Training
complete." / "Done." / "Last clean checkpoint remains at castle_defense_p1_v27.zip"),
**~12.4 hours after launch, well under Marc's ~30h estimate** -- both
`castle_defense_p1_v27.zip` (periodic checkpoint) and `castle_defense_p1_v27_last.zip`
(final graceful save) are present and intact, no errors anywhere in the log. The
training process (and its 14 arena children) had already exited on their own; only
the checkpoint-benchmark loop kept running afterward (harmlessly re-benchmarking the
now-static final model every 25 minutes for the following ~11 hours, since nothing
told it training had stopped) -- this wasn't caught earlier because nothing was
actively polling for the stop condition during that window.

**Silver lining: those extra ~11 hours of redundant benchmarking produced 26
independent 150-game readings of the exact same final, fully-converged checkpoint --
3,900 games total, a genuinely large, high-confidence sample of v27's real strength.**
Final result: **mean 20.6% win rate vs HeuristicBot (range 15.3-25.3% across the 26
readings), NOT a clear improvement over v4 (the current strongest model, 28.0% win
rate vs HeuristicBot per the last full dashboard sweep).**

**Correcting an earlier report:** with only 9 early readings available, the previous
status update read a "modest upward drift" (~21%->~27% first-3 vs last-3 average).
**With the full 53-reading history now available, that reversed** -- binning into 5
chunks across the whole run shows the model's win rate vs HeuristicBot actually
PEAKED around 22-24% mid-training (steps ~100M-1.4B) and drifted back DOWN to ~20-22%
by the end (steps ~1.5B-2B and the 26 post-completion re-reads), settling at ~19.6%
in the very last few readings. **The early "upward drift" was real but didn't hold --
this looks more like a peak-then-mild-decline shape than sustained improvement,**
though still nowhere near catastrophic (no collapse, stayed in a fairly narrow
15-33% band the whole run). Full per-chunk numbers:

| readings | steps range | mean model WR |
|---|---|---|
| 1-10 | 98M-662M | 22.8% |
| 11-20 | 741M-1431M | 24.3% |
| 21-30 | 1504M-2000M | 21.5% |
| 31-40 | 2000M (post-completion re-reads) | 21.1% |
| 41-50 | 2000M (post-completion re-reads) | 19.9% |
| 51-53 | 2000M (post-completion re-reads) | 19.6% |

**This is an honest, not-fully-successful outcome on the narrow "beat v4" bar** --
though this heuristic-only benchmark is a useful proxy, not the full picture (the
`models` dashboard sweep against the OTHER 9 old league checkpoints, team/loadout
breakdowns, etc. haven't been run yet for v27 -- that's the next, already-approved
step). Given this, the per-opponent-type training-progress breakdown
(`training_progress_opponents.csv`) is worth checking specifically for whether
self-play collapsed into something degenerate, or whether the model overfit to
beating the easier 20% of the pool (spam/dummy/old-league) while genuinely
plateauing against HeuristicBot/self-play specifically -- diagnosis, not blind
re-launch, per the standing campaign discipline.

## Full model ranking, post-training (2026-07-25) -- a real regression, not just a shortfall

The full team/loadout dashboard sweep (`dashboard` mode) was too slow to be practical
for a same-day answer (~40 min for a standard model, ~90+ min projected for v4/v7's
priority weighting, 21 opponents total -- killed after 2/13 models to avoid an
8+ hour wait) -- switched to the faster `models headstart 300` mode (aggregate win
rate only, no per-team/loadout breakdown) to answer the actual question asked
(ranking), and it finished in a reasonable time. Full result, n=300/model, sorted by
model strength (ascending heuristic win rate):

| rank | model | heuristic WR | model WR |
|---|---|---|---|
| 1 | **v25_bc** | 69.0% | **31.0%** |
| 2 | v4 | 73.0% | 27.0% |
| 3 | v23 | 75.0% | 25.0% |
| 4 | v27_last | 77.0% | 23.0% |
| 5 | v25 | 77.3% | 22.7% |
| **6** | **v27** | **78.7%** | **21.3%** |
| 7 | v21 | 81.3% | 18.7% |
| 8 | v16 | 82.3% | 17.7% |
| 9 | v20 | 83.3% | 16.7% |
| 10 | v7 | 86.3% | 13.7% |
| 11 | v22 | 90.0% | 10.0% |
| 12 | v3 | 90.7% | 9.3% |
| 13 | v14 | 96.3% | 3.7% |

**v27 ranks 6th of 13 -- middle of the pack, not the strongest, and not better than
v4** (matches the checkpoint-benchmark log's own final ~20.6% average). **But the
more important finding: `v25_bc` -- the BC-only warm-start base this entire 2B-step,
~12-hour run started FROM, with zero RL training applied -- is the single strongest
model in the whole league, clearly ahead of v27 (31.0% vs 21.3% model win rate) and
even ahead of v4.** This means the RL run didn't just fail to improve on its starting
point -- **it left the model measurably WORSE than where it began.** This is a
regression, not merely a disappointing result, and changes the framing for what to do
next: warm-starting a continuation FROM v27 would mean building on top of a
regression, not real progress. Before deciding what to warm-start from, the next step
is diagnosing the actual mechanism (check `training_progress_opponents.csv`'s
per-opponent-type breakdown for signs of self-play collapsing into something
degenerate, or the model overfitting the easy 20% of the pool at the expense of real
skill against HeuristicBot/self-play specifically) rather than assuming the fixes
made this campaign didn't work at all -- most of them (CUDA, GA reward params,
invest-explore, league diversity) are independently well-justified and still likely
correct; something else in the 2B-step run itself degraded the policy.

## ROOT CAUSE FOUND: the whole v27 run's P1 side was acting randomly, not from its own policy

Diagnosed why v27 regressed below v25_bc. Checked all four of Marc's hypotheses
against real evidence rather than guessing:

**(a) Self-play collapse -- REJECTED, replaced by a more basic finding: self-play
never ran at all.** `training_progress_opponents.csv` (366,206 rows spanning the
whole run) contains the string "Self-Play" **zero times**, despite the opponent pool
being coded for a 50% weight. At the very first checkpoint (before any per-opponent
rolling-window statistic saturates, so raw counts are meaningful), the actual
observed distribution was **Random Dummy 56.1%, Heuristic Bot 26.8%**, spam/antispam
~10%, league models collectively ~5% -- i.e. every self-play roll (50% of ALL games)
was silently landing on Random Dummy instead. Not a learned degenerate equilibrium --
self-play never had the chance to run at all.

**(b) Catastrophic forgetting -- CONFIRMED, but the mechanism is more basic than
"RL reward pulled it toward a different optimum."** Root cause (found by checking
`CastleDefense.Simulation/Program.cs`'s `modelPath` resolution against
`train_ai_cluster.py`'s `start_arenas()`): each arena was launched with only a port
argument, so `modelPath` defaulted to the bare relative filename
`"current_model.onnx"`, resolved against the arena's own working directory
(`NET10_DIR` = `CastleDefense.Simulation/bin/Release/net10.0/`) -- but
`export_current_model()` writes the file to `_SCRIPT_DIR` (`CastleDefense.PythonAI/`,
where the Python script itself runs). **Confirmed directly: the file never existed at
the path any arena ever checked** (`ls` on that exact path during the live campaign
returned nothing). Consequence: `trainingBrain` was **null for the entire 2-billion-
step run, in every arena, the whole time**. Since `p1Action = trainingBrain != null ?
trainingBrand.GetBestAction(...) : GetRandomValidAction(p1Mask)`, **P1's own actions
during every single game fell back to uniformly random, valid-but-otherwise-arbitrary
moves -- never the model's own learned policy.** PPO was training on (state,
random-action) pairs completely disconnected from the policy being updated for the
ENTIRE run -- not a subtle reward-shaping issue, a basic plumbing bug that severed the
model from its own rollout data.

This also explains why the whole-pool win rate/reward/invest-rate were **completely
flat across all six chunks of the run** (55.9-56.1% winrate, ~122.4-122.9 reward,
~0.098-0.104 invests/game, every single chunk, start to finish -- verified directly,
not eyeballed) -- there was no real feedback loop for the model's OWN choices to
improve, since its own choices were never actually being executed. 2 billion steps of
policy-gradient updates weighted toward imitating uniformly-random actions plausibly
explains the slow erosion of the BC-derived weights (a real, if unglamorous,
degradation mechanism) rather than any coherent rise-then-plateau trajectory.

**(c) Reward misspecification -- not supported as a separate/additional cause.**
Reward and win-rate moved together the whole run (both flat), not divergently -- if
the GA-tuned reward params were pointing the model toward a wrong-direction proxy,
reward would be expected to rise while win-rate fell or vice versa. No such divergence
was observed. Not ruled out as a minor contributor, but the modelPath bug alone is
sufficient to explain everything observed, so this wasn't chased further.

**(d) Overfitting the easy 80/20 split -- moot given (b)/(a): there was no real 80/20
split in practice.** With self-play collapsing to Random Dummy, the ACTUAL realized
opponent mix was roughly 56% trivial Random Dummy + 27% HeuristicBot + ~17% everything
else -- a much weaker/easier realized curriculum than designed, compounding (b)'s
damage rather than being an independent cause.

**Fixed** (`train_ai_cluster.py`'s `start_arenas()`): pass the ONNX path as an
explicit absolute argument to each arena, eliminating the cwd-relative ambiguity
entirely (`CastleDefense.Simulation/Program.cs`'s `modelPath` already correctly
accepted a second CLI arg -- it just was never given one). **Verified with a live
smoke test post-fix, not just code review:** "Self-Play" now appears at ~49% share
(matching the intended weight almost exactly), Random Dummy back down to its correct
~3%, and **`Invests/Game` jumped from ~0.1 (the ENTIRE broken v27 run, every single
chunk) to ~2+ (the fixed smoke test) -- a ~20x difference**, consistent with a policy
that actually understands investing matters (matches the human baseline's 28.4%
Invest share) rather than one whose actions were noise.

## Recommendation

**What to warm-start from:** `castle_defense_p1_v25_bc` -- still the strongest model
in the league (31.0% vs HeuristicBot) and completely untouched by this bug (BC
pretraining doesn't go through the arena/self-play path at all). Do NOT warm-start
from `v27` -- it's a regression, not a checkpoint worth building on.

**What config to change:** the `start_arenas()` fix above is necessary and sufficient
to make self-play (and therefore the whole opponent-mix design) actually work as
intended for any future run. No other config change is indicated by this diagnosis --
reward params, batch size, invest-explore rate, and the opponent-pool weights
themselves were never actually exercised as designed, so none of them are implicated.

**On Marc's own question -- given v25_bc/BC is currently the strongest model, is more/
better behavior cloning actually the higher-ROI path over more RL right now?**
Genuinely worth weighing, not a rhetorical question to wave past:
- BC's ceiling is fundamentally capped by imitating existing human play -- it cannot
  discover strategies better than what's in the demonstration data, no matter how much
  more data is collected. RL's whole value proposition is the ability to exceed human
  play through self-directed exploration -- but that value was never actually
  realized this run because of the bug above, not because RL itself failed here.
- Given the bug is now fixed and verified, a proper RL run (self-play genuinely
  running at ~50%, P1 genuinely acting from its own policy) hasn't been tried yet --
  this run doesn't provide real evidence against RL's potential, since it never
  actually ran as designed.
- More/better BC data (particularly real multiplayer human-vs-human games, weighted
  10x over singleplayer in `bc_pretrain.py`'s own design -- zero currently exist,
  all lost in the 2026-07-14 incident) is comparatively cheap and would likely raise
  the BC floor further regardless of what happens with RL, and Marc is the one who'd
  need to actually play those games.
- **This is a genuine fork needing Marc's call, not something to guess at:** (1)
  relaunch RL now that the bug is fixed and see if it actually improves on v25_bc this
  time (the original plan, now on a real footing), (2) prioritize collecting more/
  better human demonstrations (especially multiplayer) and re-run BC, or (3) both,
  time permitting. Given how much of the weekend may remain, flagging this explicitly
  rather than picking one autonomously.

## v28 relaunch (2026-07-25, Marc's decision: focus on RL, BC off the table for now)

Marc chose to focus on RL (can't provide more recordings, so BC improvement isn't
available right now) and asked to relaunch autonomously now that the plumbing bug is
fixed, with an explicit requirement: build an actual watchdog this time, not just
manual check-ins, so a repeat of the v27 failure mode (12+ hours to notice) can't
happen again.

**Config:** `TRAINING_MODEL_NAME` -> `castle_defense_p1_v28`, warm-started from
`castle_defense_p1_v25_bc` (unchanged -- still the strongest model, confirmed
unaffected by the bug). Same `total_timesteps=2_000_000_000` budget (no reason found
to change it). Archived all v27 artifacts (`checkpoint_benchmark_log_v27_ARCHIVE.csv`,
`campaign_run_v27_ARCHIVE.log`, `checkpoint_benchmark_raw_v27_ARCHIVE/`) rather than
deleting, so the diagnosed failure stays inspectable.

**Two new pieces of infrastructure, both requested explicitly:**

1. **`benchmark_checkpoints.ps1` now stops itself once training actually finishes**,
   instead of idling for hours re-benchmarking a static model (exactly what happened
   for ~11 hours after v27 completed). Tracks the training PID (`campaign_run.pid`)
   and `training_progress.csv`'s step count; requires BOTH "process not found" AND
   "steps unchanged" to hold for two consecutive cycles before stopping (a single
   coincidence of either alone isn't trusted). Also parameterized the snapshot tag
   (`-ModelTag v28`) instead of hardcoding `v27_snap_*`.

2. **New `sanity_watchdog.py`** -- a one-shot (not looping) fail-fast check launched
   alongside training:
   - **Fast phase (~5 min in):** reads `training_progress_opponents.csv` for
     Self-Play's actual share of the opponent pool and `training_progress.csv` for
     invests/game. If Self-Play's share is under 25% (designed weight is 50%) OR
     invests/game is under 0.5 (healthy runs show ~2+, the v27 bug showed a flat
     ~0.1) -- **both are the exact measurable signature of the v27 bug** -- it kills
     the training and arena processes immediately and logs why, rather than letting
     a broken run burn its full budget unnoticed.
   - **Slow phase (~1 hour in):** logs the checkpoint-vs-HeuristicBot benchmark's
     trend so far (informational, not a hard gate -- one hour isn't enough readings
     to trust given this project's established noise band).

**Launched as three independent detached processes** (`Start-Process`, survive
session pauses): training (PID tracked in `campaign_run.pid`), the fixed benchmark
loop (`benchmark_loop.pid`), and the sanity watchdog (`watchdog.pid`).

**Early numbers already look dramatically different from the broken run** (confirmed
directly from the live log at the first checkpoint, 802,816 steps / 977 games):
**Self-Play: 500 games (~51% share, matching the intended 50% weight almost
exactly)** and **Invests/Game: 0.94 and climbing** -- vs. the ENTIRE v27 run's flat
~0.1 the whole way through. This is strong, direct, early confirmation the model is
genuinely driving its own actions this time, not the earlier automated watchdog
verdict alone (that check is still pending its first 5-minute mark as this was
written, and will be reported once in).

## The watchdog itself had a bug: a false alarm at ~3.5M steps, found, fixed, relaunched

The new `sanity_watchdog.py` did exactly what it was built to do -- caught a
suspicious signal fast and halted training rather than waiting -- except this time
the suspicious signal was wrong. At its first fast check (~5 min in, step 3,555,328),
it computed Self-Play's "share" of the opponent pool as 24.6% (below the 25% floor)
and killed a genuinely healthy run.

**Root cause of the false alarm:** `training_progress_opponents.csv`'s `sample_count`
column is a rolling deque capped at `maxlen=500` (see `ProgressTracker` in
`train_ai_cluster.py`) -- once an opponent has been selected 500+ times, every future
row shows exactly 500 regardless of how much more it's actually been played. Checking
directly at the exact moment the watchdog fired confirmed **both `Self-Play` and
`Heuristic Bot` had already hit the 500 cap** while every other, rarer opponent
(individual spam tiers, old league models) was still well under it -- so the "share
of the sum" computation was comparing a cap-corrupted value against uncapped smaller
ones, artificially deflating the two high-frequency opponents' apparent share. The
RAW evidence (500, the maximum trackable value, for both) was exactly what a working
self-play mechanism should show -- the real v27 bug's signature was ZERO occurrences
ever, categorically different from "a smaller-than-expected share of a capped sum."
This is the same rolling-window artifact already discovered and worked around earlier
this session (when first trying to measure true opponent-selection frequency from
this file) -- should have been anticipated when writing this exact check and wasn't.

**Fixed:** replaced the share-of-capped-sum computation with a simple absolute floor
on Self-Play's raw sample count (`>= 20`) -- immune to the cap artifact, since a
working mechanism clears a small absolute floor quickly regardless of what other
opponents have or haven't saturated, while the real bug's signature (exactly zero,
forever) will never clear any positive floor at all.

**Cleaned up** the orphaned `plot_training.py --watch` subprocess left behind by the
forceful `taskkill` (same pattern as before -- killing the parent doesn't kill its
children) and **relaunched all three processes** (training, benchmark loop, fixed
watchdog) immediately. Reported to Marc transparently rather than glossing over it --
the watchdog he asked for is now itself validated against a real false-positive case,
which is arguably a more convincing sign it's well-calibrated than if it had simply
never fired at all.

## Invests/game as a progress metric, not just a sanity gate (2026-07-25)

Marc's instinct: "for a healthy strong strategy the average number of invests per
game should be around 5+... something to have an eye on" -- flagged as worth
verifying precisely rather than trusting the guess, and worth tracking over training
as a progress signal (does it climb as the model improves), not just the sanity
watchdog's binary alive/dead check.

**Human reference (exact, not estimated):** recovered the precise game count from the
saved `bc_pretrain_run.log` -- 53 of the 69 available replays were P1 (human) wins
(16 skipped as P2 wins). The raw `bc_sp.bin` export (before BC's separate mask-
validity filtering, which is about training-data quality, not about whether an
action really happened) contains 595 total Invest actions across those 53 games:
**595 / 53 = 11.2 invests/game in human wins.** Notably higher than Marc's own ~5
guess -- his estimate undersold it, exactly the kind of thing worth verifying rather
than assuming.

**Model references (new tool built for this, `invest-stats` mode in
`CastleDefense.BotArena`):** neither `RunMatchup` nor any existing mode tracks a
model's own final `InvestmentCount` (only win/loss/timeout) -- added a small
dedicated mode (`invest-stats <modelFragment> [headstart] [games]`) that plays a
model vs HeuristicBot (sides alternated) and reports both sides' average final
`InvestmentCount` (a `PlayerState` field that only ever increases, so its value at
game-over is exactly the total number of times that side invested). Ran n=60/model,
headstart, kept modest specifically to limit CPU competition with the live training
run:

| source | avg invests/game | context |
|---|---|---|
| Human wins (n=53 games) | **11.2** | strongest reference, real wins |
| HeuristicBot (vs v4) | 5.17 | the project's own tuned, strong bot |
| **castle_defense_p1_v4** (strongest RL model vs HeuristicBot) | **4.48** | close to HeuristicBot's own number |
| HeuristicBot (vs v25_bc) | 4.77 | -- |
| castle_defense_p1_v25_bc (v28's warm-start base) | 2.15 | notably lower than v4 |

**Reading on this:** strong/competent play (human wins, HeuristicBot itself, and v4 --
the strongest RL checkpoint against HeuristicBot) clusters in the **~4.5-11 range**,
while `v25_bc` (weaker economically despite still being the single strongest model by
raw win rate, per the earlier full ranking) sits at **~2.15** -- a real, measurable
gap between "has a decent overall strategy" and "has a genuinely strong economy."
Worth noting explicitly: invests/game isn't a strictly monotonic proxy for win rate
by itself (v25_bc actually has a HIGHER win rate vs HeuristicBot than v4 despite
fewer invests/game -- different viable strategies can win without maxing the
economy) -- but it's still a meaningful, independent signal of whether a model is
developing a real economic game, distinct from just "does it win."

**v28's current invests/game (from `training_progress.csv`, tracked continuously) is
~1.1-1.7 so far** (noisy at this very early stage, ~1% of the training budget) --
essentially at or slightly below its own `v25_bc` starting point's level (2.15), not
yet showing the climb toward the 4.5+ "strong economy" range that would indicate real
improvement. **Interpretation going forward, exactly per Marc's framing:** if this
stays pinned near ~2 while the win-rate-vs-heuristic benchmark also stalls, that's a
sign the model isn't learning the economic core (a real diagnostic signal, distinct
from the sanity watchdog's pass/fail); if it climbs toward the 4.5+ range alongside a
rising win-rate-vs-heuristic trend, that's genuine strategic improvement, not just
noise. **This will be reported alongside the checkpoint-vs-heuristic benchmark in
every periodic status update from here on**, not tracked separately/silently.

## Seat-bias root-cause investigation (2026-07-26): found the mechanism, fix not yet safe to ship

Marc's priority: fix the P1/P2 seat bias at the engine root (not accommodate it in
training), verified via a controlled mirror match (identical team+loadout both
sides, HeuristicBot vs itself). Stopped v28 cleanly first (training + benchmark loop
+ all 14 arenas + orphaned watcher processes -- confirmed zero stray processes
before starting).

**Built the controlled test** (`mirror-fixed <team> <offense> <defense> [games]
[headstart]` in `CastleDefense.BotArena`) since the existing `mirror` mode randomizes
team/loadout independently per side, confounding team-balance noise with any real
engine bias. `AssignLoadout` forces the identical team+offense+defense+signature on
both P1 and P2.

**Baseline (before any change), n=300/combo:**
- White/nuke/wall: P1 40.3% / P2 58.7%
- White/snipe/reinforcements: P1 95.3% / P2 4.7%
- Blue/snipe/reinforcements: P1 85.0% / P2 15.0%

**The bias is large but NOT uniform in direction or magnitude** -- it swings from a
mild P2 favor (nuke/wall) to an extreme ~90/10 P1 favor (snipe/reinforcements,
consistent across two different teams), ruling out a simple "P1 always wins more"
explanation and pointing at something whose IMPACT depends on playstyle.

**First candidate found and fixed (confirmed via `git log -p`, not guessed): `GetDistanceToEnemyCastle`**
included `attacker.Width` in the Side-1 branch (accounting for a unit's body reaching
the castle from its leading edge) but never mirrored the term onto the Side-2 branch
-- the same commit touched both branches, an unmirrored oversight. Fixed, but
**empirically had negligible effect** on the measured bias (re-ran the same 3 combos,
numbers barely moved) -- real bug, but not the dominant mechanism.

**Root mechanism found and CONCLUSIVELY PROVEN via direct experimentation:**
`MoveAndFight()`'s single-pass loop applies combat damage AND movement immediately,
in position-sorted iteration order. In a mutual-kill exchange within one tick,
whichever unit is processed first lands its hit before the other (now at <=0 health)
gets its own turn -- and iteration order correlates with side, since P1 units tend to
sit at higher X once advanced into enemy territory (P1 moves toward increasing X).
**Proof, not theory:** literally reversing the iteration order in a controlled test
flipped the entire bias -- White/nuke/wall went from P1 40%/P2 59% to P1 8%/P2 91%;
White/snipe/reinforcements went from P1 95%/P2 5% to P1 0%/P2 100%. This is airtight
evidence of the mechanism.

**Attempted the principled fix: true two-phase simultaneous resolution** (compute
every unit's intended damage from start-of-tick state in one pass, apply all of it in
a second pass, so no unit's fate this tick can depend on processing order) --
implemented for combat damage AND castle damage. **Result: the bias reduced somewhat
but did NOT converge to ~50/50** (still ~70-94% skewed, now toward P2 instead of P1).
Investigated further and found a SECOND instance of the same class of bug: **movement
updates (`unit.Position += ...`) were still applied immediately during the decide
phase**, so a unit processed earlier in a tick could move into range and be detected
by units processed later in the SAME tick, while the reverse could never happen --
proven the same way (flipping decision-order with damage already deferred still
flipped the outcome completely, e.g. White/nuke/wall swung from P2 82% back to P1
93% on order alone). **Extended the fix to defer movement too** (decide every unit's
new position from start-of-tick state, apply all positions simultaneously alongside
damage).

**This full fix introduced a serious NEW regression: games stopped resolving
decisively.** Draw/timeout rates exploded to 250+/300 games (vs. 2-61/300 before) --
armies appear to reach a stalemate rather than making contact reliably once movement
is deferred this way, plausibly some kind of leapfrog/oscillation effect at the
contact boundary that the original immediate-update model didn't have. **This is a
worse problem than the bias itself** (broken decisiveness affects every single game,
not just mirror-match fairness) and is not something to ship without real play-testing
and tuning time.

**Decision: reverted `GameEngine.cs` completely to its last-committed state** (`git
checkout`), confirmed a clean rebuild. Kept the `mirror-fixed` diagnostic mode in
`CastleDefense.BotArena` (pure test tooling, zero engine risk, valuable for whoever
continues this investigation) but shipped **no change to the actual combat engine**
this session -- the risk of a half-validated core-combat-resolution rewrite
outweighs leaving the known, already-well-characterized seat bias in place for now.

**Status for Marc: root cause is now fully understood and reproducible (order-
dependent resolution of both combat damage and movement in `MoveAndFight`), but the
correct fix needs more careful engineering and play-testing time than was available
in this session** -- likely a genuine architecture change (proper simultaneous-tick
semantics for movement specifically, without breaking the contact/engagement
dynamics that currently work), not a quick patch. Recommend treating this as its own
follow-up task with dedicated time, rather than rushing a fix under weekend time
pressure given how easily an incomplete attempt introduced a worse (stalemate) bug.
Given this, the training relaunch proceeds on the ORIGINAL engine (seat bias not yet
fixed) -- see the sequencing note below.

## Pause/resume for the training run (2026-07-26)

Marc's need: pause the run when he wants to use his PC, resume seamlessly later,
given a single run may now take 50+ hours.

**Verified `train_ai_cluster.py` already saves a genuinely resumable checkpoint, not
just an inference export** -- `model.save(TRAINING_MODEL_NAME)` writes SB3's full PPO
state (policy + optimizer weights, `num_timesteps`, `_n_updates`) into
`castle_defense_p1_v28.zip`, confirmed by inspecting the saved zip's `data` entry
directly (`num_timesteps: 382255104`, not 0). The existing "Resuming training:
{file}" load path does NOT reset `num_timesteps` (only the separate warm-start-from-
`v25_bc` path does that, intentionally) -- so a plain restart of the same script
already resumes correctly; the earlier "no resumable checkpoints survived" finding
was about `.zip` files being deleted by an unrelated cleanup incident, not a save/load
defect.

**Tightened the checkpoint cadence from every 10 PPO updates to every 3** (~35s of
at-risk progress at the measured throughput instead of ~2 min), since Marc may want
to pause somewhat spontaneously.

**Two scripts, both tested against the real live run, not just reasoned about:**
- **`pause_training.ps1`** -- stops training, the benchmark loop, and the sanity
  watchdog (via their PID files), then cleans up the processes a forceful kill can't
  take with it (the 14 arena children, the `plot_training.py --watch` subprocess
  chain) automatically. Reports the latest checkpoint's timestamp/size when done.
- **`resume_training.ps1`** -- relaunches all three as detached processes exactly as
  originally launched; no special "resume" flag needed since `train_ai_cluster.py`
  auto-detects `castle_defense_p1_v28.zip`. Skips relaunching anything already
  running (checks PID files first), so it's safe to run repeatedly.

**Validated with a real pause-resume-pause-resume cycle** (not just a design review):
resumed the actual in-progress v28 run (surviving from earlier in this session at
382,255,104 steps), confirmed via the saved zip's `data.num_timesteps` (not the
console log -- see the note below), ran it briefly (confirmed healthy: invests/game
already at 4.36 and climbing, matching a resumed model with real prior skill rather
than a fresh start), paused it (confirmed zero training/arena processes remained,
checkpoint advanced to 382,943,232 -- proving real progress was saved), then resumed
again cleanly.

**One display quirk worth knowing, not a functional bug:** the console/CSV progress
log's "steps" and "games" counts (from `ProgressTracker`, e.g. `training_progress.csv`)
are a script-local counter that always restarts at 0 on every launch -- they are NOT
the model's real persisted step count. After a resume, `training_progress.csv` will
show small numbers again even though the actual model (entropy schedule, the 2B-step
stop condition, everything that matters) is genuinely continuing from where it left
off. **To check true total progress after a resume, read the checkpoint directly**
(`python -c "import zipfile,json; print(json.load(zipfile.ZipFile('castle_defense_p1_v28.zip').open('data'))['num_timesteps'])"`)
rather than trusting the CSV's own step column across a pause boundary.

**Commands, for Marc directly:**
```
cd C:\repos\Castle-Defense-Game-2\CastleDefense.PythonAI
powershell -File pause_training.ps1     # pause -- safe to use the PC afterward
powershell -File resume_training.ps1    # resume exactly where it left off
```

## Sequencing note: relaunched on the ORIGINAL (seat-bias-unresolved) engine

Given the seat-bias fix wasn't safely shippable this session (see above), and Marc's
own stated need for pause/resume is independent of whether that fix lands, training
was resumed on the **original, unmodified engine** -- the same one this v28 run had
already been training on all along (no engine change was actually shipped for v28's
run; only `CastleDefense.BotArena`'s diagnostic tooling and `train_ai_cluster.py`'s
save-cadence were touched). This is a deliberate call, not an oversight: continuing
382M+ steps of real progress now, with real pause/resume in place, seemed better than
losing a weekend's momentum waiting on a properly-validated engine fix. The seat bias
remains a known, well-diagnosed, open issue (follow-up task spawned) -- whenever it
does get fixed, this run's results should be treated as measured on the biased
engine, same caveat as everything measured so far this whole campaign.

**What's fully autonomous vs. needs Marc's call:** steps 1, 2, 4, and 5 are fully
executable without him (mechanical: stop processes cleanly, run the existing
dashboard tooling, implement+validate two already-fully-specified changes, write it
up). Step 3's BRANCH POINT is where his judgment genuinely matters -- "is this good
enough to make it the new league anchor and commit real time to a second run" and
"should exploiter agents happen now or later" are calls about how to spend the REST
of the weekend, not just a mechanical next step, so that decision point will be
reported to him rather than assumed. If he's unreachable when the run finishes, the
default is: run the full evaluation (steps 1-2) and the two queued fixes (step 4)
regardless -- those are valuable either way -- and hold off on committing to a second
full training run or exploiter-agent work until he's confirmed the direction, since
those are the expensive, hours-long commitments a wrong guess would waste.

## Seat-bias fix RE-APPLIED, shipped, and v29 launched (2026-07-26)

Re-opened the seat-bias question after the previous session's revert (draw/timeout
explosion to 250+/300 in mirror-match testing). Re-diagnosed whether that explosion
was a general engine regression or an artifact specific to the artificial mirror-match
scenario -- re-applied the same two-phase fix (`MoveAndFight()` now computes every
unit's move/damage/castle-damage from a stable start-of-tick snapshot, applies moves,
then unit damage, then castle damage, then removes the dead -- no unit's fate this
tick can depend on iteration order anymore; also fixed `GetDistanceToEnemyCastle`'s
Side-2 branch, which was missing the `attacker.Width` term Side-1 already had) and
tested it against REAL, asymmetric matchups instead of just the synthetic mirror test:

- **Spam bots, all 8 tiers (50 games each, fresh rebuild):** 88-100% win rate, **zero
  draws/timeouts anywhere** -- fully healthy, matches/beats historical baselines.
- **Real model checkpoints v4/v7 vs HeuristicBot:** no regression from historical
  numbers.

This confirmed the stalemate explosion from the previous attempt was specific to
forcing bit-identical loadouts on both sides, not a general breakage. **Committed**
(`3b486ff3`) on that evidence.

**Full solution rebuild done, verified the fix is actually in the built binaries**
(`CastleDefense.Simulation.exe`/`CastleDefense.Engine.dll` timestamps both postdate
the source edit, despite MSB3026 file-lock retry warnings against the still-running
old-engine v28 arenas during the build -- the retries succeeded once those processes
were paused).

**Re-verified fresh, exact numbers post-commit before writing this up** (earlier
session numbers quoted informally before compaction turned out to be off -- these are
the real, reproduced ones):

**The number that actually matters (aggregate randomized mirror-match, `mirror` mode,
random team+loadout per side -- the closest proxy to real game variety, and the same
methodology the historical baseline was measured with):**

| | Historical (pre-fix) | Post-fix (n=300) |
|---|---|---|
| P1 win rate | 60.5-61.5% | **50.7%** |
| P2 win rate | 37.0-38.5% | **47.0%** |
| Draws | ~1.5% | 2.3% |
| Timeouts | 27.5-29.5% | **49.3%** |
| Avg length | 378s | 427s |

**The seat bias is genuinely fixed at the level Marc asked for** -- P1/P2 are now
~51/47, within noise of 50/50, down from a consistent ~60/38 skew across multiple
historical measurements. This is the real deliverable.

**But there's a real, quantified cost: the timeout rate nearly doubled (29%->49%) and
average game length grew (378s->427s).** Games generally stall out to the 10-minute
limit more often now that the old order-dependent tie-break (which used to force SOME
decisive outcome almost by accident) is gone. This is a genuine tradeoff, not a free
fix -- flagging it plainly rather than only reporting the headline win-rate number.

**`mirror-fixed` (identical loadout forced on both sides -- an artificial, narrower
diagnostic, not representative of real games where players choose independently)
confirms the mechanism, at its most extreme:**
- White/nuke/wall, n=100: **100% P2, 0 draws** -- fully deterministic (identical
  avg length to the decimal across reruns; HeuristicBot's decision logic has no RNG,
  so two bit-identical strategies produce a bit-identical outcome every time).
- White/snipe/reinforcements, n=100: **100% draws/timeouts** -- also fully
  deterministic.

**Answering Marc's standing question -- is this (a) the same seat-bias mechanism,
(b) a separate gadget bug, or (c) legitimate strategy asymmetry? Evidence says (a) AND
(c), not (b):**
- Same mechanism: both combos were shown (in the prior session's reversed-iteration-
  order experiment) to flip completely under the exact same code path. There's no
  sign of a gadget-specific interaction bug -- nothing about snipe or reinforcements'
  own targeting logic changed; the same `MoveAndFight` fix drives both outcomes.
- What differs is strategy shape, and that part IS legitimate: nuke/wall is
  aggressive/damage-forcing, so even a residual deterministic tie-break still
  produces a clean decisive winner (just now P2 instead of P1). Snipe/reinforcements
  is purely reactive on both slots (snipe picks off approaching units one at a time;
  reinforcements sustains the defense) -- two bit-identical instances of a purely
  defensive strategy have no mechanism to ever separate, so the game runs out the
  clock. Real games never actually have two players making bit-for-bit identical
  decisions forever, so this specific extreme is a diagnostic artifact of the forced-
  identical test, not something that will occur in real play.
- **The caveat that keeps this from being a clean "not a bug, don't worry" verdict:**
  the AGGREGATE randomized mirror-match's timeout rate nearly doubling shows the
  stalling effect isn't confined to one contrived combo -- it's a broad, measurable
  shift across varied loadouts. Worth continuing to watch, not dismissed.

**Decision: shipped anyway.** Real, varied asymmetric gameplay (spam bots, real
models) is fully healthy and decisive with zero new stalemates. The timeout increase
is real but concentrated in symmetric/near-mirror situations; ~50% of the training
pool (Heuristic, League, Spam, AntiSpam, RandomDummy) is asymmetric and unaffected.
The remaining ~50% is Self-Play, which is the one place this could actually matter for
training -- but Self-Play pits a stochastic PPO policy against itself (independent
action sampling, not the bit-identical deterministic bot used in this diagnostic), so
it's a much weaker version of the "identical strategy" scenario that produces the
mirror-fixed extremes. Early evidence from the live v29 run (below) shows Self-Play
episodes completing normally, not hanging.

**Known gap, not fixed this session: the training pipeline has NO visibility into
timeout rate at all.** `CastleDefense.Simulation/Program.cs` (line ~277) already
silently resolves every `IsTimeLimit` game to a synthetic winner (by absolute castle
HP) before it's ever reported to Python -- `train_ai_cluster.py`'s win-rate stats
can't distinguish a decisive win from a timeout-win, and there's no timeout-rate
column in `training_progress.csv` at all. Given the quantified timeout-rate increase
above, this is worth instrumenting if Self-Play's timeout rate needs to be watched
closely as training progresses -- flagging for Marc's judgment rather than adding
new instrumentation unprompted under time pressure.

**Model naming for the relaunch:** per the original sequencing ("relaunch fresh,
warm-started from v25_bc, on the corrected engine"), this is a NEW model,
`castle_defense_p1_v29`, not a continuation of `v28.zip` (which was trained 382M+
steps entirely on the biased engine and shouldn't be treated as a valid starting
point for measuring the corrected engine's results). `train_ai_cluster.py`'s
`TRAINING_MODEL_NAME` updated accordingly; `TRAINING_BASE_MODEL` stays
`castle_defense_p1_v25_bc` (unchanged warm-start source). `pause_training.ps1` /
`resume_training.ps1` / `benchmark_checkpoints.ps1` updated to reference v29.
v28's progress logs archived (`training_progress_ARCHIVE_v28.csv`,
`training_progress_opponents_ARCHIVE_v28.csv`, `checkpoint_benchmark_log_v28_ARCHIVE.csv`)
so v29's fresh logs don't get appended onto v28's data (log files are append-mode).

**v29 launched, confirmed healthy at startup:** all 14 arenas connected, no
`v29.zip` found so it warm-started fresh from `v25_bc` as intended. First logged
checkpoint: 229,376 steps / 216 games, **Self-Play already at 90/216 games (41.7%,
tracking toward its 50% pool weight)**, invests/game 2.51 (healthy start, in line with
the documented v28-launch baseline). Sanity watchdog (PID 57600) running its 5-minute
fast-phase check now. Training PID 64320, benchmark loop PID 22908.

**Commands unchanged, now targeting v29:**
```
cd C:\repos\Castle-Defense-Game-2\CastleDefense.PythonAI
powershell -File pause_training.ps1     # pause -- safe to use the PC afterward
powershell -File resume_training.ps1    # resume exactly where it left off
```

## v29 progress check at ~10 hours / 344M steps (2026-07-26 12:26 EDT): the self-play divergence is BACK, seat-bias fix did not touch it

Marc explicitly declined the self-play-timeout-rate instrumentation flagged in the
previous entry ("game-design choice he'll handle later") -- not building it.

**Headline finding: the exact "self-play winrate up / everything else down" pattern
Marc originally flagged is happening again, on the corrected (seat-bias-fixed)
engine.** This answers his standing question directly: **removing the seat bias did
NOT fix the divergence.** Whatever mechanism drives it is independent of the engine
bug -- almost certainly the self-play weighting/mechanism itself (classic self-play
collapse: the policy specializes against a moving target nearly identical to itself,
which produces real wins there but doesn't transfer, and may be actively displacing
general skill via something like catastrophic forgetting).

**Evidence (win rate vs fixed opponents, sampled at ~14M/~84M/~169M/~254M/~344M steps):**

| Opponent | ~14M | ~84M | ~169M | ~254M | ~344M (latest) |
|---|---|---|---|---|---|
| Self-Play | ~55% | ~52%* | ~84% | ~80% | **83.8%** |
| Heuristic Bot | ~15% | ~13% | ~9% | ~3% | **4.8%** |
| v4 (league) | 22.6% | 29.9% | 22.6% | 9.2% | **10.2%** |
| v7 (league) | 31.0% | 29.4% | 21.6% | 8.6% | **7.6%** |
| v25 (league) | 52.1% | 49.3% | 45.4% | 28.4% | **34.2%** |
| Spam T4 | 71.2% | 62.7% | 48.4% | 41.8% | **42.8%** |
| Spam T7 | 82.1% | 85.1% | 81.0% | 46.0% | **33.4%** |
| Anti-Spam | 31.7% | 37.6% | 20.2% | 22.2% | **26.2%** |
| Random Dummy | 65.2% | 70.0% | 53.6% | 50.0% | **57.4%** |

*Self-Play stayed flat/noisy through ~90M steps, then broke upward sharply right
around the point everything else started its steepest decline (~100-110M steps).

**Every non-self-play opponent declined, including trivial ones.** Random Dummy
sitting at only 57% is the starkest tell -- a genuinely-improving policy should
crush a literal random-action bot at 90%+, not hover near a coin flip. The
checkpoint-vs-HeuristicBot benchmark log (below) shows the same shape independently.

**Checkpoint-vs-HeuristicBot benchmark, full series (model_winrate_approx_pct):**
19.3, 26, 29.3, 26.7, 22, 22, 18.7, 22.7, 16.7, 18, 17.3, 11.3, 11.3, 10, 18, 15.3,
12, 15.3, 16, 12.7, 10.7, 10.7 -- rose for the first ~2-3 readings (~30-46M steps,
peak 29.3%), then a sustained decline to a 10-18% floor for the remaining ~290M
steps. Same shape as the per-opponent table above, measured independently.

**Invests/game: genuinely healthy, no complaint here.** 1.3 (~14M) -> 1.0 (~50M) ->
1.6 (~100M) -> 9.6 (~170M) -> 7.9 (~254M) -> **9.0 (latest, ~344M)** -- climbed into
and now sits comfortably within/above the previously-verified 4.5-11 healthy
reference range.

**Sanity watchdog: PASSED, but the pass is stale and wouldn't have caught this.**
It's a one-shot script -- fast check passed at 02:26 (Self-Play sample count=500,
invests/game=0.68, correctly confirming the model wasn't null), slow check finished
at 03:21 after only 2 benchmark readings (19.3%->26%, still in the rising phase) and
then exited. The decline only became visible in later readings the watchdog was never
going to see again. Worth remembering for future runs: this watchdog catches the v27-
style "totally broken" failure mode, not a slower divergence that only shows up
hours in.

**Throughput:** ~344.3M steps / ~451,400 games over ~10h5m elapsed -> **~9,500
steps/sec, ~746 games/min** -- notably below the previously-measured ~25,000-28,000
steps/sec / ~1,700-2,000 games/min baseline for this architecture. Not investigated
yet (competing CPU load from this session's own mirror-match/spam BotArena testing
runs earlier is a plausible partial explanation, not confirmed). At this rate,
reaching the 2B-step target would take **~48-49 more hours** from now.

**Process health: all clean.** 14 arenas + 4 python worker processes + benchmark-loop
powershell process running continuously since launch (02:20:58), zero errors or
exceptions in `campaign_run.err.log` across the full ~10 hours (just the one known
harmless ONNX-export deprecation warning).

**Not yet decided: what to do about the divergence.** This is the same shape Marc
flagged before the seat-bias detour, now confirmed to be a separate, real, and
apparently worse-than-before mechanism (Heuristic win rate down to ~5%, previously
never seen that low). Likely candidate: the 50%-Self-Play pool weighting is too
aggressive relative to how much a moving-target opponent can teach without also
eroding general skill. Reporting for Marc's call rather than changing the opponent
mix unprompted -- this is exactly the kind of pool-composition decision he should
make, not one to guess at autonomously.

## Root cause of the self-play divergence FOUND (2026-07-26): the trainee's forced invest-exploration nudge only applies to one side of self-play

Marc's context correction first: "Random Dummy" actually plays like a low-tier spam
bot in practice (it only ever successfully executes cheap unit spawns, since it
tries a literal random action every tick and most others are illegal/unaffordable
most of the time) -- it is NOT a trivial do-nothing baseline. This matters a lot for
interpreting the divergence table in the previous entry, see below.

**Paused v29 via `pause_training.ps1`** (clean, 0 processes remained) to free CPU for
controlled experiments, per Marc's instruction. Built three new diagnostic modes in
`CastleDefense.BotArena` (`model-diag`, `model-vs-model`, `selfplay-sim`) plus a
`GetRawLogits` method on `AIBrain` (exposes pre-argmax logits so real-game entropy can
be measured directly, not just the training script's own noise-based health check).

**Finding 1 -- the policy has NEVER voluntarily invested, at any point measured,
including before any v29 RL training happened.** `model-diag` runs a model through
~150 real games (diverse opponent pool, pure argmax, zero forced exploration) and
tallies its actual chosen actions:

| Checkpoint | Real invests/game | Action mix (non-wait) |
|---|---|---|
| `v25_bc` (0 steps of v29 RL) | **0.00** | 98.5% spawnT1, 1.5% spawnT2, nothing else |
| v29 @ 261M steps | **0.00** | 97.3% spawnT1, rest negligible |
| v29 @ 397M steps (latest) | **0.00** | 95.5% spawnT1, rest negligible |

Cross-checked against the existing, already-validated `invest-stats` tool (not just
the new one) -- same result, 0.00 invests/game for `v25_bc` vs HeuristicBot. **The
"invests/game climbing to ~9, healthy, within the 4.5-11 reference range" I reported
last update was wrong** -- that metric is computed from `training_progress.csv`,
which counts P1's action AFTER `CastleDefense.Simulation`'s `INVEST_EXPLORE` forces
investment ~5% of legal opportunities. The model's real, unforced policy has
converged to (and never left) a pure tier-1-spam strategy with no economy at all.
Real entropy per decision (masked softmax over legal actions, measured on real game
states, not synthetic noise): ~0.35-0.36 nats at both 261M and 397M steps -- low
(fairly peaked) but not collapsing further; what IS still climbing is raw logit
*magnitude* (the training script's own `range=` diagnostic: ~10-19 in the first
70M steps -> 500+ now) -- the model is getting more confident in the SAME narrow
strategy, not discovering a narrower one.

**Finding 2 -- the model is NOT getting worse in absolute skill. It is real,
measurable, and better than its own ancestor at head-to-head play.** Direct
`model-vs-model` matches (no forcing either side):
- `v25_bc` vs v29@397M: **v29 wins 84.0%** (21/150 vs 126/150, 3 draws).
- v29@261M vs v29@397M: **v29@397M wins 57.3%** (64/150 vs 86/150).

So RL training has genuinely improved execution of the tier-1-rush strategy over
time -- better micro/targeting/timing within the same never-invest framework. This
rules out "the policy is rotting/forgetting" as the mechanism. It has gotten better
at the one thing it does, which just isn't enough against an opponent that scales.

**Finding 3 -- re-baselining Random Dummy per Marc's correction: the ~57% reading is
NOT alarming.** Since Random Dummy behaves like a low-tier spam bot in practice, and
v29's real policy IS a tier-1-spam bot (Finding 1), a v29-vs-RandomDummy game is
functionally rush-vs-rush -- a close, noisy matchup is the EXPECTED shape, not a red
flag on its own. The alarming reads are the ones against opponents that actually
scale an economy (HeuristicBot ~4-5 invests/game, real league checkpoints v4/v7/v25)
-- those declines are real and this investigation explains why (below).

**Finding 4 -- THE mechanism: self-play's win rate is overwhelmingly a measurement
artifact of the forced-exploration asymmetry, not genuine skill.** Confirmed the
self-play sampling mechanism first: `CastleDefense.Simulation/Program.cs` uses the
literal same `trainingBrain` object for both P1 and the self-play P2 (not a stale
snapshot -- always perfectly in sync, reloaded together whenever Python acks a new
version). A clean `model-vs-model` mirror match of v29@397M against itself (no
forcing either side) gives a normal, unremarkable **58.0%/42.0%** split (in line with
ordinary team/loadout-draw noise, similar magnitude to the post-seat-bias-fix
HeuristicBot mirror baseline).

**But real self-play training games are NOT this clean mirror match.**
`INVEST_EXPLORE` (5% forced invest when legal) applies ONLY to P1's action --
never to the self-play opponent's action, even though it's the identical brain. Built
`selfplay-sim` to reproduce exactly this one asymmetry (side A gets the 5% nudge,
side B doesn't, otherwise identical model) and it turns the clean 58/42 mirror into
**92.7%/7.3%** -- MORE extreme than the 83.8% actually tracked in live training, and
side A's invests/game (4.54, entirely from forced actions) lands right in the
"healthy-looking" range that was mistakenly read as real economic learning.

**Why this specific asymmetry produces wildly different effects depending on
opponent:** against HeuristicBot (which already invests ~4-5x/game on its own), P1's
occasional forced invest barely moves the needle -- Heuristic still wins ~85-95% of
these games, which is the model's real, ungenerous skill level against a true
scaling opponent. Against a self-play copy (which, per Finding 1, would ALSO never
invest without forcing), that same 5% forced nudge is the ONLY economy either side
ever sees -- so P1 gets a total, uncontested, mechanical economic edge that has
nothing to do with strategic skill. Since self-play is 50% of every training batch,
this artifact dominates half of the model's entire training signal, teaching the
optimizer "the current strategy is winning big" -- which is entrenching (not
correcting) the never-invest tier-1-rush strategy, evidenced by the logit-range
climb in Finding 1. Meanwhile that same strategy is genuinely, measurably losing
ground against every opponent that actually scales (HeuristicBot, v4, v7, v25 --
see the previous entry's decline table), because it was never a complete strategy
against those in the first place.

**Full causal chain, evidence-backed end to end:** BC pretraining produced (or RL
never corrected) a policy that structurally never invests -> self-play's asymmetric
forced-exploration nudge fabricates an artificial ~90%+ "win rate" for that exact
non-strategy against itself -> since self-play is half of training, this false
positive signal dominates the reward landscape and pushes the policy to become MORE
confident in the never-invest rush (rising logit range) -> the same strategy
genuinely deteriorates against every opponent that scales its own economy, because
scaling opponents were never actually beaten by pure rush, and the model has no
countervailing signal telling it so (since the one place its "wins" outnumber its
losses -- self-play -- is a mechanical artifact, not real feedback).

**Not yet fixed or acted on, per Marc's explicit instruction to validate the cause
first.** Reporting the mechanism for his decision. The two most obvious candidate
levers (not implemented): (a) stop applying `INVEST_EXPLORE` asymmetrically -- either
apply it to the self-play opponent too, or don't apply it during self-play at all;
(b) address the underlying never-invest degeneracy more directly (e.g. an explicit
reward term or exploration schedule targeted at investment specifically, since 5%
forced-action exploration alone clearly isn't teaching the model to WANT to invest
after 400M+ steps). Both are real design decisions, not mechanical fixes -- left for
Marc.

**Pause/resume validated cleanly as part of this investigation, per Marc's request.**
`pause_training.ps1` stopped everything with zero remaining processes. After the
diagnostics above, `resume_training.ps1` correctly found and resumed from
`castle_defense_p1_v29.zip` ("Resuming training: castle_defense_p1_v29.zip", not a
fresh warm-start), all 14 arenas reconnected cleanly. One pre-existing quirk
reconfirmed (already documented in the pause/resume entry above): `training_progress.csv`
/ `training_progress_opponents.csv` get cleared and restart their own step/game
counters at 0 on every resume -- this is a script-local counter, not the model's real
`num_timesteps` (which the checkpoint zip preserves correctly). Not a new issue,
just re-verified under real use.

## Why won't the model learn to invest? Deep dive on RL learnability (2026-07-26)

Marc's framing: investing is, in his experience, always a dominant strategy, and he's
seen 90%-forced-invest-exploration produce strong models before -- but that "doesn't
teach the model anything, it's just artificially improving performance." He asked for
a real investigation into why a reward signal this strong isn't cutting through the
noise, not another pool-rebalance guess. v29 stayed paused; used the freed CPU for
this.

**Read the full reward function (`GameEngine.CalculateReward`) and the live
GA-tuned `reward_params_5000.json`** (not just the class defaults):
`WinReward=35164, InvestReward=2186, InvestDecay=930, AntiSpend=1113,
SavingsWeight=0.085, CombatScale=0.509, GadgetUpgrade=1566, GadgetUse=28`.

**Ruled out: the direct reward for investing is NOT weak, delayed, or penalized.**
`if (myPlayer.Income - myPrevIncome > 0) reward += InvestReward + (11 - InvestmentCount) * InvestDecay`
fires the SAME tick `Invest()` succeeds (`ApplyInvestmentStep()` updates Income
synchronously, no delay). For the first investment this is `(2186 + 930*10)/1000 ≈
11.49` -- roughly **1000x the -0.011/tick time penalty** and far larger than a single
kill (`6*0.509/1000 ≈ 0.003`). The `AntiSpend` penalty explicitly excludes the invest
tick itself (`myPrevIncome == myPlayer.Income` in its condition means it only
penalizes spending on OTHER things while saving up -- it's pro-investment, not
anti-). This is not a subtle or sparse signal; it's one of the single largest reward
events in the whole function, and it doesn't need to wait for the eventual win.

**Ruled out: discount horizon.** `gamma=0.9998` → effective horizon `1/(1-gamma) =
5000` RL-steps. Each RL-step is 9 engine ticks (confirmed in
`CastleDefense.Simulation/Program.cs`'s per-decision loop), and `MAX_TICKS=18,000`,
so the LONGEST possible game is `18000/9 = 2000` RL-steps -- the discount horizon is
2.5x the absolute maximum game length, and ~5-10x a typical game (~500-900 steps
measured). There is no horizon problem: even pure sparse win/loss credit would
propagate back across an entire game with room to spare. (This also means the
direct invest reward above doesn't even need to rely on this -- it's immediate
regardless.)

**A real but secondary wrinkle: the N-step board-eval shaping term is close to a
wash for the FIRST investment specifically.** `compute_nstep_shaping` looks
`BOARD_SHAPING_LOOKAHEAD=30` steps (~9 seconds) ahead at `EvaluateBoard()`
(weight ~21.3-21.7, barely annealed across the whole run) and adds `eval[t+30] -
eval[t]` to that step's reward. `EvaluateBoard()`'s income term (weight 0.7, the
LARGEST of six components) rises immediately and permanently on invest; the money
term (weight 0.2406) drops sharply (money spent). Back-of-envelope with realistic
first-invest numbers (income 2→~2.6, money ~18→~0): income-score contributes
roughly **+0.039** (weighted), money-score roughly **-0.046** (weighted) -- net
slightly negative for investment #1 specifically, before the compounding benefit of
later, larger income jumps would plausibly flip this positive. Not the main effect
(dwarfed by the direct `InvestReward`), but worth knowing this shaping term doesn't
help early investing and may even add a small amount of noise against it.

**Ruled out: observability of own economy.** `GetStateVector` includes
`log10(Money)`, `log10(Income)`, and `log10(InvestmentPrice)` for the player's own
side -- the model can clearly perceive its own economic state. (Correction to
CLAUDE.md, which is stale here: it claims `InvestmentPrice` is NOT in the state
vector and `InvestmentCount` is -- current code is the reverse: `InvestmentPrice`
(log10) replaced raw `InvestmentCount` in a past commit, on the reasoning that count
is derivable from `InvestmentPrice` + `Income`. Worth a docs fix separately.)

**A real, structural observability gap, flagged but not the primary mechanism: the
enemy's economy is deliberately hidden** (`GetStateVector`'s own comment: "we hide
the enemy's money/income so the AI doesn't cheat"). The model can never directly
perceive that an opponent is out-investing it -- only infer it indirectly from
battlefield pressure over time. This makes "invest because my opponent is scaling"
structurally harder to discover from observation alone, though it's a symmetric,
long-standing design choice, not something that changed. Noted as a contributing
consideration for candidate fixes (a reward-shaping term could use full-state
economic comparison even though the observation can't, since reward computation has
engine-level access regardless of what's shown to the policy).

**THE decisive finding: PPO's own clipped-objective mechanics cannot learn a
correctly-rewarded but near-zero-probability action from injected exploration
samples, and this is measurably getting WORSE, not better, over training.** Extended
`AIBrain`/`model-diag` to compute the real softmax **P(invest) whenever it's legal**,
across real games, zero forced exploration:

| Checkpoint | P(invest \| legal) geometric mean | min | max |
|---|---|---|---|
| v29 @ 261M steps | 5.5 × 10⁻⁵² | 2.0×10⁻⁵⁷ | 1.4×10⁻¹⁷ |
| v29 @ 397M steps (latest) | **9.2 × 10⁻¹⁴³** | 5.2×10⁻¹⁵¹ | 4.0×10⁻³³ |

(`v25_bc` never had a single legal-invest decision sampled in 100 games -- its money
apparently never crosses the $18 threshold before games end, itself more evidence of
how thoroughly non-economic its play is.)

**This is not "small," it's numerically unreachable by ordinary gradient steps.**
PPO's surrogate objective clips the importance ratio `π_new(a)/π_old(a)` to
`[1-0.2, 1+0.2]` (MaskablePPO's default `clip_range`, unchanged in this config) --
regardless of how large the advantage is, a single update can only move an action's
probability by a bounded RELATIVE amount once the ratio leaves that band. Getting
from `~1e-143` to anywhere relevant via repeated ~20%-per-update increases would need
on the order of **1,800 consecutive positive updates** (`log(1e143)/log(1.2)`) --
and critically, the measured trend across 261M→397M steps moved the WRONG way (91
more orders of magnitude AWAY from relevance), meaning whatever pull the rare
forced-invest-and-win samples exert is being overwhelmed, not just outpaced.

**Why the pull is being overwhelmed: this compounds directly with the self-play
finding from the previous entry.** Self-play is 50% of every batch and (per that
investigation) fabricates an ~90%+ "win rate" for the current never-invest strategy
via the P1-only forced-invest asymmetry. Raising the probability of an
ALREADY-common action (spawnT1, wait) via a modest gradient nudge stays comfortably
inside the clip band (its `π_old` is already large, so a small absolute change is a
small ratio change) and applies with full force every update. Raising a
near-zero-probability action's probability by the same absolute amount is a
proportionally enormous ratio, and gets clipped hard. So the two forces are not
symmetric: the (real, artifact-driven) pressure entrenching "don't invest" moves
freely; the (real, correctly-rewarded) pressure toward "invest" is structurally
throttled by the same mechanism, every single update, and the throttle gets tighter
the smaller P(invest) already is. This is a self-reinforcing trap, not a one-time
setback.

**This also fully explains Marc's own historical observation about 90%-forced
exploration.** At 90% forcing, nearly every legal opportunity IS an investment
regardless of what the policy wants -- so the environment behaves as if scripted to
invest, and the model learns good downstream PLAY given a scaled economy (unit
composition/timing, since the trajectory itself is realistic and coherent) without
the policy's own `P(invest)` ever needing to rise, because the environment supplies
the choice regardless of the policy's preference. Strong resulting BEHAVIOR, but the
POLICY never actually attached probability/credit to choosing it -- exactly Marc's
own diagnosis ("artificially improving performance," not teaching). At 5% scattered
forcing, the reverse problem: legal invest moments occur, but the same PPO-clip
mechanic prevents any single success from moving the needle much, and coherent
multi-invest trajectories (which would need several successive forced rolls to
survive the model's own competing unit-spending, at ever-increasing cost) are rare
enough that the model may functionally never experience the FULL winning economic
trajectory end to end -- consistent with Marc's own suspicion in angle 2.

**Secondary contributing factor: the entropy bonus doesn't protect specific rare
actions.** `ent_coef` (0.02-0.03, annealing early in a run, at floor by 400M steps)
regularizes TOTAL categorical entropy across all 14 actions. A action's own
contribution to that total (`p*log(1/p)`) vanishes as `p→0`, so the entropy bonus
provides essentially no resistance to one specific action's probability collapsing
toward zero as long as entropy is "spent" elsewhere (here: spread across wait vs.
spawnT1 vs. occasional T2/T3/repair/gadget, matching the measured ~0.35-0.42 nat
entropy that's roughly flat across the run even as invest's probability craters
another ~90 orders of magnitude). A scalar, whole-distribution entropy bonus is not
well-suited to preventing this specific failure mode.

**Full synthesis:** this is not a reward-design problem (the direct invest reward is
large, immediate, and correctly signed) and not a discount-horizon problem (ample
margin). It is a policy-gradient MECHANICS problem: PPO's clipped surrogate
objective is structurally bad at learning from off-policy-injected samples of an
action whose current probability is extremely low, and the self-play forced-invest
asymmetry (previous entry) has been actively driving that probability toward zero
for the entire run, faster than the rare correctly-rewarded forced samples can pull
it back given the same clip constraint works against them specifically. The two
findings are one connected story, not two separate bugs.

**Candidate fixes identified, NONE implemented -- for Marc's decision, per his
explicit instruction that careful analysis-before-action is now the default:**
1. Fix the self-play forced-invest asymmetry (apply `INVEST_EXPLORE` to both
   self-play sides, or disable it for self-play specifically) -- removes the
   confounding push, doesn't by itself fix the clip-bottleneck for recovering
   from `~1e-143`.
2. Replace scattered 5% per-tick forcing with deliberately COHERENT invest-heavy
   episodes (force a real multi-investment trajectory through to completion in a
   fraction of episodes, rather than independent per-tick rolls) -- lets the model
   experience the actual winning trajectory end to end, directly targeting Marc's
   own angle-2 suspicion.
3. A targeted intervention on the invest action's logit specifically (e.g. a floor
   or periodic reset) so forced samples aren't starting from a numerically
   unrecoverable probability -- more invasive, needs care.
4. Loosen `clip_range` (or exempt forced-exploration samples from clipping
   entirely, since they aren't genuinely on-policy samples anyway) so rare-but-good
   actions can move faster.
5. A potential-based reward term using the ENGINE's full-state economic comparison
   (both sides' real income/investment, even though the observation hides the
   enemy's) -- reward computation isn't limited by what the policy can see.
6. An action-specific (not whole-distribution) minimum-probability floor or
   auxiliary loss, rather than relying on the scalar entropy bonus.

Not acted on. Reporting the mechanism and options; v29 remains paused pending Marc's
call on which lever(s) to pull.

## Invest-collapse fixes implemented and validated (2026-07-26)

Marc approved the fixes conceptually and asked for implementation specifics worked
out carefully, then a fast/cheap test BEFORE committing to a long run, plus a
reasoned resume-vs-restart recommendation. v29 stayed paused throughout; two short
(15M-step) controlled tests used the freed CPU instead.

**Implemented in `CastleDefense.Simulation/Program.cs` (rebuilt, committed):**
1. **Self-play asymmetry fix.** The self-play opponent copy (P2, literally the same
   `trainingBrain` weights as P1) now gets the identical invest-exploration forcing
   P1 gets. Every OTHER opponent type (Heuristic/spam/league) is left untouched --
   that asymmetry is the valid experiment ("does investing beat a fixed external
   strategy") we want to keep, it's only a confound when the "opponent" is a mirror
   of the trainee itself.
2. **Coherent invest-curriculum episodes**, replacing the flat scattered 5% roll.
   15% of episodes are flagged at episode start and force ~90% of legal invest
   opportunities for their entire duration (both sides, if self-play) -- so those
   episodes play out a real, complete high-investment game end to end, not just an
   isolated forced tick. The remaining 85% of episodes keep a small residual 2%
   baseline rate.

**Deliberately NOT implemented this round:** exempting forced samples from PPO's
clip, and an action-specific probability floor. Both require overriding
`MaskablePPO.train()` (which this pipeline calls directly, unmodified) -- real
surgery on SB3-contrib internals, more invasive and slower to get right than the two
above. Held in reserve if the simpler fixes hadn't tested out; they did, so not
needed yet (see results below).

**Test harness:** a standalone copy (`test_invest_fix.py`, not part of the real
campaign) with env-var overrides for model name/base/ONNX path/step budget, so two
independent 15M-step tests could run without touching `train_ai_cluster.py` or any
real checkpoint. `castle_defense_p1_v29.zip` was never touched directly -- a protected
copy (`castle_defense_p1_v29_testresume.zip`) was used for the resume test.

**Test A -- fresh warm-start from `v25_bc`, 15M steps, new C# fixes in place:**
Real (unforced) P(invest) via `model-diag`, zero forced exploration:
**geometric mean 0.2765** (up from a starting point of essentially 0, since `v25_bc`
never had a single legal-invest decision sampled in earlier testing). Concretely,
the model chose to invest voluntarily in **61 of 109** decisions where it was legal
(56%) across 150 real games. Win rate vs the mixed pool: 62.7%, healthy. **The fix
works, decisively, from a healthy starting point.**

**Test B -- resumed from the collapsed `v29.zip` (P(invest) ~9.2e-143), 15M more
steps, the SAME new C# fixes:** Real P(invest): **geometric mean 1.44e-136** --
statistically indistinguishable from where it started (7 orders of magnitude of
technical movement, but both numbers are equally "never" in practical terms). **Zero
voluntary invests observed** across 19,319 legal-invest decisions in 150 games (vs.
61 for Test A). Win rate 59.3%, entropy lower (0.258 vs 0.377 nats) -- consistent
with a policy that is simply too entrenched for a 15M-step burst (or plausibly much
longer) to meaningfully dislodge. **The fix does NOT rescue the collapsed
checkpoint** in a comparable budget.

**This directly answers Marc's practical question, with real evidence rather than
just reasoning:**
- **Yes, the fixes apply to a resumed run** -- they live entirely in the C# arena
  binary and the (unmodified) Python training script's behavior, not in the
  checkpoint's weights. Test B proves this: it used the identical fixed binary and
  picked up the new curriculum/self-play logic exactly like Test A did.
- **But applying correctly is not the same as recovering successfully.** The v29
  checkpoint's entrenchment (the same one-way-ratchet mechanism documented in the
  previous entry: raising a near-zero action's probability is clip-bottlenecked,
  while the forces that suppressed it were not) is severe enough that the identical
  fix, run for the identical number of steps, produces a working policy from a
  healthy base and produces no measurable change from a collapsed one.

**Recommendation: RESTART fresh from `v25_bc` with the fixes in place, not resume
`castle_defense_p1_v29.zip`.** Reasoning: (1) empirically validated above -- the
exact same intervention that cleanly works from `v25_bc` measurably fails to move
the collapsed checkpoint in a like-for-like test; (2) v29's 400M+ steps of "progress"
are entirely progress at executing a narrow tier-1-rush strategy we are specifically
trying to move away from (confirmed in the prior entry: v29@397M beats v25_bc 84%
head-to-head, but at that same never-invest strategy) -- there is no real
economic-play progress in that checkpoint worth preserving; (3) `v25_bc` is already
an independently strong base (beat HeuristicBot in earlier campaign testing, was the
single strongest model in the full league ranking before this campaign began).
Continuing from the collapsed checkpoint isn't free even if it eventually did
recover -- it would need to first unlearn ~140 orders of magnitude of entrenched
confidence against a mechanism this investigation shows actively resists that
recovery, before any of the curriculum's benefit could show through.

**Not yet done: launching the real long run.** Cleanup still needed (test model
files, league_models copies, killing the `plot_training.py` watcher leftovers each
short test spawned) and the new model naming decision (a fresh name, e.g. `v30`, to
keep this clean break distinct from `v29`'s collapsed history, similar to why `v29`
wasn't just a continuation of `v28`). Reporting test results and the restart
recommendation to Marc before proceeding, per his explicit instruction.

## v30 launched: fresh restart from v25_bc with both validated invest fixes (2026-07-26)

Marc approved the restart-over-resume recommendation and gave the go-ahead
autonomously. Launched `castle_defense_p1_v30` per the plan.

**Setup:**
- `train_ai_cluster.py`: `TRAINING_MODEL_NAME` → `castle_defense_p1_v30`,
  `TRAINING_BASE_MODEL` unchanged (`castle_defense_p1_v25_bc`), full 2B-step budget
  unchanged. Both invest fixes (self-play forcing symmetry, 15%-of-episodes
  full-invest curriculum) are already in the committed `CastleDefense.Simulation`
  binary from the earlier validation work -- no additional code changes needed here.
- `pause_training.ps1` / `resume_training.ps1` / `benchmark_checkpoints.ps1` updated
  to target v30.
- v29's progress logs archived (`training_progress_ARCHIVE_v29.csv`,
  `training_progress_opponents_ARCHIVE_v29.csv`, `checkpoint_benchmark_log_v29_ARCHIVE.csv`)
  so v30 starts with clean logs.
- Full solution rebuilt (`CastleDefenseGame2.sln`) to make sure `CastleDefense.Simulation`
  and `CastleDefense.BotArena` both reflect the latest committed code.
- **`benchmark_checkpoints.ps1` extended**: every cycle now also runs `model-diag` on
  the snapshot (100 games, diverse pool, zero forced exploration) and logs
  `invest_p_geomean`, `invest_legal_decisions`, `invest_chosen_pct` alongside the
  existing HeuristicBot win-rate columns. **This is now the key metric to watch** --
  the old `avg_invests_per_game` column in `training_progress.csv` is contaminated by
  forced-exploration actions and is exactly what misled the last two sessions into
  thinking investing was healthy. Trust `checkpoint_benchmark_log.csv`'s new columns
  over that one going forward.
- `sanity_watchdog.py` now also logs Self-Play's win rate at the fast-check mark
  (informational, not a hard gate -- too few samples that early to be reliable) so a
  recurrence of the asymmetry is visible immediately rather than only discovered
  hours in.

**Launched via `resume_training.ps1`** (correctly detected no `castle_defense_p1_v30.zip`
existed, warm-started fresh from `v25_bc` as intended). All 14 arenas connected
cleanly.

**Early health checks, all foreground-polled directly (not left to wakeups),
at ~5-6M steps:**
- **Sanity watchdog: PASS.** Self-Play sample count saturated (500), invests/game
  (contaminated metric) 1.09 -- confirms the model is genuinely driving its own
  actions, no v27-style null-brain recurrence.
- **Self-Play win rate holding at 56.6-58.8%** across 8 consecutive 500-game rolling
  readings (4.6M-5.4M steps) -- right in the healthy ~50-58% range a fair mirror
  match should show (matches the clean control test from the earlier investigation),
  NOT climbing toward the ~80-90% the old one-sided forcing produced. **The self-play
  symmetry fix is holding in the live run.**
- **Real unforced P(invest), measured directly via `model-diag` on the live
  `current_model.onnx` at ~5M steps: geometric mean 0.917 (91.7%)**, with the model
  actually choosing to invest in **44 of 45** legal opportunities (97.8%) across 150
  real games. This is even stronger than the isolated 15M-step validation test
  (which reached 0.28) -- likely benefiting from the full 14-arena opponent mix
  rather than the smaller test harness. **The invest-collapse fix is working
  decisively in the live run, not just the isolated test.**

**Process health:** 20 processes (14 arenas + training + benchmark loop + watchdog +
shells), zero errors beyond the one known harmless ONNX-export deprecation warning.
Logit range at ~12 (healthy, nowhere near the 500+ the collapsed v29 run reached) --
consistent with a policy that hasn't (yet, and per the whole point of this fix,
shouldn't) collapsed away from investing.

**Watching for:** whether P(invest) and the checkpoint-vs-Heuristic benchmark keep
improving together as training progresses (the original healthy pattern Marc always
expected -- vs.-Heuristic rising WITH vs.-everything-else, not just vs.-self-play),
per the standing directive to watch for any further degeneracy. Will report on this
as more benchmark cycles land.

**Commands, unchanged, now targeting v30:**
```
cd C:\repos\Castle-Defense-Game-2\CastleDefense.PythonAI
powershell -File pause_training.ps1     # pause -- safe to use the PC afterward
powershell -File resume_training.ps1    # resume exactly where it left off
```

## v30 one-hour go/no-go check (2026-07-26): PASS on all three criteria, letting it run

Marc's explicit framing: don't waste CPU hours on a run that isn't going anywhere --
treat the first hour as a real kill-or-continue gate, not a status ping. Three
named criteria, checked with two independent `checkpoint_benchmark_log.csv`
readings (13.6M and 29.6M steps) plus the live rolling stats in
`training_progress_opponents.csv`:

**1. Is real unforced P(invest) actually rising off ~0?** Not just rising -- already
saturated at the healthy end and holding: **0.991 at 13.6M steps, 0.982 at 29.6M
steps**, both via `model-diag` (zero forced exploration). Chosen 100% of the time it
was legal both readings (45/45, then 23/23). No sign of the old collapse anywhere
near recurring.

**2. Is the self-play asymmetry actually gone?** Self-Play win rate has stayed in a
tight **52.6-58.8% band across 20+ consecutive 500-game rolling readings** spanning
14M-31M steps -- squarely the healthy mirror-match range this campaign's earlier
investigation established, nowhere close to the ~80-90% the old one-sided forcing
produced. Confirmed stable, not just an early lucky window.

**3. Checkpoint-vs-Heuristic trend direction:** **rising** -- 14.0% -> 26.7% model
win rate between the two benchmark readings (13.6M -> 29.6M steps). The live rolling
stat agrees independently: Heuristic Bot's tracked win rate against the trainee rose
from ~13% (14-21M steps) to ~18-19% (30-31M steps) over the same window -- two
different measurement methods pointing the same direction. This is the exact pattern
Marc has always wanted and the previous two runs never showed this early: real
progress against a fixed, competent opponent, not just self-play noise.

**Verdict: PASS, clearly. Letting v30 continue.** None of the v28/v29 failure
signatures are present -- P(invest) isn't stuck near zero, Self-Play isn't inflated,
and vs.-Heuristic is moving the right direction alongside everything else rather
than diverging from it. Process health unchanged: 20 processes, zero errors beyond
the known harmless ONNX deprecation warning.

## Pause/resume cycle + P(invest) trend resolved: selective investing, not re-collapse (2026-07-26)

Marc paused v30 to free his CPU (`pause_training.ps1` -- checkpoint saved cleanly at
83,951,616 steps, zero processes remained), then resumed later
(`resume_training.ps1` -- confirmed "Resuming training: castle_defense_p1_v30.zip",
not a restart; first post-resume update landed at 84,295,680 steps, correctly
continuing). One known quirk reconfirmed: `training_progress.csv`'s own step
counter resets to 0 on every resume (script-local, not the model's real
`num_timesteps`) -- `checkpoint_benchmark_log.csv`'s `training_steps` column
inherited this, so readings after a resume show a much smaller number than the
model's true cumulative progress. Not a bug, just remember to add the pre-resume
checkpoint's step count back in when reading that column across a pause boundary.

**The open question from before the pause: P(invest)'s chosen% had dropped from
100% to 9.2% over two readings, while real invests/game held at 2.72. Three more
readings since (spanning the pause) resolve it decisively:**

| Reading | invest_chosen_pct | P(invest) geomean | invest_legal_decisions | **real invests/game** |
|---|---|---|---|---|
| pre-pause | 9.2% | 3.66% | 993 | 2.72 |
| post-resume #1 | 1.6% | 6.37% | 3,503 | **2.62** |
| post-resume #2 | 0.3% | 0.93% | 10,678 | **2.61** |

**The percentage-based metrics keep cratering, but the real functional metric
(actual invests/game, measured directly via `invest-stats`, unforced) is rock
solid: 2.72 -> 2.62 -> 2.61, across the pause boundary, no decline at all.** This
resolves the mechanism cleanly: `invest_legal_decisions` is exploding (993 -> 10,678)
because the model reaches its own steady ~2.6-investment target early/mid-game and
then correctly, consistently chooses NOT to invest for the rest of a long game
(spending on units/combat instead) -- every one of those later decisions still
counts as "legal but declined" in the percentage stats, mechanically shrinking
`invest_chosen_pct` and `P(invest) geomean` even though nothing about the model's
real behavior is regressing. **This is genuine selective, functional investing, not
the re-collapse failure signature** -- the failure signature would require
invests/game ALSO trending toward zero, which it plainly isn't.

**Lesson for reading this campaign's own new metric going forward:** trust
`invest-stats`-measured real invests/game as the ground truth when
`invest_chosen_pct`/`P(invest) geomean` looks alarming -- the percentage stats are
denominator-sensitive in a way that mechanically produces exactly this shape
(declining %, growing n) for a model that has learned WHEN to stop investing, not
just whether to invest at all. Worth remembering this before treating a
`invest_chosen_pct` drop alone as a kill signal in the future.

**Self-Play win rate: still healthy**, 53.6-55.8% since resume -- the symmetry fix
continues to hold, no ballooning at any point across the pause.

**Checkpoint-vs-HeuristicBot: flat, not yet rising.** 16.7-18.7% model win rate
across all readings so far (both pre- and post-pause), matched by the live rolling
stat (11.8-13.4%, no clear direction). Not the v28/v29 active-decline pattern
either, though -- HeuristicBot itself averages 5.11-5.37 invests/game to the
model's 2.6, so there's a real, understood gap left to close as the model's economy
matures further, not evidence of anything broken.

**Verdict: NOT the failure signature. Letting v30 continue.** Real invests/game
stable, self-play symmetric, no active decline vs. Heuristic (flat, with an
understood reason it isn't rising yet). Process health: 20 processes, zero errors,
114.9M cumulative steps as of this check.

## Overnight check at 494M steps (2026-07-27 10:50 EDT): plateaued, not climbing -- P(invest) has gone past v29's own collapse depth, though real behavior hasn't

Morning status check per Marc's ask. Process healthy and alive (18 processes,
zero errors beyond known harmless matplotlib/ONNX warnings), 493,731,840 cumulative
steps, ~9,450 steps/sec sustained (~44 more hours to the 2B target).

**The honest answer to the key question: no, win-rate-vs-Heuristic has not climbed
overnight. It's plateaued, arguably drifted down slightly.** Full live rolling
trend (`training_progress_opponents.csv`, Heuristic Bot): ~12-15% in the first
30-60M steps, settling into a noisy ~7-14% band for the remaining ~350M steps, with
many individual readings in the 6-10% range from ~130M steps onward. Not the sharp
v28/v29-style collapse toward near-zero, but clearly not rising either -- if
anything the center of the noise band has drifted down a couple points since the
early hours.

**P(invest) (the percentage-based `checkpoint_benchmark_log.csv` metric) has kept
collapsing all night, past where v29 ever reached:** the full overnight series goes
`3.72e-3 -> 8.0e-5 -> 5.5e-8 -> 1.3e-11 -> 4.5e-16 -> ... -> 2.4e-103 -> 4.0e-120 ->
... -> 9.16e-187` (latest, 405M steps). `invest_chosen_pct` has been pinned at
0-0.3% for the last 15+ readings. **9.16e-187 is a more extreme collapse than v29's
own worst point (~1e-143)** by a large margin.

**But real invests/game (measured directly via `invest-stats`, unforced) has stayed
essentially flat the whole time: 2.72 -> 2.62 -> 2.61 -> 2.55, across nearly 400M
steps and the pause/resume boundary.** So this is NOT the same failure mode as
v28/v29 (which was a genuine behavioral collapse to 0.00 invests/game) -- the model
has settled into a real, stable habit of investing ~2.5-2.7 times early/mid-game,
then correctly declining for the remainder of the game, same interpretation as the
previous entry, just now measured at a far more extreme numerical depth.

**Updated read on what this means, given how much further it's collapsed:** the
astronomical P(invest) depth is now a genuine structural concern in its own right,
separate from whether current behavior looks fine. Per the earlier PPO-clip
investigation, recovering ANY meaningful probability mass from ~1e-187 would need
on the order of thousands of consecutive positive gradient updates (`log(1e187)/
log(1.2) ~= 2360`) -- the model has likely now locked itself into a ~2.5-invest
ceiling that it is structurally very unlikely to ever autonomously push past, even
though HeuristicBot's own economy runs at 5.1-5.4 invests/game and the gap between
those two numbers is the most plausible explanation for why vs.-Heuristic isn't
climbing. **Working theory: the model found a real, stable, moderately-good
strategy (invest ~2.5x, then fight) that beats weak/legacy opponents comfortably
but is capped below what's needed to beat Heuristic's stronger economy, and the
same self-reinforcing PPO-clip mechanic that ate the ORIGINAL zero-invest problem
is now doing the same thing to a "invest exactly this many times, never more"
plateau.**

**Self-Play win rate: still excellent, unchanged.** 46.6-58.4% across literally the
entire overnight run (4.7M-409M steps, both before and after the pause), centered
right around 50-52%. The self-play symmetry fix has proven durable across nearly
400M steps -- this specific mechanism is genuinely resolved.

**Win rate by opponent type (live rolling, most recent readings):** `v25_bc` 58.0%
(beats its own un-trained ancestor solidly), Random Dummy 64.6-64.8% (a healthy
majority against a peer rush-shaped opponent per Marc's correction), `v25` 40.0-40.2%,
Spam Bot T4 35.8% (a real, historically-tough matchup, still below par), `v7` 18.2%,
`v4` 12.8% (both long-documented hard matchups), Heuristic Bot ~13-14% (current
window). **The pattern is consistent with the plateau theory above:** comfortable
against weak/legacy/non-scaling opponents, still weak specifically against the
opponents known to run a real, larger economy (Heuristic, v4) -- exactly where the
~2.5-invest ceiling would be expected to bite.

**No new degeneracy beyond the P(invest) depth itself** -- action distribution,
process health, and self-play symmetry all look clean. This isn't a repeat of the
v27 (null-brain) or v28/v29 (zero real investing) failure signatures; it's a new,
narrower kind of plateau worth its own follow-up once there's a clear go/no-go
decision to make.

## v30 killed; deep-dive on the dashboard discrepancy and whether a probability floor would actually help (2026-07-27)

Marc killed v30 (`pause_training.ps1` -- confirmed zero processes, checkpoint saved
but not meant to be resumed). This entry is analysis only, nothing implemented, per
his explicit instruction to understand the mechanism before building anything.

**Marc's dashboard (`plot_training.py`) reads directly from `training_progress.csv`**
-- confirmed by reading the plotting code: `avg_invests_per_game` and
`overall_winrate` are literally the same columns already being tracked, not a
separate measurement. Pulled the actual column values directly: `overall_winrate`
oscillated in a **0.37-0.46 band for the entire ~419M-step run**, and
`avg_invests_per_game` really is **~13.4-13.5** in the final rows. No discrepancy
between what Marc's dashboard shows and what's in the CSV -- both describe the same
ground truth.

**Why `avg_invests_per_game` reads ~13.5 when real voluntary investing (`invest-stats`,
zero forcing) is ~2.5-2.7 -- confirmed mechanism #1:** `invest_count =
int(np.sum(action_arr == 9))` (train_ai_cluster.py) counts every successful invest
action P1 took in a batch, **forced or voluntary, indistinguishably** -- `action_arr`
stores whatever action ends up recorded, and `INVEST_EXPLORE`/curriculum forcing
overwrites the action before it's ever recorded. This confirms Marc's hypothesis
exactly: the dashboard number is contaminated by forced actions, same root issue
already flagged for `checkpoint_benchmark_log.csv`'s deprecated columns.

**But the arithmetic doesn't fully close, and it's worth being honest about that
rather than overclaiming precision.** Built a new BotArena diagnostic
(`curriculum-sim`) to directly measure what a real 90%-forced curriculum episode
produces (matching `investCurriculumEpisode` exactly, including the self-play
symmetry fix): vs HeuristicBot, **avg 1.85 real investments/game** (games end too
fast under a weakened defense to accumulate much); vs a symmetric self-play mirror
(both sides forced, matching the real fix), **avg 6.12** (max 9 -- confirmed the
engine hard-caps `InvestmentCount` at 9, `if (player.InvestmentCount > 8) return
false;` in `GameEngine.Invest()`, so the "runaway 70-invest snowball" I initially
suspected is flatly impossible). Pool-weighting these (30% Heuristic, 50%
self-play, ~20% other opponents estimated) and blending with the 85% of episodes
that aren't curriculum (~2.7 voluntary) gives a back-of-envelope estimate around
**~3**, not ~13.5. **There's a real, unquantified residual gap.**

**Leading candidate for the residual gap: a batch/episode-boundary accounting
artifact, not a further behavioral mechanism.** `batchEpisodes` (what becomes
`len(episodes)` on the Python side) only records an episode when it actually
*completes* within a batch's step window (confirmed:
`batchEpisodes.Add((oppName, epWinner))` fires only inside the "episode just ended"
branch). But `batchAction`/`action_arr` records every action taken during the
batch's full 8192-step (~41-minutes-of-game-time) window, including from episodes
still in progress at the batch boundary. Since curriculum episodes are
disproportionately long-running (measured avg 376-413s, frequently hitting the
600s cap), they're more likely than a typical short/decisive game to straddle a
batch boundary -- meaning their accumulated invest actions get tallied into the
numerator across a batch (or more) before the episode itself is ever credited to
the denominator. This would systematically inflate the ratio beyond what a clean
per-completed-episode count would show, and the direction/mechanism is right, but
the exact magnitude hasn't been directly instrumented -- didn't want to spin up a
new training run just to measure this given the explicit instruction to kill v30
and free the CPU. Flagging as the most likely explanation, not a proven one.

**None of this changes the core diagnosis, and this is the important point: every
version of the accounting, from the crude ~13.5 to the more careful ~3 estimate,
agrees on the same conclusion** -- training-time investing behavior is dominated by
scripted/forced mechanisms, not organic policy choice, and the real, voluntary
number (measured directly and repeatedly at 2.5-2.7 throughout, decoupled from all
of this) is what actually describes the deployed policy's behavior.

**`overall_winrate` pinned at ~40%: reconciled, and it's redundant confirmation of
what the per-opponent breakdown already showed, not a new/separate signal.**
`overall_winrate` is P1's win rate blended across the ENTIRE opponent pool (weights:
Self-Play 50%, Heuristic 30%, League 8%, Spam 6%, AntiSpam 3%, RandomDummy 3%), all
in one rolling-2000-game average. Self-Play is, near-tautologically, always going
to average close to 50% regardless of skill level (it's a mirror match -- both
sides ARE that skill level by construction), and it's HALF the pool. A rough
weighted estimate using our own measured per-opponent numbers (self-play ~51%,
Heuristic ~13.5%, weaker opponents 25-65% variously) lands at **~38-39%** -- squarely
inside the observed 37-46% band. **A flat aggregate near 40% is the mathematically
expected consequence of self-play sitting at its structural ~50% anchor (unmovable
by construction) while Heuristic (30% weight) stays stuck low, not independent
evidence of "no improvement anywhere."** It's the same finding as the per-opponent
breakdown, computed a different way -- worth knowing so this metric isn't read as a
new, separate red flag going forward.

**Central question: would a probability floor actually fix this?**

*What it would fix, with real confidence:* the specific numerical-lockout problem.
Real P(invest) reached ~9e-187 -- recovering meaningfully from that via ordinary
gradient steps needs on the order of ~2,360 consecutive positive updates under
standard PPO clipping (`log(1e187)/log(1.2)`), which is not a realistic timescale.
A floor would keep the model periodically SAMPLING genuine (not scripted) invest
attempts across a wide variety of real game states indefinitely, which is currently
structurally impossible. This part is well-evidenced and the floor is a sound,
targeted answer to it.

*What it would NOT guarantee, and the reasoning matters:* whether that restored
exploration actually discovers that investing MORE (beyond the current ~2.5
ceiling) is worthwhile. Self-play -- 50% of the pool -- is now fair (confirmed
durable at ~50% the entire run), but a fair mirror match gives the same result
regardless of whether BOTH sides invest 2.5 or 5 times, as long as they match each
other -- so self-play alone supplies little pressure to climb past whatever
ceiling both sides already share. The real incentive to invest more can only come
from the asymmetric matchups (Heuristic 30%, real league models), where
HeuristicBot's own strategy (proven independently, in its own tuning history) shows
a bigger economy genuinely wins more. A floor re-enables the exploration that could
discover this; it doesn't guarantee the discovery, though the direct invest reward
(already confirmed large and immediate in the earlier reward-structure
investigation) makes it a reasonably well-supported bet, not a blind one.

**Recommendation for the discussion: a probability floor looks like the right
targeted lever for the confirmed mechanism (numerical lockout), but it's worth
validating cheaply (a short test, same discipline as the original invest-fix
validation) before committing a full run to it** -- specifically checking that
restored exploration actually starts finding invests-beyond-2.5 rewarding against
Heuristic specifically, not just that P(invest) numerically rises again. Not
implemented; reporting for Marc's decision on next steps.

New diagnostic tooling (`CastleDefense.BotArena`'s `curriculum-sim` mode) committed
for future reference.

## Validation methodology + cheap probes for the invest-collapse fixes (2026-07-27)

Marc's core methodological concern, verbatim reasoning: investing didn't even peak
until ~200M steps last run, so a naive "run 15M steps and check if P(invest) moved"
test can pass while the real objective (win-rate-vs-Heuristic climbing) still fails
at scale -- exactly what happened with v30's own 15M-step validation looking clean
right before the full run plateaued. He asked for validation that's predictive of
long-run outcomes, not just short-run liveness, and explicitly floated: a
concentrated-pressure learnability probe, a direct credit-assignment/advantage
check on existing checkpoints (no training needed), a mechanical gradient-response
test once a floor exists, and a reward-gradient check for pool restructuring.

**The validation plan, in order of cost (cheapest/most decisive first):**

1. **Causal counterfactual A/B (zero training cost) -- built and run.** Force the
   model to invest exactly ONCE, at its first legal opportunity, then let it play
   its own natural (unforced) strategy for the rest of the game; compare real
   discounted reward and win rate against playing fully naturally (which, given
   P(invest)~0, means never investing). Uses the exact training reward function
   (GA-tuned `reward_params_5000.json`), exact 9-ticks-per-decision cadence, and
   gamma=0.9998 discounting -- this measures ground-truth outcome, not a model's
   possibly-miscalibrated internal belief. New BotArena mode: `invest-counterfactual`.
   **Greenlight/kill criterion:** a clear, consistent positive delta means investing
   is genuinely good given how the model currently plays afterward (floor is a sound
   bet); a flat or negative delta would mean the model's current downstream style
   doesn't benefit from investing, and a floor would be pointless. **Cost: ~5 min,
   200 games total, zero training steps.**

2. **Value-function advantage probe (zero training cost) -- built and run.** Load
   the actual trained checkpoint's critic and compute a TD-style advantage
   (`reward + gamma*V(after) - V(before)`) directly on the transitions collected in
   (1). Tests whether the ALREADY-TRAINED value function recognizes investing as
   good, or whether the credit-assignment machinery itself is miscalibrated (in
   which case a floor alone wouldn't be enough -- the critic would keep discouraging
   rediscovered invest attempts even with more sampling). New script:
   `analyze_invest_advantage.py`. **Greenlight/kill criterion:** consistently
   positive advantage means the only blocker is sampling probability (floor should
   work cleanly); near-zero/negative advantage would mean a floor needs to be paired
   with a value-function fix too. **Cost: seconds, pure inference on existing data.**

3. **(Not run, held in reserve) Concentrated-pressure short training probe.** All-
   Heuristic (or heavily Heuristic-weighted) pool + a floor prototype, ~10-15M
   steps, checking real invests/game (via `invest-stats`, not the contaminated
   dashboard metric) for ANY upward movement past 2.5. This is the one experiment
   that costs real training steps -- only worth running if (1)/(2) come back
   ambiguous. **They didn't**, so this wasn't run. Keeping it documented as the
   fallback if the eventual full-scale floor test looks murky.

**Results:**

**(1) Counterfactual A/B -- decisive, in both matchups:**

| Opponent | Pool A (forced 1 invest, n=100) | Pool B (natural, n=100) | Delta |
|---|---|---|---|
| HeuristicBot (fresh start, no headstart) | WR 0.0%, return **+13.19** | WR 0.0%, return **-5.12** | **+18.31 return** |
| Self-play mirror | WR **77.0%**, return **+329.65** | WR **57.0%**, return **+247.35** | **+20.0pp WR, +82.30 return** |

Neither pool wins outright vs a fresh (no-headstart) real HeuristicBot within 100
games -- that matchup is just hard at this difficulty -- but the REWARD trajectory
is unambiguously, substantially better with a single investment. Against a
self-play mirror the effect is dramatic and shows up in the outcome that matters
most: **+20 percentage points of win rate from one extra investment early on.**

**This directly refutes my own earlier theoretical worry that self-play (50% of the
pool) gives little pressure toward investing more because it's "fair" once
symmetric.** It's not just theoretically fair in aggregate -- *within* any single
self-play game, whichever side invests more still wins more, same as any other
matchup. Self-play should be a perfectly good source of learning pressure once
exploration is restored; it doesn't obviously need Heuristic-heavy pool
restructuring to supply that pressure.

**(2) Value-function advantage probe -- also decisive:**

| Opponent | n | mean advantage | % positive |
|---|---|---|---|
| Heuristic | 83 | **+6.43** | 77.1% |
| Self-play | 94 | **+9.37** | 76.6% |
| Overall | 177 | **+7.99** | 76.8% |

**The already-trained critic agrees with the causal result** -- it assigns positive
advantage to the vast majority of forced-invest transitions, in both matchups.
Interestingly the self-play advantage is if anything slightly *higher* than
Heuristic's, consistent with (1)'s finding -- another data point against needing to
lean on pool restructuring specifically.

**Synthesis: both cheap, zero-training-cost experiments agree, and neither shows
any sign of a deeper problem.** The causal ground truth says investing helps
substantially; the trained critic already believes this too. That means the ENTIRE
blocker really is the confirmed numerical sampling lockout (P(invest) ~1e-187) --
there's no hidden credit-assignment or reward-miscalibration problem working
against a floor. This is about as clean a green light as a cheap probe can give:
**a probability floor is a well-supported bet, not a blind one, and the evidence
suggests it may not even need to be paired with pool restructuring** to start
seeing real learning pressure toward more investing, since self-play alone already
shows a strong, real incentive once sampling is restored.

**Still not implemented -- reporting for Marc's decision on whether to proceed to
building the floor** (and, per this evidence, whether pool restructuring is even
still a priority, or whether to test the floor alone first for cleaner
attribution). New tooling committed: `invest-counterfactual` and `curriculum-sim`
(BotArena), `analyze_invest_advantage.py` (Python, loads the real SB3 checkpoint's
critic directly).

---

# ============================================================
# HANDOFF STATE (2026-07-27) — READ THIS FIRST IN A NEW SESSION
# ============================================================

Session retired here for token-cost reasons. Everything below is what a cold
session needs. **No training is running. Do not start a long run without reading
the "Next step" section — the floor fix is NOT yet greenlit.**

## Current state

- **v30 is dead/killed** (Marc's call, plateaued). `castle_defense_p1_v30.zip` =
  **502,677,504 steps**, intact, resumable — but not worth resuming (400M+ steps
  of a never-invest tier-1-rush strategy we're trying to move away from).
- **`castle_defense_p1_v30_floortest.zip` = 516,784,128 steps** — the
  probability-floor validation checkpoint (v30 + ~14.1M steps with the floor
  active). Verified it loads cleanly and is resumable.
- **Zero training/arena/benchmark processes alive.** Machine is idle.
- Everything committed; working tree clean.

## 1. The probability floor — what was built

**Where:** `train_ai_cluster.py`. `FloorInvestActionNet` wraps `policy.action_net`
(the final `Linear(512,14)` producing raw action logits). Clamps the invest logit
(index 9) so it can never sit more than `INVEST_FLOOR_MAX_GAP = 5.0` below the
best logit in that forward pass.

**Why that injection point:** `action_net` is the single shared path used by
rollout log-probs, `model.train()`, `check_policy_health()`, and the ONNX export —
wrapping there keeps all of them consistent automatically. Applied to the RAW
logit, *before* legality masking, so when investing is illegal (can't afford it)
the env's action mask still zeroes it regardless — the floor only ever matters when
investing is a genuine legal choice.

**Why 5.0:** matches the natural early-training logit spread this project saw
(~10-19 range before any collapse). ~exp(-5) ≈ 0.7% relative weight — ordinary
early-training uncertainty, not a forced push. Enough for PPO to see real invest
samples; not enough to force constant investing like the old 90% curriculum did.

**`apply_invest_floor(model)`** is idempotent and is called at every model
load/create point, *including* the NaN-rollback and degenerate-policy-rollback
paths (those construct a fresh `action_net` and would silently lose the floor).

**IMPORTANT — `save_model_unwrapped(model, path)`:** `MaskablePPO.load()` rebuilds
a plain policy then loads the state_dict onto it, so saving while wrapped produces
`action_net.base.weight` keys and the checkpoint **fails to reload**
(`Missing key(s): action_net.weight`). This hit us for real mid-session. All
`model.save()` calls now go through `save_model_unwrapped`, which unwraps → saves →
re-wraps. Verified: the floortest checkpoint reloads cleanly with
`action_net` type `Linear`. **Any new save path must use this helper.**

## 2. Floor validation result — MECHANICALLY CONFIRMED, BUT NOT GREENLIT

Bounded test: resumed the collapsed v30 with the floor on, ~14.1M steps (target was
20M; the app crash cut it short — but the result is usable and consistent).

| | pre-floor v30 | floor @8M | floor @14.1M |
|---|---|---|---|
| P(invest) geomean | ~9e-187 | 6.5e-3 | **7.12e-3** |
| max logit range | 400-800 | 27.3 | 160.8 |
| **real invests/game** (unforced) | **2.55** | **2.26** | **2.18** |

**(a) Mechanical fix: PASS, decisively.** P(invest) recovered from ~1e-187 to
~7e-3 and **held there across 14.1M steps of live PPO training without being
re-clipped back down** — that was the key anti-re-clipping question and it's
answered. Direct softmax check on real collapsed states also confirmed the clamp
lands exactly at the configured 5.0 gap.

**(b) Behavioural payoff: NOT demonstrated.** Real unforced invests/game did **not**
climb — 2.55 → 2.26 → 2.18, flat-to-slightly-down, nowhere near HeuristicBot's
~5.2. So the floor reopens the door but, in 14M steps, the model has not walked
through it.

**Honest verdict: AMBIGUOUS — do NOT greenlight a 100M+ run on this.** Caveat in
both directions: 14M steps is short, and Marc's own observation is that investing
took ~200M steps to peak last run, so "no movement in 14M" is genuinely weak
evidence either way. Per the standing methodology
([[feedback_rl_validation_methodology]]), the correct next move is the **bounded
concentrated-pressure fallback**, not a full run: floor ON + a Heuristic-heavy
(or all-Heuristic) pool, ~10-20M steps, watching real `invest-stats` invests/game
for *any* move above ~2.5. If it moves under maximum pressure → greenlight. If it
still won't move when investing is the only way to win → the floor alone is
insufficient and we go back to the drawing board (pool restructuring, or the
clip-exemption idea still held in reserve).

## 3. Crash post-mortem — system-wide resource starvation, not an app bug

**Evidence:** Zero errors/exceptions/tracebacks/OOM/CUDA failures in the training
log — it ends with clean `[Arena N] Connection closed.` lines, the signature of the
Python process being *externally killed*, not crashing. Meanwhile the Windows
Application event log shows **`steamwebhelper.exe` hung 4 separate times between
13:19:24 and 13:20:54** ("stopped interacting with Windows and was closed") —
i.e. an unrelated app was also being starved in the exact window the Claude app
died and the checkpoint was written (13:20:01).

**Conclusion:** the machine, not the app, was the failure point. 14 arenas + the
trainer = 15 CPU-bound processes on 20 logical cores, plus GPU, plus 31.8 GB RAM
under pressure. The Claude desktop app was collateral damage, not the cause.
Nothing in the training pipeline itself misbehaved.

## 4. THE GAP THAT ACTUALLY STRANDED MARC (fixed)

Marc's report — "no way to stop the training except to kill it" — was a **real bug
in `pause_training.ps1`, now fixed.**

The old script found processes *only* via PID files (`campaign_run.pid` etc.),
which are written **only by `resume_training.ps1`**. The floor test was launched
directly (not via that script), so no PID file existed; worse, stale PID files from
the *previous* run were still on disk pointing at long-dead PIDs, so the script
reported "already stopped" and moved on. It would then kill the arenas by name —
but **the Python trainer survived**, with no clean way to stop it. Exactly the
trap Marc hit.

**Fix:** `pause_training.ps1` now also sweeps by **command line**
(`train_ai_cluster.py`, `test_invest_fix.py`, `plot_training.py`,
`benchmark_checkpoints.ps1`), so it stops the run *however it was launched*. It
excludes its own PID, scopes the "did it work" check to our processes only (a bare
`Get-Process python` false-alarms on unrelated Python), and deletes stale PID files
so they can't cause phantom "already stopped" next time. Tested standalone: safe
no-op when nothing is running.

## 5. APP-INDEPENDENT STOP COMMAND (for Marc, no Claude session needed)

Open a normal PowerShell window and run:

```
cd C:\repos\Castle-Defense-Game-2\CastleDefense.PythonAI
powershell -File pause_training.ps1
```

**Confirmed to have zero dependency on the Claude app or any agent session** — it's
pure PowerShell process management (`Get-CimInstance` / `Get-Process` /
`Stop-Process`), no Python, no network, no IPC. It works whether training was
started by `resume_training.ps1`, by hand, or by an agent. Training checkpoints
every 3 PPO updates (~35s of at-risk progress), so stopping this way loses almost
nothing and `resume_training.ps1` picks up from the saved step count.

Hard-stop fallback if anything ever survives the above (blunt, kills all arenas +
any matching trainer):
```
Get-Process CastleDefense.Simulation -EA SilentlyContinue | Stop-Process -Force
Get-CimInstance Win32_Process | ? { $_.CommandLine -like "*train_ai_cluster.py*" } |
  % { Stop-Process -Id $_.ProcessId -Force }
```

## 6. RESOURCE CAP RECOMMENDATION for future long runs

The 14-arena load contributed to the crash. For unattended multi-day runs on this
machine (20 logical cores, 31.8 GB RAM), recommend:

- **Drop `N_ENVS` from 14 → 10** in `train_ai_cluster.py`. Leaves ~8-9 logical
  cores for the OS, the desktop app, and Marc's own work instead of ~4. Note prior
  measurement: going 14→18 arenas gave *no* throughput gain, so this pipeline is
  not arena-count-bound near this range — the expected throughput cost of 14→10 is
  modest and likely well worth the stability.
- Optionally set the arena processes to below-normal priority so the desktop always
  wins contention (`Get-Process CastleDefense.Simulation | % { $_.PriorityClass =
  'BelowNormal' }` after launch).
- Don't foreground-poll a long run from the agent session; let it detach and check
  in periodically.

## Next step (for the fresh session)

Run the **bounded concentrated-pressure test** described in §2 — floor ON,
Heuristic-heavy pool, ~10-20M steps, `N_ENVS=10`, judged on real `invest-stats`
invests/game moving above ~2.5. That is the go/no-go for the floor. **Do not launch
a 100M+ run before it passes.**

---

# 2026-07-28 — bounded concentrated-pressure test launched (the HANDOFF's "Next step")

Fresh session, read the HANDOFF STATE section + memory first per instructions. Per
the standing methodology ([[feedback_rl_validation_methodology]]) and the HANDOFF's
explicit next step, ran the **bounded concentrated-pressure test**, not a new long
run: floor ON + Heuristic-heavy pool, judged on real `invest-stats` invests/game
(ground truth), not the contaminated `training_progress.csv` figure or P(invest)
itself.

**Code changes made (all default-preserving — a normal launch with no new env vars
set behaves bit-identically to before):**
1. `CastleDefense.Simulation/Program.cs` — the opponent-pool roll thresholds
   (`0.03/0.06/0.12/0.20/0.50` cumulative for RandomDummy/AntiSpam/Spam/League/
   Heuristic) are now `CUM_RANDOM_DUMMY`/`CUM_ANTISPAM`/`CUM_SPAM`/`CUM_LEAGUE`/
   `CUM_HEURISTIC`, each overridable via an env var of the same name
   (`POOL_CUM_*`), defaulting to the exact production values. Rebuilt
   (`dotnet build -c Release CastleDefense.Simulation`) so the arena exe picks
   this up.
2. `test_invest_fix.py` — added `TEST_N_ENVS` (was hardcoded 14) and
   `TEST_PROGRESS_LOG` (was hardcoded `"training_progress.csv"`) env overrides.
   **The progress-log one is a real near-miss worth flagging**: `__main__`
   unconditionally `os.remove()`s that file at startup to start each test run's
   log clean — with the hardcoded default this would have silently deleted the
   live campaign's real, currently-uncommitted `training_progress.csv` /
   `_opponents.csv` the moment this harness was run again for any test. Fixed by
   defaulting to the same filename (no behavior change for the two prior tests
   that already used it) but making it overridable so this run uses a
   completely separate file.
3. New `launch_heuristic_pressure_test.ps1` — a `resume_training.ps1`-style
   detached launcher setting all the env vars below and starting
   `test_invest_fix.py` hidden, logging to `heuristic_pressure_test.log`/
   `.err.log`, PID to `heuristic_pressure_test.pid`. `pause_training.ps1`
   already sweeps on `*test_invest_fix.py*` by command line, so no changes
   needed there — confirmed this run is stoppable the standard app-independent
   way.

**Test configuration:**
- `TEST_MODEL_NAME=castle_defense_p1_heuristic_pressure_test`,
  `TEST_BASE_MODEL=castle_defense_p1_v25_bc`,
  `TEST_ONNX_NAME=heuristic_pressure_test_model.onnx`,
  `TEST_PROGRESS_LOG=training_progress_heuristic_pressure_test.csv` — all
  distinct from every real-campaign filename, so this can never collide with or
  overwrite `current_model.onnx`, `castle_defense_p1_v30*`, or the real
  `training_progress*.csv`.
- `TEST_N_ENVS=10` per the resource-cap rule
  ([[project_training_stop_and_resources]]).
- `TEST_TOTAL_STEPS=20000000` — the top of the HANDOFF's approved 10-20M bound,
  to give the strongest possible signal within budget.
- Pool: `POOL_CUM_RANDOM_DUMMY=0.02, POOL_CUM_ANTISPAM=0.04, POOL_CUM_SPAM=0.08,
  POOL_CUM_LEAGUE=0.10, POOL_CUM_HEURISTIC=0.90` → Random Dummy 2%, Anti-Spam
  2%, Spam 4%, League 2%, **Heuristic 80%**, Self-Play (remainder) 10% — vs.
  production's Heuristic 30% / Self-Play ~50%. Deliberately maximizes exposure
  to the one opponent in the pool that actually punishes not investing
  (Heuristic scales its own economy), while keeping a small self-play/simple-bot
  slice for play-style diversity.
- **Base model: fresh warm-start from `v25_bc`, not a resume of
  `v30`/`v30_floortest`** — a judgment call the task explicitly flagged as
  needing a decision. Reasoning: `v30_floortest` already carries ~517M steps of
  momentum toward the never-invest rush baked into the *rest* of the policy
  (unit-spend habits, etc.), not just the invest logit itself that the floor
  patches — that entrenchment is the most likely reason 14M more steps under
  the floor alone produced zero movement in real invests/game. A fresh base
  removes that confound and gives the Heuristic-heavy pressure its cleanest
  possible shot; it's also the same starting point that produced this
  project's one genuinely strong positive signal so far (the self-play-
  asymmetry-fix validation: fresh `v25_bc` + forced exploration hit P(invest)
  geomean 0.28, 56% real legal-opportunity invest rate over 15M steps — before
  that promising short-run signal didn't survive to the full 500M-step v30
  run, which is exactly why this is being re-tested rather than assumed).

**Launched and verified healthy (foreground, not left unchecked) at the first
checkpoint, update 3 / 245,760 steps:** all 10 arenas connected cleanly, zero
errors/warnings beyond a pre-existing benign ONNX-export deprecation notice,
checkpoint saved successfully (logit range 6.59, action0 rate 0% — not
degenerate), and the realized opponent mix at this early sample already matches
the design closely: Heuristic Bot 274/352 games (78%, target 80%), Self-Play
40/352 (11%, target 10%). `training_progress_heuristic_pressure_test.csv`'s
`avg_invests_per_game` column is the same contaminated metric flagged
repeatedly in this log (includes forced curriculum invests) — **do not use it to
judge this test**; the real judgment metric is `CastleDefense.BotArena.exe
invest-stats` run against a snapshot of `heuristic_pressure_test_model.onnx`
copied into `league_models/`, same as the HANDOFF specifies.

**Not yet done, for whoever checks in next (this run is bounded to ~20-40
minutes wall-clock, so likely done or nearly done by the time this is read):**
1. Confirm the run reached `total_timesteps` or was stopped cleanly (check
   `heuristic_pressure_test.log` tail and whether `heuristic_pressure_test.pid`'s
   process is still alive).
2. Copy `heuristic_pressure_test_model.onnx` into
   `CastleDefense.Simulation/bin/Release/net10.0/league_models/` under a name
   containing `heuristic_pressure_test`, then run
   `CastleDefense.BotArena.exe invest-stats heuristic_pressure_test headstart 150`
   for the real invests/game number (ground truth), and ideally `model-diag` too
   for the P(invest)/entropy numbers for comparison against the prior floor-only
   test's 7.12e-3 / 2.18.
3. Judge per the HANDOFF's stated bar: real invests/game moving meaningfully
   above ~2.5 → greenlight a full run combining the floor + a similarly
   Heuristic-heavy pool. Still pinned near ~2.2-2.6 → the floor + pool-pressure
   combination is insufficient even under maximum realistic pressure, and the
   next move is back to the drawing board (the clip-exemption idea or a more
   direct reward-shaping approach, both still held in reserve from the earlier
   six-candidate list) — report either outcome honestly, per Marc's standing
   instruction not to spend cycles without being sure they're worth it.
4. No periodic benchmark loop (`benchmark_checkpoints.ps1`-style) was attached
   to this run deliberately — it would add BotArena game-playing CPU load on
   top of an already-N_ENVS=10-capped machine for a run only intended to last
   tens of minutes; a single end-of-run `invest-stats`/`model-diag` check is
   the cheaper, sufficient signal here.

## League pool bloat found and fixed mid-session (2026-07-28) — NOT intentional

Marc flagged, before the bounded test above got far: every run keeps loading a
pile of `v28_snap_*`/`v29_snap_*` files and worried they're near-identical
weak opponents just accumulating and eating memory, contrary to his stated
intent of a lean league (best models + spam bots only).

**Confirmed: unintended accumulation, not by design.** `league_models/` (the
folder `CastleDefense.Simulation/Program.cs` loads on startup via
`Directory.GetFiles(leagueDir, "*.onnx")` — every `.onnx` file, no name
filtering, no recursion, `AIBrain`-loaded into memory for the arena's entire
process lifetime) had **46 files**: the 13 intentionally-curated checkpoints
(v3/4/7/14/16/20/21/22/23/25/25_bc/27/27_last) plus **30 auto-generated
timestamped snapshots** (10 each from the v28, v29, and v30 campaigns) plus
the 2 floor-validation snapshots (`v30_floortest_8M/14M`).

**Root cause:** `benchmark_checkpoints.ps1` copies `current_model.onnx` into
this exact folder every cycle as `${ModelTag}_snap_$timestamp.onnx` (so its own
`models`/`model-diag` BotArena calls can find it by name — that part is
intentional and documented) and its own comment already states the correct
intent: *"these are cheap, frequent self-checkpoints, not meant to accumulate
as permanent league anchors."* The cleanup that was supposed to enforce that
only matched `"${ModelTag}_snap_*.onnx"` — i.e. only THIS campaign's own tag.
Every time a campaign was retired and the next one started under a new tag
(v28 → v29 → v30), the previous tag's 10 snapshots were never touched by
anything ever again. Three retired campaigns' worth silently piled up over four
days, tripling the loaded league size with policies we already know are
weak/collapsed variants of the same never-invest rush this whole campaign is
trying to fix — and since `Directory.GetFiles` doesn't distinguish them from
the curated checkpoints, the 8-20% "League" pool slice (in production; 2% in
this bounded test) was drawing 65% of its picks from this junk rather than the
intended best-models set. Real cost confirmed: each of the (14, or 10 for this
test) arena processes loads every file in the folder as its own in-memory
ONNX Runtime session — 46 sessions/arena instead of the intended 13-15.

**Fixed, in order:**
1. Archived (not deleted — reversible) the 30 orphaned snapshot files to
   `league_models/archive_stale_auto_snapshots/` (not visible to
   `Directory.GetFiles` at the top level, so the training loader no longer
   sees them). Kept all 13 curated checkpoints and both `v30_floortest_*`
   files exactly as Marc asked. Folder is now 15 files.
2. `benchmark_checkpoints.ps1`'s cleanup filter widened from
   `"${ModelTag}_snap_*.onnx"` to `"*_snap_*.onnx"` — now a global rolling
   keep-10 window across every tag, so a retired campaign's snapshots get
   swept away automatically the next time ANY benchmark loop runs, instead of
   requiring another manual cleanup like this one.
3. **The already-running bounded test (launched earlier this session) was
   stopped and relaunched** via `pause_training.ps1` + `launch_heuristic_pressure_test.ps1`
   — its arenas had already loaded the stale 46-model league into memory
   before the archive step, and League-loading only happens once at arena
   startup, so a restart was required for the fix to actually take effect. Lost
   ~0 real progress (was only 245K/20M steps, ~1.2%, into the aborted first
   attempt). **Verified on the relaunch**: League picks in the first two
   checkpoints are exclusively `v27_last`, `v16`, `v21`, `v7` — the curated
   set, zero snapshot noise — and Heuristic Bot share is tracking the intended
   ~80% (187/241 games ≈ 78% at the second checkpoint). Healthy, no errors.

**Not done (flagging, not fixing, since it's a design question for Marc, not
a bug):** the deeper coupling — `benchmark_checkpoints.ps1` writes its
self-comparison snapshots into the SAME folder the live training pool draws
from, because that's also where `FindLeagueModelsDir()` looks for named
models by convention — is still there. The keep-10 global window (fix #2)
should keep this from silently growing unbounded again, but if Marc wants
benchmark self-snapshots to NEVER be eligible as training opponents even
transiently, that would need a separate folder plus updating
`FindLeagueModelsDir()`'s callers, a bigger change not attempted here.

## League/benchmark folders fully separated (2026-07-28, Marc's follow-up ask)

The keep-10-globally fix above prevents unbounded growth but still let
benchmark self-snapshots sit in the live training league for however long
they exist before the next cleanup pass — Marc asked for the cleaner fix:
benchmark snapshots should NEVER be eligible as training opponents, even
transiently, since the curated `league_models/` set is deliberately
hand-picked and validated.

**Fix:** `benchmark_checkpoints.ps1` now writes its snapshots to a new sibling
folder, `league_models_benchmark/`, instead of `league_models/`. Since
`CastleDefense.Simulation/Program.cs`'s training-opponent-pool loader is
hardcoded to the literal directory name `league_models` (non-recursive), it
was already structurally incapable of ever seeing anything placed in a
differently-named sibling — no changes needed there at all; moving the write
target was sufficient by itself for the training side of the guarantee.

The harder part was BotArena, whose `models`/`model-diag` modes (both used by
the benchmark loop to test its own snapshot) resolve model names via
`FindLeagueModelsDir()`, called at 13 separate callsites across
`CastleDefense.BotArena/Program.cs`, all hardcoded to look for a directory
literally named `league_models`. Rather than touching all 13 (each doing its
own fragment-match `Directory.GetFiles(dir, "*.onnx")...Contains(fragment)`),
added a single env var check at the top of `FindLeagueModelsDir()` itself:
`BOTARENA_MODEL_DIR`, checked before the hardcoded candidates, returned
directly if set and populated with `.onnx` files. `benchmark_checkpoints.ps1`
sets this to `league_models_benchmark` before its `models`/`model-diag` calls,
so every one of those 13 callsites transparently resolves from the new folder
for that invocation, with zero other code changes and zero behavior change
for any other BotArena invocation that doesn't set the env var (e.g. a manual
`invest-stats` run from a terminal, as used below, is completely unaffected).

**Verified directly, not just reasoned about:** copied a throwaway probe
model into the new `league_models_benchmark/` folder, ran
`BOTARENA_MODEL_DIR=...\league_models_benchmark BotArena.exe models headstart 1 test_probe`
— resolved correctly from the new folder. Ran the same `models headstart 1 v25_bc`
command with NO env var set — resolved from the real `league_models/` exactly
as before. `league_models/` still contains exactly its 15 curated files;
`league_models_benchmark/` starts empty (auto-created by the script,
gitignored the same way as `league_models/` — already covered by the repo's
generic `bin/` ignore rule, no `.gitignore` changes needed).

## Bounded concentrated-pressure test RESULT (2026-07-28) — real but modest movement, not decisive

Run finished cleanly on its own: reached 20,070,400 / 20,000,000 timesteps
(29,568 games), saved `castle_defense_p1_heuristic_pressure_test(.zip/_last.zip)`,
`heuristic_pressure_test_model.onnx`, and
`training_progress_heuristic_pressure_test(.csv/_opponents.csv)` — all confirmed
present on disk. No leftover training/arena/BotArena processes (`pause_training.ps1`
and a direct process check both came back clean).

**Measured with the ground-truth tool (`invest-stats`, vs HeuristicBot,
headstart) — NOT the contaminated `avg_invests_per_game` column:**

| checkpoint | real invests/game | HeuristicBot's own (same games) |
|---|---|---|
| `v25_bc` (pre-fix base) | 1.85 (150 games) | 4.99 |
| `v30_floortest_14M` (floor only, no pool change — re-measured today for a same-day apples-to-apples check) | 2.25 (150 games) | 5.27 |
| **`heuristic_pressure_test_final` (floor + Heuristic-heavy pool, THIS test)** | **2.76 (150 games), 2.80 (300 games)** | 5.18-5.24 |

The 300-game reading confirms the 150-game one (2.76 vs 2.80) — **this is a real,
repeatable ~24-28% relative increase over the floor-only ~2.2-2.26 ceiling, not
sampling noise**, and it clears the HANDOFF's stated bar ("any move above
~2.5"). It is **not**, however, a climb anywhere close to HeuristicBot's own
~5.2 — less than halfway there.

**The floor held — and did far better than in isolation.** `model-diag`
(vs a 12-opponent mixed pool, 150 games) on the final checkpoint:
`P(invest) when legal: geometric mean = 9.997E-001` (essentially certain —
up from the floor-only test's already-passing **7.12e-3**, itself up from
pre-floor's ~9e-187) and max logit range only 15.2 (nowhere near the
hundreds-scale range that signaled collapse before) — **no re-clipping, and
the sampling-lockout problem this whole campaign has been chasing looks
essentially solved under this configuration.** Only caveat: no periodic
snapshots were taken mid-run (deliberate, to save CPU on a bounded test — see
the earlier "Not done" note), so there's no P(invest) trend curve for this
run the way the floor-only test had one at 8M/14.1M — only start (implicitly
~7e-3 inherited from `v30_floortest`, since that's not this run's actual
start point... **actually this run started fresh from `v25_bc`, which had
never been floor-tested, so its true starting P(invest) is unmeasured — the
end-state 0.9997 is the only real datapoint**) and end.

**A more precise diagnosis than "floor insufficient" falls out of this:** if
P(invest) is ~0.9997 whenever investing is legal, but real invests/game is
still only 2.80, the bottleneck has moved. It's no longer "the policy won't
choose to invest" (that's fixed) — it's that **legal invest opportunities are
rare in the first place**: `model-diag` sampled 338,609 total decisions and
found invest legal in only 68 of them (0.02%). The model isn't managing its
money (spending on units vs. saving toward the next investment threshold) to
create as many invest opportunities as HeuristicBot's economy does — a
different, and more specific, problem than pure exploration/sampling.

**Win rate vs HeuristicBot (300 games, headstart), for context:** this
checkpoint wins 22.3% (Heuristic 77.7%) — worse than `v25_bc`'s 25.3% but
better than `v30_floortest_14M`'s 16.0%. Not an overall improvement yet, but
not a regression below the pre-floor base either, and unsurprising this early:
Marc's own prior observation is that investing behavior didn't peak until
~200M steps in the last full run, and this is only 20M steps into a
completely fresh warm-start under a new pool.

**Verdict: real, repeatable positive movement on both the metric that
matters (real invests/game, +24-28%, confirmed at two sample sizes) and the
underlying mechanism (P(invest) collapse looks solved, not just patched) —
but NOT the decisive climb toward ~5.2 that would justify a blind full
2B-step commitment.** Per the letter of the HANDOFF's stated bar this clears
the "greenlight" threshold (it moved, and stayed moved, above ~2.5) — but the
honest calibration is that 2.80 is early positive signal, not proof of
convergence to Heuristic-level economics. Recommend: **don't jump straight to
a full 2B-step run yet** — instead, given the floor+pool combination is now
confirmed healthy and cheap to keep running, extend THIS SAME configuration
for a further bounded stretch (e.g. another 50-100M steps, still far short of
a full commitment) specifically watching whether real invests/game keeps
climbing now that the sampling mechanism is solid, and/or investigate the
newly-identified bottleneck directly (a cheap probe on money-management /
spend-vs-save discipline, analogous to the counterfactual/critic probes used
earlier in this campaign, rather than another blind training stretch) before
committing full compute. This is a recommendation, not a decision — Marc's
standing preference is that spending real compute cycles is his call.

---
*(Log continues below as the campaign progresses — periodic benchmark results,
plateau diagnosis if one occurs, and any further tuning.)*

# ============================================================
# 2026-07-28 (session 2) — MEASUREMENT AUDIT: two instruments found broken
# ============================================================

Marc asked for a fresh evaluation of the campaign's direction. No training was run.
Instead, two of the numbers this campaign has been steering by were checked against
their source code, and **both turned out to be wrong**. Everything below is a
correction to conclusions already recorded above — read this before trusting any
headline figure in the earlier entries.

## 1. `invest-stats` invests/game is contaminated by free headstart invests

**The bug.** `invest-stats ... headstart` calls `CreateGame(true)`, which builds both
players via `PlayerState(timeSkip)`. That constructor calls `ApplyInvestmentStep()`
`timeSkip` times, so **both sides begin the game with `InvestmentCount` already equal
to `timeSkip`, before either policy acts.** `invest-stats` then reports
`InvestmentCount` at END of game (Program.cs:313) with no subtraction.

`timeSkip = Math.Max(rng.Next(-8, 9), 0)` ⇒ **E[timeSkip] = 36/17 = 2.118**, SD 2.74,
SE of the mean over 150 games = **0.224**.

**Corrected headline table** (subtracting the ~2.12 free baseline):

| checkpoint | as reported | free | **actually earned** |
|---|---|---|---|
| v25_bc | 1.85 | ~2.12 | ~0 |
| v30_floortest_14M | 2.25 | ~2.12 | ~0.13 |
| heuristic_pressure_test | 2.80 | ~2.12 | **~0.68** |
| HeuristicBot | ~5.2 | ~2.12 | **~3.1** |

**What this changes:**
- The gap to HeuristicBot is **4.5x, not 1.9x**. "Less than halfway there" was wrong.
- The noise floor is larger than assumed: the headstart RNG alone contributes ±0.22
  per 150-game run. The 150-vs-300-game agreement (2.76 vs 2.80) confirms the
  MEASUREMENT is repeatable, not that the policy improved — both draws sample the
  same free-invest distribution.
- The direction of the result **survives and is arguably stronger**: ~0 earned →
  ~0.68 earned is a change in kind, not a 24% bump. The floor+pool combination did
  do something real.

**Also resolved: the 68 / 338,609 "invest legal in only 0.02% of decisions" figure.**
No arithmetic error — `model-diag` uses `CreateGame(false)` (no headstart,
`InvestmentCount=0`, `Money=0`) while `invest-stats` uses headstart. Both use 3-tick
greedy argmax. So 0.02% describes COLD-START games only, and is unsurprising there:
the cheapest unit costs $2-3 while the first invest costs $18. **It does not describe
the headstart games the model is trained and benchmarked on.** The conclusion "the
bottleneck has moved to money management" was drawn from the wrong game regime and
should not be relied on.

**Fix applied:** `invest-stats` now records starting `InvestmentCount` and reports
EARNED invests (end − start) with a standard error, plus the free baseline as a
visible sanity line. `verify_invest_metric.ps1` re-measures all three checkpoints at
300 games, both with and without headstart. NOT YET RUN — needs a Windows build.

## 2. The board evaluator's calibration was mis-specified; its weights were meaningless

**The bug.** `train_evaluator.py` fit `LogisticRegression(fit_intercept=False)` on six
features that are all sigmoid outputs — every component equals 0.5 in an even
position. With no intercept the model can only score an even game at 50% when
**sum(w) == 0**, forcing a zero-sum split in which some coefficients must be negative.
The script then applied `np.maximum(raw_w, 0.0)` and normalised, **deleting the
negative half of the solution.**

Measured on the real data (263k mirrored samples, numpy GD):

```
B) unclamped     sum(w) = +0.042    <- confirms the forced degeneracy
   HP 3.34  Income 4.77  Money 3.16  Army 0.79  Gadget -5.76  Repair -6.25
A) after clamp   HP 0.277 Income 0.396 Money 0.262 Army 0.065 Gadget 0 Repair 0
```

**The zeros in `evaluator_weights.json` were clamped negative coefficients, not
"this component has no predictive value."** A regularisation sweep confirms the fit
was never identified: same data, same spec, only L2 varying, and the surviving set
moves (Army drops out between 1e-4 and 1e-3; Income climbs 0.40→0.60; sum(w) drifts
0.042→0.191). The committed `evaluator_weights.json` zeroes HP/Army/Repair; this
session's reproduction zeroes Gadget/Repair. **Same script, same data, opposite
answer — that non-reproducibility is the verdict.**

**What the data actually says:**

```
                          acc    logloss     HP  Income  Money   Army  Gadget  Repair
C) centered logistic   74.39%    0.4914   5.53    5.26   2.96   2.96    0.13    2.39
D) deployed form       75.37%    0.5510   0.248   0.752  0      0       0       0
   in-code before      74.88%    0.5706   0.200   0.491  0.169  0.035   0.105   0
```

Fit D matches the form `EvaluateBoard()` actually evaluates — `(w·x)/sum(w)`, `w >= 0`.
Fit C is a correctly specified logistic on centered features; **castle HP comes out as
the LARGEST term**, and all six components carry signal.

**RETRACTION.** Earlier in this session the old weights were read as independent
confirmation that this game is economy-dominated (income ~67% of win probability, HP
~0%). That was reading an artifact. Economy is still the largest single factor under
the deployed form (income 0.75 vs HP 0.25), but HP is nowhere near zero, and under the
better-calibrated logistic HP slightly exceeds income. **The evaluator does not
provide strong independent support for economy-dominance.**

**Fixes applied:**
- `GameState.cs` EvalWeight* → fit D (Hp 0.2476, Income 0.7524, rest 0.0), with the
  full diagnosis in a comment so this can't silently regress.
- `train_evaluator.py` rewritten: fits the deployed `(w·x)/sum(w)` form with
  non-negativity enforced DURING optimisation (not clamped afterwards), and reports a
  correctly specified centered logistic alongside for reference. `CURRENT_W` was also
  stale (`[0.35,0.15,0.05,0.30,0.10,0.05]`, not matching shipped C# for several
  rounds), so the "Current vs Learned" table was comparing against a phantom baseline.
- `audit_evaluator.py` added — reproduces the diagnosis and the regularisation sweep.
- Loud warning when a calibration CSV has no `tick` column. **`calib_data.csv` has no
  `tick`/`game_id`**, so the autocorrelation thinning at line 122 never fires for it
  and all 1.7M within-game frames go in raw, swamping the human-replay data. The real
  fix is at the source: the C# `--collect-calibration` exporter should emit tick and
  game_id.

**IMPORTANT SIDE EFFECT: `EvaluateBoard()` is in the RL reward loop.**
`Simulation/Program.cs:446` feeds it into `batchEval`, which Python uses for N-step
potential-based reward shaping. Changing these weights **changes the training signal**
(normalised: HP 0.20→0.248, Income 0.49→0.752, money/army/gadget→0). It does NOT
affect HeuristicBot or the web game, neither of which calls it.

## 3. Six pressures toward investing, all failing

Worth recording plainly. The policy currently has **six** independent mechanisms
pushing it to invest: `+3000` per income increase, early-invest bonuses
(+800/+400/+150), savings-progress reward, the anti-spend penalty, the 90%
`INVEST_CURRICULUM_FORCE` curriculum, and income-dominated potential shaping via
`EvaluateBoard()`. It still converges on not investing. Six independent pressures all
failing makes "PPO just needs better exploration" the less likely explanation.

The structural reading: the cheapest unit costs $2-3 and the first invest costs $18,
so saving requires **6-9 consecutive restraint decisions** at the 9-tick training
cadence, while a $2 purchase is legal at nearly every one of them. That is a
temporally-extended commitment problem (options/macro-actions), not an exploration
problem — and a logit floor on action 9 cannot fix it, because the floor only acts at
the moment investing is already legal.

## 4. Cadence mismatch worth knowing

Training decides every **9 ticks** (`Simulation/Program.cs`, `for (int fi = 0; fi < 9; fi++)`).
`AIModelOpponent` and `model-diag` decide every **3 ticks**. Every BotArena number ever
recorded for an ONNX checkpoint was measured at 3x the decision rate the policy was
trained at. Not necessarily wrong — but it is not the training regime, and nothing in
the log acknowledges the difference.

## 5. Direction recommendation (for Marc's decision)

Recorded so the reasoning survives: **search, staged, with measurement fixed first.**

- **PPO**: 30 versions, multi-day runs, has never beaten a hand-written heuristic. The
  reward function now carries ~10 hand-tuned shaping terms — the signature of a
  learning signal being hand-carried. Its specific failure (above) is structural.
- **Heuristic-only**: strongest agent by a wide margin, but bounded by the author's own
  understanding of the game. That is the definition of not-superhuman.
- **Search**: the historically reliable path to superhuman play, converts inference
  compute directly into strength with no training loop, and plays above the quality of
  its own evaluator. Decisively, it **subsumes** the other two rather than competing:
  HeuristicBot becomes the rollout policy, and a learned value net can later replace
  the hand evaluator (AlphaZero decomposition). It is the only option under which the
  other two paths' work is not stranded.

Blocker for search: the engine cannot be cloned. `_scheduledEvents` holds `Action`
delegates (GameEngine.cs:38) created as lambdas at 14 gadget-effect callsites, closing
over `engine`/`_def`/locals — closures cannot be deep-copied, and a cloned engine's
pending effects would silently mutate the ORIGINAL state. `_actionQueue`
(`ConcurrentQueue<Action>`) has the same problem. Fix = data-based `PendingEffect`
records dispatched through a switch. Independently valuable: gives deterministic
replay, and would have made `InferV1TimeMachineState`'s brute-force inference
unnecessary.

Recommended order: **(1) trustworthy benchmark harness → (2) engine refactor
(data-based effects, Clone, seeded RNG) → (3) flat rollout search with HeuristicBot as
rollout policy → (4) decide from data whether to deepen search or feed it a learned
value net.**

Step 1 is first because two of the two instruments examined this session were broken.
That is a base rate, not a coincidence, and it means the project currently cannot tell
whether a change helped.


# ============================================================
# 2026-08-07 — PROBE A: does a better rollout policy make the search stronger?
# ============================================================

Marc approved a staged plan whose Stage 0 was one cheap probe that could kill the
whole direction in a day. This is that probe's result. **It came back negative, and
the negative is structural rather than a tuning miss.** No training was run; total
cost was about 90 minutes of CPU on one box, zero GPU.

## The question

`RolloutSearchBot` is a flat one-ply search: clone, apply a candidate action, let
HeuristicBot drive BOTH sides to a fixed horizon, score the leaf. So it cannot
discover a line HeuristicBot would not play out — the rollout policy IS the ceiling.
The standard route past that ceiling is to distil the search into a fast policy and
feed it back in as the rollout policy, then iterate. **That only compounds if a
stronger rollout policy actually produces a stronger search.** Nobody had measured
whether it does.

## What was built

`CastleDefense.Engine/Bot/RolloutPolicy.cs` — an `IRolloutPolicy` seam, a
`RolloutPolicyKind` enum, and `SavingHeuristicBot`: HeuristicBot that (1) invests the
instant it can afford to, (2) once past `commitFraction` of the way to the next
investment, stops offensive spending and delegates to the existing defence-only
profile, (3) otherwise plays normally. `RolloutSearchBot` takes `ownRolloutPolicy` /
`oppRolloutPolicy`, both defaulting to `Heuristic` so every existing caller is
unaffected. `search-test` gained `--rollout-policy`, `--opp-rollout-policy`,
`--save-commit`, and `--csv` (per-game outcomes, so two arms on one seed can be
paired for McNemar).

**Both wiring checks passed before any measurement.** The control arm reproduces the
pre-change run byte-for-byte (10970 decisions, 37,922,916 simulated ticks, 20.9%
overrides, 5.70/5.30 invests at seed 999 n=20), and the treatment differs on every
counter. Identical output across a config change would have meant the flag was dead.

## The premise check failed first: the candidate policy is WEAKER

Ladder, `--nostart`, seed 12345, 200 setups x 2 sides = 400 games/cell:

| contender | vs HeuristicBot | earned invests |
|---|---|---|
| HeuristicBot (control) | 50.2% [45.4, 55.1] | 6.96 |
| SavingHeuristic @1.00 | 48.5% | 6.91 |
| SavingHeuristic @0.70 | 47.2% | 6.88 |
| SavingHeuristic @0.50 | 46.0% | 6.92 |
| SavingHeuristic @0.25 | 42.2% [37.5, 47.1] | 7.00 |

Monotone DOWN in commitment strength. **A smoke run at n=50 showed this trend running
the opposite way (38% -> 50%)** — worth remembering as a live demonstration that this
benchmark's small-n noise exceeds the effects being chased.

**EARNED INVESTS ARE FLAT ACROSS EVERY ARM (6.96 -> 7.00).** Scripting "commit to
saving" does not make the bot invest more. HeuristicBot's existing
`PaceAttackSpendForInvestment` already extracts nearly all of it; the commitment only
makes it attack less, and less attacking is worth less than HeuristicBot's attacking.
**This is the third independent sighting of the same thing**, after the Armageddon
macro's unexplained 6.82 -> 6.86 at 7.3% firing and the `--no-macro` gap. It means the
save-invest macro's large contribution (75.0% vs 44.0% with it disabled) is probably
NOT coming from the economy, and the project does not currently know why its best
macro works.

## Probe A run as a dose-response instead

A stronger rollout policy could not be bought cheaply, so the same question was asked
on the axis available: if search is insensitive to an 8-point degradation of its
rollout policy, the coupling route 1 depends on is weak regardless of direction.

Shipped config (interval 15, horizon 300, margin 0.10), seed 4242, n=600 paired:

| rollout policy | its own strength | search win rate | delta | McNemar p |
|---|---|---|---|---|
| HeuristicBot (control) | 50.2% | **74.8%** [71.2, 78.1] | — | — |
| Saving @0.50 (own side) | 46.0% | 70.2% | −4.7 | **0.0042** |
| Saving @0.25 (own side) | 42.2% | 73.0% | −1.8 | 0.27 |
| Saving @0.25 (OPPONENT side) | — | 72.0% | −2.8 | 0.068 |

Control reproduces the recorded shipped figure (74.8% vs 75.0% [71.4, 78.3]), so the
instrument is calibrated.

**THE RELATIONSHIP IS NON-MONOTONE.** The WEAKEST rollout policy (@0.25) produced a
BETTER search than the middle one (@0.50): +2.8 points, b=33/c=50, p=0.078 on the
direct paired comparison. If search strength were a function of rollout-policy
strength the ordering would be preserved. It is not. Both perturbations cost
something, but not in proportion to how much they degraded the rollout policy.

**THE OVERRIDE RATE DOES NOT MOVE AT ALL: 8.21% / 8.28% / 8.16% / 8.23% across all
four arms.** Nor do the macro-selection rates (save-macro 4.5 / 4.2 / 4.3 / 4.4%).
This is the sharpest single number in the probe.

## Mechanism, and why it generalises

Search consumes only DIFFERENCES between candidate scores — both the argmax and the
override test are comparisons. Changing the rollout policy shifts every candidate's
leaf score in the same direction at once, so the ranking survives almost intact. An
unchanged override rate under a materially changed rollout policy is exactly what that
looks like.

In Go or chess a better rollout policy also serves as a prior over a branching tree,
so it changes which lines are REACHABLE. In a flat one-ply search there is no tree, so
it only changes score levels, which then cancel in the comparison. **Distillation
cannot work here as designed: feeding a better policy into the rollout moves all
candidate scores together and leaves the argmax where it was. The loop has nothing to
compound.**

## What this does not prove

The derivative was measured DOWNWARD, over an 8-point range, at n=600 (which resolves
~3 points). A large upward asymmetry is not formally excluded. Two things argue
against spending more on it: the dimension varied — save-versus-attack commitment —
is precisely the dimension the macros act on and therefore the most favourable axis
for coupling available; and the score-difference argument predicts insensitivity in
both directions from structure, not from this sample.

## Route 2's cheap probe, taken at the same time

`--opp-rollout-policy saving` changes only the ENEMY model inside the rollout. It cost
2.8 points (p=0.068). The candidate enemy model is weaker than HeuristicBot, so
"search became optimistic about the enemy and played worse" fits — which is exactly
the objection raised against using `HumanCloneBot` (which loses 95% to HeuristicBot)
as the rollout's opponent model. Partial evidence, one model, but it points the same
way: an opponent model that is correctly shaped but too weak makes search worse.

## Direction implied

What actually took this bot from ~50% to 75% was ADDING CANDIDATES — the macros — not
improving rollouts. Candidates change the option set the argmax chooses from; rollout
quality changes score levels that cancel. **The headroom is in the option space.**

Before building anything there, the invest paradox above should be explained, because
it says the project does not know why its best macro works.

Code is committed to the working tree but NOT to git — Marc's call.

# ============================================================
# 2026-08-07 — STAGE 0: what the save-invest macro's value actually IS
# ============================================================

Follow-on from Probe A. Probe A closed the rollout policy as a lever; this asks what the
one mechanism that DID work is actually doing, because the project's explanation for it
turned out to be untested. All arms n=600, paired, seed 4242, shipped config
(interval 15 / horizon 300 / margin 0.10), same build.

## The decomposition

| arm | win rate | macro firings/game | earned inv own/opp | units | spend |
|---|---|---|---|---|---|
| A1 shipped | **74.8%** [71.2, 78.1] | 25.18 | 6.93 / 6.12 | 224.8 | 31,276 |
| A2 `--no-macro` | 63.7% [59.7, 67.4] | 0 | 6.63 / 6.23 | 210.5 | 31,251 |
| A3 same macro, RANDOM timing @4.5% | 63.3% [59.4, 67.1] | 25.78 | 6.67 / 6.23 | 199.4 | 28,710 |
| A4 macro every decision | 0.0% (0W/600L) | all | 5.46 / 5.31 | 0 | 0 |
| B1 all three macros off | 63.7% | 0 | 6.75 / 6.22 | 221.1 | — |
| B2 random, affordability-gated | 64.7% | 6.9 (1.2%) | 6.67 / 6.24 | 211.1 | — |

Paired: A1 vs A2 −11.2% (b=88/c=21, p<0.0001). **A2 vs A3 −0.3% (b=36/c=34, p=0.905).**
A1 vs A3 −11.5% (b=87/c=18, p<0.0001).

## THE RESULT: the value is 100% in the SELECTION, 0% in the behaviour

Search fires the save-invest macro ~25 times a game and it is worth +11.5 points. Fire
the IDENTICAL macro the IDENTICAL number of times (25.78 vs 25.18) at random moments and
it is worth nothing. The behaviour is inert without the timing.

**The timing is not a writable rule.** Only 1.2% of decisions are ones where the
investment is already affordable, but search fires the macro on 4.5% — so roughly
three-quarters of its firings are on decisions where it CANNOT yet buy. It is choosing
when to HOLD money, not when to spend it. Gating random firing on affordability (B2)
recovers ~1 of the 11 points. This is also the post-hoc explanation for why Probe A's
`SavingHeuristicBot` failed: it scripted the affordable case, which is the third that
does not matter.

**Both prior hypotheses died.** "Attacked less" is backwards — the macro arm buys MORE
units than the no-macro arm (224.8 vs 210.5) at equal spend. "Invested more" is only a
third of the story (+0.81 vs +0.40 differential). The macro banks at the right moments,
compounds income, and funds MORE attacking later. It is sequencing, not a trade-off.

A4 is the mechanism check: fire it always and the bot buys literally zero units and loses
600 of 600, confirming the macro is survivable only because it is rare.

## CORRECTION: the 44.0% is a MARGIN 0.01 NUMBER and was never labelled as one

`--no-macro` at the shipped margin is 63.7%, not 44.0%, which tripped this session's own
stated abort criterion. A first hypothesis (the Armageddon macro did not exist when 44.0%
was measured) was WRONG — B1 turns off all three macros and still scores 63.7%. The
actual reconciliation, re-measured in this build:

| margin | overrides (on/off) | macros ON | macros OFF | macro worth |
|---|---|---|---|---|
| 0.01 | 26.2% / 17.8% | 68.5% (recorded 69.8%) | **43.8% (recorded 44.0%)** | +24.7 |
| 0.10 shipped | 8.2% / 3.8% | 74.8% (recorded 75.0%) | 63.7% | +11.2 |

Every recorded figure reproduces within noise. **The instrument is healthy** — the 44.0%
just describes a configuration the bot does not ship with.

**Consequence, and it inverts a standing claim.** At the shipped margin, search with NO
macros beats HeuristicBot 63.7% against ~50% for HeuristicBot self-play. The primitives
are NET POSITIVE there. "Search's primitive suggestions are net harmful" and "the macros
are the entire source of strength" are both margin-0.01 statements. The correct general
statement is the override-rate invariant: intervening rarely is good, intervening often is
bad, whichever kind of move it intervenes with — the same invariant Probe A found the
evaluator and the rollout policy obeying. The macro is worth +11.2 points at the shipped
config, not +31.

## Bug found and fixed: GameEngine.Clone shares mutable reference fields

`Clone()` uses `MemberwiseClone()`, so every reference-type field is SHARED with the
original until explicitly replaced. Diagnostic counters added as plain arrays were
therefore incremented by all ~231x of search's rollout simulation: the first smoke run
reported **92,686 units bought in an ~8,200-tick game**. Fixed (Clone now resets both
arrays) and a comment added at the MemberwiseClone call. Caught only because the number
was absurd on its face — a counter inflated 10% would have shipped. Worth auditing what
else Clone does not reset; `CloneCheck.cs` guards state divergence but would not catch a
diagnostic counter.

## Where this points

The lever is search's judgement about WHEN TO HOLD MONEY. It currently makes that call
from a 300-tick truncated rollout scored by a 6-feature logistic — and horizon is a
documented CLIFF (250 -> 30%, 200 -> 1.5%) precisely because below ~300 an investment does
not repay inside the rollout. So the decision that carries all of the bot's margin is
evaluated right at the edge where the estimator is known to break down. Play-to-completion
evaluation of macro candidates aims exactly there, and is affordable because macro
decisions are ~5% of decisions.

Not started — Marc's call.

# ============================================================
# 2026-08-08 — STAGE 1a: how wrong is search's hold-money estimate?
# ============================================================

Stage 0 showed the save-invest macro's entire +11.2 points live in WHICH decisions it
fires on, and that ~3/4 of firings are decisions where the investment is not yet
affordable — search is choosing when to HOLD money. Stage 1a asks whether that choice is
actually wrong, against play-to-completion ground truth. New BotArena mode `macro-truth`.
No training. ~1 hour CPU total.

## Two failed framings first, both instructive

**Per-decision regret is NOT well-posed for a commitment macro**, and the data says so
from both ends:

| truth definition | mean truth gap | implication |
|---|---|---|
| hold until affordable, UNBOUNDED | 0.19 | one decision swings 0.62 win prob — not credible |
| force ONE decision, real bot plays on | **0.0000** | the forced action is a no-op ~99% of the time |

The unbounded version reproduces the A4 saturation failure inside the measurement (holds
the purse to game end and dies with a full bank). The one-step version is a no-op because
Stage 0 is right: only 1.2% of decisions are affordable, so "fire the macro" almost always
means "hold", and holding once changes nothing. **The fix is a BOUNDED commitment window**
(`--commit-ticks`): commit for W ticks or until the investment lands, then hand back to
HeuristicBot — which is what the live bot does when the rollouts turn against saving.

## The result: the decision is BIMODAL

n=1219 sampled decisions, K=30 play-to-completion rollouts per branch, common random
numbers, shipped config, seed 4242:

| window | truth TIES (gap=0) | truth prefers macro | shallow chose macro | shallow regret | random floor | noise floor (K=15) |
|---|---|---|---|---|---|---|
| 75 ticks | **95.7%** | 2.2% | 4.3% | 0.01846 | 0.01995 | 0.00000 |
| 225 ticks | **91.9%** | 4.0% | 4.3% | 0.02832 | 0.03480 | 0.00000 |
| 600 ticks | **84.9%** | 4.7% | 4.3% | 0.03582 | 0.06960 | 0.00000 |

**At ~92% of decisions the choice makes literally no difference** — all 30 playouts of both
branches return identical outcomes. **At the remaining ~8% it is near game-deciding**
(median gap 0, p90 gap 1.0). Almost nothing in between.

**Search gets ~94% of decisions right and concentrates its errors on the decisive ones:
mean gap when shallow is wrong is 0.38 against 0.07 overall, 5.5x.** That is why its
regret (0.0283) sits close to a coin flip (0.0348) despite high accuracy.

The stated Stage 1a bar (disagreement >= 15%) was the WRONG bar — it assumed a smooth
distribution of stakes. Disagreement is 7.5%. The regret bar (>= 0.03) is met at the
longer windows. Recorded plainly because the criterion was set in advance.

## Affordability: ONE playout per branch is enough

Regret of a bounded-commitment truth estimator at budget m (W=225), ties broken to prior:

    m= 1  regret 0.00118   sign-errors  7 (0.6%)
    m= 2  regret 0.00077   sign-errors  4 (0.3%)
    m= 4  regret 0.00096   sign-errors  6 (0.5%)
    m= 8  regret 0.00052   sign-errors  4 (0.3%)

**HELD-OUT CHECK** (choose on seeds 1-15, score on independent seeds 16-30): estimator
regret **0.00022** vs shallow **0.02860** — a **131x** gap, 2 sign flips in 1219. The sign
is stable across independent futures, so this is not an artifact of scoring on the sample
that chose.

Because the gaps are 0 or 1 rather than finely graded, a single playout resolves them.
In-game cost is therefore ~2 extra full-length rollouts per decision (~8,000 ticks against
the current ~3,600), roughly **3x** — about 57ms per decision against the live game's 250ms
async budget. **Shippable, not merely benchmarkable.** The earlier worry that deep
evaluation would inherit the hold-forever pathology was right about the unbounded form and
wrong about the bounded one.

## THE CAVEAT THAT MATTERS

**0.028 per decision does not convert to a win-rate prediction.** Decisions are not
independent — changing one changes every subsequent state, and all of these states were
generated by the SHIPPED bot's play. Change the decision rule and the state distribution
shifts, so the estimator's measured accuracy is off-policy. This bounds the per-decision
headroom and establishes the signal exists. Only Stage 1b measures games won.

## Stage 1b, now concretely specified

Replace the macro candidate's shallow leaf with a bounded-commitment play-to-completion
rollout, m=2 (m=1 is enough on this evidence; 2 is cheap insurance), W=225 ticks, CRN, and
evaluate the PRIOR BASELINE the same way — scoring one by truth and the other by truncated
eval would compare two different estimators, the same class of error as the `--divergence`
phase bug. Everything else keeps today's behaviour. Measured by `search-test` n=600 paired
against the 74.8% control, McNemar, with worst-case decision latency reported.

Not started — Marc's call.

# ============================================================
# 2026-08-08 — STAGE 1b: deep macro evaluation. NEGATIVE.
# ============================================================

Ran the estimator Stage 1a validated: bounded-commitment play-to-completion for the macro
candidate AND the prior baseline, m=1, W=225, CRN, deep given authority over exactly the
macro-vs-prior question. n=600 paired, seed 4242, shipped config, both arms same build.

| arm | win rate | overrides | macro firings/game | earned inv |
|---|---|---|---|---|
| control | 74.8% (449/600) | 8.21% | 25.18 | 6.93 |
| deep m=1 | 75.8% (455/600) | 7.31% | 21.16 | 6.86 |

**+1.0 points, discordant b=38/c=44, McNemar p=0.58.** Fails the pre-registered bar
(beat control at p<0.10). Latency 232.6 ms/decision average, **1629 ms worst case**,
against the live game's 250 ms async budget — not shippable even had it won.

## The lesson: per-decision regret did not predict policy strength

Stage 1a measured a **131x** regret gap (held-out: 0.00022 vs 0.02860) and it bought
+1.0 points of win rate, indistinguishable from zero. The caveat recorded before the run
("0.028/decision does not convert to a win-rate prediction; decisions are not independent
and the states are off-policy") turned out to be the entire story rather than a footnote.
**Treat per-decision regret as a necessary-but-not-sufficient screen from now on.**

## Mechanism, and it is the same family as Probe A

Both estimators — shallow and deep — use **HeuristicBot as the continuation policy**. The
REAL continuation after the decision is the search bot. Extending the rollout from 300
ticks to game end does not fix that mismatch, it **amplifies** it: the estimate becomes a
more precise answer to a question about a game that is not the one being played. This is
why more playouts would not help either — m controls variance, and the error here is bias.

Probe A found search insensitive to rollout-policy STRENGTH; this finds it damaged by
rollout-policy MISMATCH once the rollout is long enough for the mismatch to compound. Both
say the rollout is a poor instrument for valuing a long-horizon commitment.

## Behavioural read

Deep vetoes (22.1/game) exceeded promotions (18.4/game): the "better" estimator made the
bot fire the winning mechanism LESS (25.18 -> 21.16 macro firings, overrides 8.21% ->
7.31%, earned invests 6.93 -> 6.86) and the win rate did not move. It changed the macro
decision ~40 times a game to net effect zero — consistent with Stage 1a's finding that
~92% of these decisions are ties, where intervening freely is harmless but pointless.

## Where the search stands now — four levers, three closed

| lever | verdict |
|---|---|
| leaf EVALUATOR | closed 2026-08-07, six directions, none beat deployed |
| ROLLOUT POLICY | closed by Probe A — insensitive and non-monotone |
| LEAF DEPTH / estimator quality | closed by Stage 1b — regret gap does not convert |
| the OPTION SET itself | **open**, and 1-for-3 historically (save-invest wins; press measured worse; Armageddon unproven at p=0.185) |

The shipped 74.8% configuration remains the best measured. Nothing was changed in it:
`--deep-macro` is opt-in and the control reproduces byte-for-byte.

Reusable from this arc: `macro-truth` mode, the deep-eval path, `--rollout-policy`,
`--macro-random-rate`, per-game CSVs for paired testing, and purchase counters.

# ============================================================
# 2026-08-11 — CAPABILITY MAP: the bot's worst loadouts, and why
# ============================================================

Marc's brief: the bot-vs-bot loadout table is contaminated as BALANCE data, but read as
a CAPABILITY map it localises what the bot cannot do. His hypothesis, to be tested and
not assumed: the bot's worst loadouts are the ones demanding sequenced multi-decision
execution, e.g. Blue's stall loop, with Blue+snipe+speed the worst measured cell.

**The hypothesis is confirmed in kind and relocated in specifics.** The dominant term is
not Blue and not snipe. It is **speed defence**, and it is the single largest capability
deficit in the game. No new simulation was run — this is analysis of the 2026-08-05
sweep plus the human record. Script: `CastleDefense.PythonAI/capability_gap.py`
(re-runnable, caveats in its docstring, so this does not become another pasted number).

## Method

Bot and human numbers are on different scales (the bot's is vs a field of bots, Marc's is
vs the bot), so they are compared on **within-agent deviation** — each agent's win rate
with option L minus that same agent's own overall rate:

    gap(L) = human_dev(L) - bot_dev(L)

Human: 114 games (the 11 quarantined abandoned rerolls excluded via the quarantine
folder's ids), Marc as P1 vs search or heuristic, 88.6% overall — which reproduces
CLAUDE.md's 58W/5L + 43W/8L exactly, so the exclusion is right.
Bot: the SearchMirror cells of `dashboard --bot search --mirror-games 12`, 1536 games,
shipped config, `headStart: false`, base-tier gadgets. Overall 48.2%.

## The result

| option | bot | Marc | bot dev | Marc dev | GAP |
|---|---|---|---|---|---|
| **speed (def)** | 23.7% (n=384) | 85.4% (41/48) | −24.5 | −3.2 | **+21.4** |
| **Blue (team)** | 29.7% (n=192) | 88.0% (22/25) | −18.6 | −0.6 | **+18.0** |
| Orange (team) | 44.3% | 100% (12/12) | −4.0 | +11.4 | +15.4 |
| Green (team) | 44.8% | 100% (9/9) | −3.5 | +11.4 | +14.9 |
| snipe (off) | 43.0% (n=384) | 90.9% (30/33) | −5.3 | +2.3 | +7.6 |
| Yellow (team) | 58.3% | 64.3% (9/14) | +10.1 | −24.3 | −34.4 |

**Orange and Green are ceiling artefacts and must not be read.** Marc is at 88.6%, so
his deviation cannot exceed +11.4, and both sit exactly there on 12 and 9 games. What
makes the top two credible is that they are driven by the BOT's deviation, not his:
his speed dev is −3.2 and his Blue dev is −0.6, i.e. neither option costs him anything
much. The ceiling cannot manufacture those.

**Tier confound, checked.** Marc's replays carry a time-machine headstart so his gadgets
are often upgraded, and speed upgrades are not a small difference — base 1.5x/90 ticks,
`speed_2` 2.0x/150, `speed_3` **10x**/300. Split by exact id: base 12/15 = 80.0%,
`speed_2` 22/25 = 88.0%, `speed_3` 7/8 = 87.5%. The tier-matched gap is therefore
**+15.9, not +21.4** (bot base speed 23.7% vs Marc base speed 80.0%), on n=15 with a
Wilson CI of [55, 93]. Still the largest single gap; the noisiest end of it. Base speed
does cost Marc something real (−8.6) — it is a weak-ish gadget for a human and a
catastrophic one for the bot.

## Marc's specific cell, and why the hypothesis needed relocating

Blue|snipe|speed is indeed 0/12. But of the bot's **8 worst cells out of 128, seven
contain speed**, across five different teams:

    0.0%  Blue firebomb|speed     0.0%  Blue snipe|speed
    0.0%  Orange firebomb|speed   0.0%  Red snipe|speed
    8.3%  Black snipe|speed       8.3%  Blue freeze|speed
    8.3%  Purple firebomb|speed   8.3%  Purple freeze|speed

Speed is negative for **all eight teams** (Purple −55.6, Black −43.1, Red −39.6,
Yellow −33.3, Blue −31.2, White −29.2, Orange −28.5, Green −1.4 against each team's own
other three defences). Blue is additively bad on top of it; snipe is a distant third at
+7.6. So it is a speed effect with a Blue effect stacked on it, not a Blue+snipe effect.

Green is the lone exception at −1.4, and that is not speed working for Green — Green's
other three defences are the worst in the game (45.1%), so there is nothing to fall from.

## THE MECHANISM, and it is exactly "sequenced execution"

`master_gadgets.csv`: speed is `Targeted=0`, +1.5x movement to ALL allied units for 90
ticks (3 s), $30, 5 s cooldown. **It sits in the Defense slot and has no defensive effect
whatever.** It is an offensive tempo tool — its value is compressing a wave's arrival so
the enemy cannot answer it piecemeal.

`HeuristicBot.TryUseDefenseGadget` (HeuristicBot.cs:3337):

```csharp
case "speed":
    if (myUnits.Any(u => !IsWall(u)) && BigSpendJustified(me, def, 0))
        used = TryCast(...);
```

**It fires whenever any non-wall friendly unit exists and the money is there — that is
"cast on cooldown".** It is by far the weakest condition of the four: heal gates on
`avgHpPct < 0.85`, wall on `inDanger` plus an enemy actually being present,
reinforcements on `DeferForInvestment`. Nothing in the speed branch looks at where the
units are, how many there are, whether they are mid-field or still at the castle, or
whether the 3-second window covers the approach. The bot buys a 3-second tempo burst and
spends it on whatever happens to be standing around.

**And search cannot repair this, for the reason Probe A already established.** Speed's
value is a sequence — spawn a wave, wait, boost as it crosses — and the search's rollout
policy is HeuristicBot, which fires speed on cooldown inside *every* candidate branch.
The waste is common to all candidates, so it cancels in the comparison, and no leaf score
difference ever points at it. This is the same score-differences-cancel mechanism that
closed the rollout-policy lever, seen from the other side.

## Why this matters more than it looks

The acceptance test rolls loadouts randomly, so **the bot draws speed in one game in
four**, and in those games it is playing at 23.7% against ~56.4% for its other three
defences. Naively that is worth ~8 points of overall win rate — larger than anything
else currently on the table, including the Armageddon margin.

It is also squarely inside the ONE lever still open. A speed macro is an option-set
change: a candidate that holds the cast until a wave is actually in transit. The option
set is 1-for-3 historically, but the previous three were economic; this one has a
measured deficit, an identified mechanism, and a clear firing rule to test against
"cast on cooldown".

**Not yet established, and worth one cheap run before building anything:** the sweep is
fixed-loadout-vs-random-field, so speed's 23.7% has never been measured head-to-head
(speed vs speed). Confirm the deficit survives a direct matchup before treating the 8
points as real.

# ============================================================
# 2026-08-11 — ARMAGEDDON MARGIN AT n=2400: NEGATIVE. The +1.9 was noise.
# ============================================================

The 2026-08-10 goal statement drops playability as a constraint, which re-opens exactly
one decision already in the tree: the Armageddon-commitment macro ships at margin 0.10
(fires 0.5%) rather than 0.0 (fires 7.3%) because Marc traded ~2 unproven points for a
more interactive opponent. A repo-wide grep found this is the ONLY playability-motivated
compromise in the codebase, so it is the whole of what the goal change unlocks.

It was recorded at n=600 as +1.9 points, 34 of 57 discordant, **p=0.185** — "positive
everywhere, proven nowhere". Resolving that needs ~4x the discordant pairs.

Both arms n=2400, paired setups, seed 4242, no headstart, same build, control =
`--margin 0.10` (which resolves every margin to 0.10, exactly what ships), treatment
adds `--arma-margin 0.0`.

| arm | win rate | overrides | arma firings | save-macro | earned inv own/opp | units |
|---|---|---|---|---|---|---|
| control (0.10) | **75.42%** (1810/2400) | 7.9% | 0.4% | 4.4% | 6.88 / 6.02 | 217.8 |
| treatment (0.0) | 74.38% (1785/2400) | 14.5% | 7.4% | 4.0% | 6.86 / 6.04 | 198.2 |

**Paired delta −1.04 points. Discordant b=129 / c=104, McNemar p=0.116, exact binomial
p=0.116. 95% CI on the paired difference [−2.29, +0.20].**

## The verdict: the sacrifice was not a sacrifice

The effect is not merely unproven, it has **changed sign** against 4x the data, and the
confidence interval now essentially excludes the +1.9 that was recorded. Marc gave up
nothing when he chose playability here. **Margin 0.10 is correct on strength grounds
alone, and the code comment telling future sessions not to "fix" this to 0.0 should now
say so for a second, stronger reason.**

Instrument checks both passed before reading anything into it:
- Control reproduces the recorded shipped figure: 75.42% against 74.8% / 75.0% recorded.
- The flag is live, not dead — arma firing 0.4% → 7.4% (recorded 0.5% → 7.3%), and every
  other counter moved too. Identical output would have meant a dead flag.
- Discordant fraction is stable with n: 57/600 = 9.5%, 233/2400 = 9.7%. The n=600 sample
  was simply small; nothing about the earlier measurement was broken.

## Two things this reinforces

**THE OVERRIDE-RATE INVARIANT, again.** Overrides 7.9% → 14.5% and the win rate falls.
That is now the fifth independent sighting: degraded evaluators in either direction, the
rollout policy, the deep estimator, and now the Armageddon margin all obey *intervening
rarely is good, intervening often is bad, whichever kind of move it intervenes with*.
Any future proposal that raises the override rate should be assumed harmful until it
measures otherwise.

**THE ARMAGEDDON MACRO STILL DOES NOT MOVE THE ECONOMY.** Earned invests 6.88 → 6.86
while firing 18x more often. The code comment flagged this as "one unexplained thing"
at 6.82 → 6.86; at n=2400 it is not unexplained noise, it is flat. A macro whose entire
stated purpose is committing to the eight-investment race changes investments by −0.02
and unit purchases by −19.6. **It is a defence-only-play macro that has been described
as an economic one.** The strategy-matrix result that motivated it (Armageddon tops the
dominance order) is not thereby refuted — but this macro is not delivering that strategy,
and that gap is worth remembering before the option-set lever is spent on a variant of it.

## Cost

~1h50m CPU on one box, no GPU, no training. The shipped configuration is unchanged.

# ============================================================
# 2026-08-11 — ASYNC STALENESS COST: none measurable. Assumption confirmed.
# ============================================================

The live bot snapshots the engine, thinks on a background thread, and applies the answer
when it is ready, so it always acts on a state some milliseconds old. The standing
assumption was that this costs little because the game moves slowly. It had never been
tested, and this project has a poor record with untested "seems fine" assumptions.

**It tests clean, with 15-30x of margin.** No change to the shipped bot.

## Instrument

New `--stale-ticks D`: decide from the state at tick T, commit the answer at tick T+D,
at most one decision in flight. Same structure as the live async path, but a
deterministic tick delay instead of wall clock — the live path's delay is not
reproducible, so it cannot be benchmarked directly. `Apply()` re-derives macro behaviour
from the state the action lands in, exactly as live. One tick = 33 ms.

`D=0` routes to the ORIGINAL code path rather than through the new one, deliberately:
`UpdateStale` clones and draws from `_rng`, so running it at 0 would perturb the stream
and the control would stop being the control.

**WIRING CHECK, both directions:**
- `D=0` in the new build is **byte-identical to the old build's control across all 600
  games** — same outcome, ticks, decisions, overrides, macro firings, earned invests.
- `D>0` arms differ substantially (37-53 discordant games each). The flag is not dead.

## The real operating point, measured rather than assumed

`search-test 20 --margin 0.10 --max-ms 250 --threads 1`, uncontended:
**15.5 ms per decision average, 34 ms worst case — on ONE core.** The live game gives
each decision `ProcessorCount - 2` = 18 cores and evaluates candidates in parallel, so
real latency is strictly lower. Either way the operating point is **under one tick**.

## Dose-response, n=600 paired per arm, seed 4242, no headstart, same build

| D | ≈ ms | win rate | delta vs D=0 | b / c | p (exact) | 95% CI on delta |
|---|---|---|---|---|---|---|
| 0 | 0 | 74.8% | — | — | — | — |
| 1 | 33 | 77.3% | +2.50 | 37/52 | 0.137 | [−0.58, +5.58] |
| 2 | 67 | 77.2% | +2.33 | 32/46 | 0.141 | [−0.55, +5.22] |
| 4 | 133 | 74.8% | +0.00 | 42/42 | 1.000 | [−2.99, +2.99] |
| 8 | 266 | 73.3% | −1.50 | 53/44 | 0.417 | [−4.72, +1.72] |
| 15 | 499 | 74.7% | −0.17 | 45/44 | 1.000 | [−3.25, +2.92] |

**Flat and non-monotone. Nothing reaches significance anywhere, including D=15 — a FULL
decision interval of staleness, 15-30x the real operating point.**

Every behavioural counter is flat too: overrides 7.8-8.4%, save-macro 4.3-4.5%, earned
invests 6.77-7.04, units bought 215.1-226.7, decisions/game 543-561.

Decisions per game does NOT fall at D=15, which is correct rather than suspicious: the
commit and the next decision land on the same tick (`_next` is set when the decision
STARTS), so cadence is preserved and only the staleness changes. That is also what the
live async path does.

## DO NOT CHASE THE +2.5 AT D=1

D=1 and D=2 both sit near +2.4 at p≈0.14, which is tempting. **This session already
watched exactly that pattern evaporate**: the Armageddon margin measured +1.9 at n=600
with p=0.185 and came back **−1.04** at n=2400 with the sign reversed. Same n, same
p-range, same shape. Treat a two-point effect at n=600 as unmeasured, and note that D=1
and D=2 are near-identical policies so their agreement is not independent corroboration.

## The honest limits of this result

1. **Staleness is modelled as CONSTANT; live it is variable** and grows late-game as
   candidate counts and unit counts rise. But the constant worst case at 15 ticks is
   already free, so variation inside that range cannot matter much.
2. **The opponent is HeuristicBot, which does not exploit reaction delay.** A human can
   bait a cooldown, feint, or time a wave against a known lag; Marc plays that way and
   HeuristicBot does not. This result bounds the cost against a non-exploiting opponent.
   It does not prove latency is free against Marc — and against him it is the *acceptance
   test*, not this benchmark, that would show it.

## Verdict

**There is no free win rate here.** The async design was a good call and stays. Cost:
~85 min CPU. Reusable: `--stale-ticks`, which is also the only way to benchmark any
future change to the decision-timing model.

# ============================================================
# 2026-08-11 — SEAT BIAS: a perfect mirror is decided by the engine, not the play
# ============================================================

Found while building the head-to-head defence duel, as its symmetry check. **The check
failed, and what it found is larger than the thing it was checking.** Reported before the
speed result because it governs how that result must be read.

## The finding

Same team, same offence gadget, same defence gadget, same signature, same bot on BOTH
sides — a perfect mirror — should be a coin flip, and really should mostly DRAW.
`mirror-fixed White nuke wall 100` (the project's own seat-fairness instrument, whose
comment says "a truly fair engine should land at ~50/50 within noise") returns:

    P1 wins: 0/100 (0.0%)   P2 wins: 100/100 (100.0%)   draws: 0

Reproduced independently in the new `defence-duel` harness, and it is **deterministic and
team-dependent**. HeuristicBot both sides, nuke/wall, n=40 per team, seats alternated:

| team | mirror outcome |
|---|---|
| Black, Red, White | **P2 wins 100%** |
| Purple, Yellow | **P1 wins 100%** |
| Green | P2 wins 80% |
| Orange | ~balanced (45/40) |
| **Blue** | **40/40 DRAWS — the only team that behaves correctly** |

Blue draws at 294.6s with zero tick-cap games, i.e. a genuine simultaneous finish. That is
what all eight teams should look like. Seven of eight instead hand a deterministic win to
one specific seat.

**It is NOT within-tick poll order.** `--p2-first` reverses which bot is polled first each
tick — the natural suspicion, since the second-polled bot sees the first one's action in
the same tick — and the winner does not move: P2 still wins 20/20 on White. Whatever the
cause is, it survives reversing the drive order, so it is engine-level (unit spawn
geometry / combat targeting resolution) rather than harness-level.

Games are genuinely distinct, so this is not one game repeated: `CreateGame` uses
`new GameState()` → `new Random()` and `new GameEngine(state)` → unseeded RNG.

## WHAT IT DOES NOT INVALIDATE, checked rather than assumed

**The dashboard sweep is protected.** `botIsP1 = i % 2 == 0` with `cellGames = 12`, so
every team x offence x defence cell is exactly 6 games on each seat and the seat term
cancels *within the cell*, not merely in aggregate. Its comment already says a side
asymmetry exists and that the sweep must alternate because of it. So the capability map's
speed numbers stand.

**The new `defence-duel` harness is protected** the same way, and demonstrably: wall vs
wall on White returns exactly 50.0% (A as P1 0/50, A as P2 50/50). The alternation cancels
a 100-point bias precisely, which is also why nothing downstream ever noticed.

**`search-test` is protected** — `searchIsP1 = g % 2 == 0`.

## WHAT IT DOES MEAN

1. **Any bot-vs-bot measurement that does not balance seats is worthless on this engine**,
   and the failure is silent because aggregate numbers look sane. This belongs with the
   measurement pitfalls in CLAUDE.md.
2. **Effective sample size in near-mirror configurations is far below n.** If the outcome
   is fixed by (team, seat), the games carry no information about the thing under test.
   Any A/B whose two arms are near-mirrors of each other should use a NON-mirror design —
   which is why the speed mechanism probe below is `speed-no-cast vs wall` rather than
   `speed-no-cast vs speed`.
3. **The 2026-08-03 "Make unit geometry seat-symmetric" commit did not finish the job.**
   Whether this is a bug worth fixing is a game-design question (balance work is deferred),
   but it is certainly a measurement hazard worth knowing about.

Not fixed. Diagnosing the engine-level cause is a separate piece of work and would change
game behaviour, which is not something to do mid-measurement.

# ============================================================
# 2026-08-11 — SPEED CONFIRMED head-to-head; and the DEFENCE GADGET IS NET NEGATIVE
# ============================================================

Marc asked for the head-to-head confirmation of the capability map's speed finding, since
the 23.7% came from a fixed-loadout-vs-random-field sweep. New BotArena mode
`defence-duel`: same bot, same team, same offence gadget, same signature on both sides,
differing ONLY in the defence gadget, sides alternated, paired setups.

## Q1 — the speed deficit is REAL and worse than the field number

SearchBot both sides, n=200 per arm, seed 4242:

| matchup | A win% | decisive-only | draws |
|---|---|---|---|
| **speed vs heal** | **0.0%** (0W/103L) | 0.0% | 97 |
| **speed vs wall** | **4.0%** (8W/122L) | 6.2% | 70 |
| speed vs reinforcements | 31.0% | 35.4% | 25 |

Speed won **zero of 200** against heal. Head-to-head with everything else held equal the
deficit is larger than the sweep reported, not smaller. Confirmed.

## Q2 — but the mechanism is NOT speed-specific

| arm | A win% | A earned inv | A units |
|---|---|---|---|
| speed vs wall | 4.0% | 3.65 | 154.4 |
| speed NEVER-CAST vs wall | 29.0% | 4.60 | 231.4 |
| wall vs heal | 18.0% | 4.21 | 155.0 |
| wall NEVER-CAST vs heal | 52.0% | 4.55 | 181.9 |

Never casting speed is worth +25; never casting WALL — the bot's best defence — is worth
+34. **The control moved more than the treatment**, so "the bot misuses speed specifically"
does not survive.

## THE DUEL'S MAGNITUDES ARE NOT TRUSTWORTHY, AND THE TELL WAS b=0

Both duel McNemars came back **b=0** (never-casting lost zero games that casting won,
winning 50 and 68). A perfectly one-directional effect over 200 games is a harness
signature, not a real effect. Three corroborating smells: 45-58% of games hit the 600s
tick cap, draws ran 25-97 per arm, and the seat splits were extreme (wall vs heal: 0% as
P1, 36% as P2). The duel is a NEAR-MIRROR — same team, same offence, same bot — which is
exactly the configuration where [seat bias] collapses effective sample size.

**So it was re-run on the calibrated instrument**, `search-test` vs HeuristicBot with
random loadouts, n=600 paired, both arms same build:

| arm | win rate | overrides | save-macro | earned inv | units | spend |
|---|---|---|---|---|---|---|
| control | 74.8% (449/600) | 8.2% | 4.5% | 6.93 | 224.8 | 31,276 |
| `--no-def-gadget` | **80.7%** (484/600) | 5.8% | 2.1% | 7.47 | 262.7 | 45,248 |

**+5.83 points, 95% CI [+2.24, +9.43], discordant b=43/c=78, exact p=0.0019.**

WIRING CHECK: the control is **600/600 byte-identical** to the previous build's control
arm, so the new flag perturbs nothing when off.

**The duel got the SIGN right and the magnitude wrong by 4-6x** (+34 vs +5.8). Note b=43
here, not 0 — that is what a real noisy effect looks like, and it is the contrast that
makes the duel's b=0 diagnosable in hindsight. Use `defence-duel` for direction only.

## What is established, and what is not

**ESTABLISHED: the search bot's defensive-gadget policy is worth LESS THAN ZERO.** Simply
never casting it is +5.8 points against HeuristicBot at p=0.002 — the largest single
measured gain since the save-invest macro. The freed money goes into BOTH economy and army
(invests 6.93 -> 7.47, units 224.8 -> 262.7, spend +45%).

**NOT ESTABLISHED, and each needs its own run:**

1. **Candidate-count confound.** Suppression removes action 12 from the search candidate
   list AS WELL AS disabling the prior/rollout cast, and the override rate fell 8.2% ->
   5.8%. Every lever in this project obeys "intervening less is better", so some of the
   +5.8 may be the candidate removal rather than the gadget. **Control: suppress action 11
   (offence gadget) instead and see whether that also gains ~6 points.**
2. **Is it HeuristicBot's policy or the gadget?** Never-casting is a floor, not a ceiling —
   a better casting rule could beat it. Test `DisableDefenseGadget` on HeuristicBot alone,
   with no search involved, to get a pure-policy answer free of the candidate confound.
3. **It is measured against HeuristicBot, which does not punish an absent wall.** Marc
   does. A bot that never casts a defensive gadget may be far more exploitable by a human
   than by this benchmark. This is the same limit as the staleness result and it matters
   more here, because this one is a shipping candidate.

## Consequence for the speed lead

The speed-specific macro is no longer the obvious next move. Speed is 1 of 4 defence
gadgets and never-casting helps wall MORE. The question has changed from "why can't the
bot use speed" to "why is its defensive-gadget spending net negative at all", which is a
bigger and cheaper target. Nothing shipped; `--no-def-gadget` is opt-in and the control
reproduces byte-for-byte.

# ============================================================
# 2026-08-11 — ATTRIBUTION: it is the CASTING RULE, and search casts it better than never
# ============================================================

`--no-def-gadget` bought +5.83 points (p=0.0019) but did THREE things at once: removed
action 12 from the search candidate list, and stopped the prior AND the rollout policy
casting. The override rate fell 8.2% -> 5.8% alongside the gain, and every lever on this
project obeys "intervening less is better" — so the gain might have been the candidate
removal. Marc called the control. Split the flag, ran each part, n=600 paired, seed 4242,
all arms same build.

**Wiring checks passed first:** plain control and `--sup def-cand,def-cast` are both
**60/60 byte-identical** to the runs they must reproduce, so splitting the flag changed
nothing.

| arm | action 12 a candidate? | prior/rollout casts? | win rate | delta | McNemar p |
|---|---|---|---|---|---|
| control | yes | yes | 74.8% | — | — |
| `--no-def-gadget` | no | no | 80.7% | +5.83 | 0.0019 |
| **`--sup def-cand`** | **no** | yes | 75.7% | **+0.83** | **0.405** |
| **`--sup def-cast`** | yes | **no** | **83.0%** | **+8.17** | **0.00001** |
| `--sup off-cand,off-cast` | offence | offence | 76.8% | +2.00 | 0.207 |

## THE CANDIDATE-COUNT CONFOUND IS DEAD

Removing action 12 from the candidate list ALONE buys **+0.83, p=0.405** — nothing. The
+5.83 was never about search intervening less. This also quietly bounds the
intervene-rarely invariant: it is not so strong that pruning any candidate helps.

## THE RESULT: HeuristicBot's defensive-gadget RULE is worth −8.2 points

`--sup def-cast` stops the prior and the rollout casting the defence gadget but **leaves
action 12 as a search candidate**, and it is the best arm measured: **83.0%, +8.17,
p=0.00001**. It also beats the full suppression head to head — **+2.33 points, b=11/c=25,
p=0.029** — and the only difference between those two arms is that search keeps the
option.

**So the gadget is not the problem; the rule is.** Search casting it selectively is better
than casting it on HeuristicBot's schedule (+8.2) AND better than never casting it (+2.3).
This is structurally the same finding as Stage 0's on the save-invest macro: the value is
in WHEN, and a cast-on-cooldown rule destroys it. Two independent mechanisms in this bot
now have that shape.

Mechanism is money: earned invests 6.93 -> 7.49, units bought 224.8 -> 262.5, spend on
units 31,276 -> 43,997. The money HeuristicBot was burning on defensive casts returns less
than units and investments do.

## Specificity: it is DEFENCE, not gadgets in general

Suppressing the OFFENCE gadget the same way buys +2.00 at p=0.207 — directionally positive,
unproven, and clearly smaller than defence's +8.17. The CI [−0.85, +4.85] does not exclude
a modest real offence effect, so this is "much smaller", not "zero".

## What this does to the speed lead

**Superseded.** The capability map found speed at 23.7% and the duel confirmed it
head-to-head (0/200 vs heal). But the deficit is not speed-specific: it is the defensive
casting rule, which speed happens to expose worst because speed is the one defence gadget
whose value is purely about timing. A speed-specific macro is the wrong shape; the target
is the defensive casting rule as a whole.

## Shipping candidate, with the caveat that matters

`--sup def-cast` = 83.0% vs the shipped 74.8%, +8.2 points, p=0.00001. Largest single
measured gain since the save-invest macro (+11.2). In Elo, 74.8% -> 83.0% is +191 -> +275,
so **+84 Elo** — real, and still small against the ~674 Elo the acceptance test needs.

**NOT YET MEASURED, and it gates shipping: how often does search actually cast the defence
gadget in this arm?** It must be non-zero (that is the only difference from full
suppression) but there is no counter for action-12 selection. Add one before shipping —
if it is very rare the bot is close to "never defends with gadgets", which is a
qualitatively different opponent to play against.

**And the standing limit applies with extra force here because this is a shipping
candidate: it is measured against HeuristicBot, which does not punish an absent wall.
Marc does.** A bot that rarely casts a defensive gadget may be much more exploitable by a
human than by this benchmark. The acceptance test is the only instrument that can answer
that.

Nothing shipped. All flags opt-in; control reproduces byte-for-byte.

# ============================================================
# 2026-08-12 — GADGET CASTING AUDIT: 6 of 16 fire on cooldown, and Marc's $20/s is already in the code
# ============================================================

Marc's brief after the def-cast result: find every gadget with cast-off-cooldown logic;
work out the right income level to start deliberately spamming for UPGRADE XP (twice,
L1->L2 and L2->L3); and stagger that spam so the three slots are never all on cooldown.

## 1. The audit — every branch in the three TryUse* methods

`BigSpendJustified(me, def, 0)` is the key: it is `cost < 0.8*money || income >= 50`, an
**AFFORDABILITY test with no usefulness component**, and it is permanently true from
investment 5 onward. Any branch gated only by it is cast-on-cooldown in practice.

| gadget | gate | verdict |
|---|---|---|
| snipe, nuke, firebomb, meteor, poison, blackhole | real value comparison vs target cost | OK |
| freeze | killValue / multiplier / buyTime / earlyStall | OK |
| divine | castle HP < threshold, or inDanger + mass | OK |
| heal | `avgHpPct < 0.85` + affordable | OK (shallow but real) |
| rage | non-wall unit + (inDanger or enemy within 250px) + affordable | OK (shallow but real) |
| **speed** | `∃ non-wall unit` + affordable | **CAST-ON-COOLDOWN** |
| **reinforcements** | `!DeferForInvestment` + affordable | **CAST-ON-COOLDOWN** |
| **cash** | **nothing at all** | **CAST-ON-COOLDOWN, and uncapped — see below** |
| **wave** | `(inDanger && budget) \|\| affordable` | **partly cast-on-cooldown** |
| **goo** | `healUseCase = units && enemies && affordable` | **partly cast-on-cooldown** |
| **wall** | `!alreadyHaveWall && (inDanger \|\| (enemies>0 && affordable))` | **partly cast-on-cooldown** |

The file's own comment at the `DeferForInvestment` definition names **reinforcements, wave
and goo** as the unconditional ones. **It missed speed, cash and wall.** Speed is the one
the capability map caught, and it is the worst of them because speed's entire value is
timing.

**`cash` is a special case and arguably a bug:** it has no condition AND passes
`double.MaxValue` as `estimatedEnemyValue`, which makes `paysForItself` true inside
`TryCast`, so it **bypasses the income-drain cap entirely** and spams from tick 0.
`divine` bypasses the same way but has a genuine trigger, so only cash is unguarded on
both axes.

## 2. MARC'S RULE IS ALREADY IMPLEMENTED, AND IT DERIVES HIS EXACT NUMBER

`TryCast` enforces a self-imposed minimum interval:

    minSeconds = cost / (income * GadgetMaxIncomeDrainFraction),   k = 0.30

so a gadget becomes castable **every cooldown** exactly when

    income >= cost / (cooldown * k)

For speed L1 that is `30 / (5 * 0.30)` = **$20/s — precisely the figure Marc gave from his
own play, arrived at independently.** The spam-to-upgrade behaviour therefore already
exists as an emergent consequence of the drain cap, and "what income should we start
spamming at" is really "what should k be".

Thresholds at the shipped k=0.30, against the income curve
(inv 0-8 = 2, 3, 4, 8, 20, 60, 252, 750, 2500):

| | income needed | reached at investment |
|---|---|---|
| L1 -> L2 | 4.2 (freeze) to 31.1 (goo); speed/rage 20.0 | **3-5** |
| L2 -> L3 | 40.5 (freeze_2) to 466.7 (goo_2); speed_2 300.0 | **5-7** |

Both phases are reachable in a normal game (the bot earns ~7 investments), so the
mechanism is live, not theoretical.

**Two defects in it as an upgrade mechanism:**

1. **It is a rate limiter, not an intent — it does not know what an upgrade is.** At max
   tier there is no `NextTierId`, so `AddGadgetXp` returns early and the XP is discarded,
   but the cap still permits casting: speed_3 needs `3600/(10*0.30)` = $1200/s and income
   at investment 8 is 2500, so the bot spams a maxed gadget at **$360/s for nothing**.
2. **It conflates tactical casting with XP farming.** Below the threshold it still permits
   a cast every `cost/(income*k)` seconds with no usefulness test at all — cast-on-a-timer
   rather than cast-when-useful. That is what the +8.2-point def-cast result was removing.

## 3. Staggering: NOT IMPLEMENTED

`_lastGadgetCastTick` is keyed by gadget FAMILY, so it only rate-limits each gadget against
itself. `Decide()` calls TryUseOffenseGadget -> TryUseDefenseGadget -> TryUseSignatureGadget
in sequence within one decision, so **all three slots can and do fire on the same tick**,
which is exactly the all-on-cooldown vulnerability Marc described. Nothing coordinates them.

## Design implied (not yet built)

Separate the two motives that the drain cap currently conflates:
- **Tactical cast** — requires genuine usefulness; fixes the six weak gates above.
- **Upgrade cast** — explicit and deliberate: only when `NextTierId != null`, only when
  `income >= cost/(cooldown*k)`, never while in danger, and **staggered at least S seconds
  from any other slot's cast** so one gadget is always available.

Tunables to measure: k (probably different for L1->L2 and L2->L3, since the L2 costs are
10-15x larger) and S. Nothing built or measured yet — reported first because finding that
Marc's rule already exists changes what should be built.

# ============================================================
# 2026-08-12 — GADGET DOCTRINE + XP FARMING: negative, monotonically
# ============================================================

Implemented Marc's gadget doctrine (flags 21-27) and measured it on the ladder, 400
setups x 2 sides, --nostart, seed 12345, paired against the default HeuristicBot in the
same run. All flags default OFF and verified **60/60 byte-identical** to the committed
bot before measuring.

## A BUG OF MINE FIRST, because it invalidated the first read

`TryUpgradeSpam` returns before the per-gadget switch, so it bypassed EVERY safety check
in it -- including `survivesOwnBlast`, the guard added after Marc's "I've seen the bot kill
itself with a nuke 3 times" report. NukeEffect damages BOTH castles by BaseValue/2
(100/1500/12000 by level) and the XP casts were aimed at our OWN castle, which is also
where our units stand.

**The ladder's DoNothing rung caught it: 83.5% against an opponent that takes no actions.**
Pinning the offence slot localised it in one run -- nuke 26.7%, freeze/snipe/firebomb all
100.0%. Fixed by repeating the nuke guard on the spam path, aiming XP casts at the ENEMY
castle, and refusing when any of our units are inside the blast radius.

**Lesson: DoNothing is normally a formality that reads 100% and gets skipped. It was the
only rung that could separate "this strategy is expensive" from "this code kills its own
castle" -- and without it the first result would have been reported as the former.**

## The k sweep, after the fix. MONOTONE, and the optimum is "do not do it"

| arm | OVERALL | vs HeuristicBot | earned inv |
|---|---|---|---|
| reference (no spam) | **87.4%** | **48.2%** | **6.96** |
| k = 0.04 | 87.3% | 47.7% | 6.94 |
| k = 0.08 | 86.7% | 43.8% | 6.82 |
| k = 0.15 | 86.5% | 43.2% | 6.56 |
| k = 0.30 (= Marc's $20/s for speed) | 83.4% | 32.8% | 6.11 |
| k = 0.30, defer at 25% of savings | 85.3% | 38.8% | 6.62 |

**Perfectly monotone in k, and the earned-invest column tracks it exactly** (6.94 / 6.82 /
6.56 / 6.11). k=0.04 is indistinguishable from the reference on all three columns, i.e.
the best setting is the one where the mechanism never fires. The deferral lever moves the
same way for the same reason.

**The failure mode is economic: gadget tiers are being bought with investments.** Earned
investment differential predicts win rate almost monotonically across ~20 configs in this
project, and it is the exact quantity the save-invest macro exists to protect.

## The casting doctrine, separately

`GadgetDoctrineNoSpam` (AoeTradeRule + DivineShieldsUnits + RageOnSiege + BlackholeBuyTime
+ SiegePreCast, no XP farming): **86.9% overall vs 87.4%, but 43.8% vs 48.2% head-to-head.**
Roughly flat overall, ~4 points down in direct play. Five bundled changes, so this could be
one real win cancelled by one real loss; not yet decomposed. `SiegePreCast` and
`AoeTradeRule` each touch 3 of 16 gadgets, so their ablations need `--offense`/`--defense`
pinning or they arrive diluted to noise.

## What this does and does not say

It does NOT say Marc's technique is wrong. It says a THRESHOLD RULE is the wrong way to
express it. Marc spams when he judges the cost irrelevant; k is a crude proxy that starts
speed at investment 5, well before the economy stops mattering. **This is the third
independent sighting of the same shape in this bot** -- the save-invest macro (Stage 0:
100% of the value is in WHEN, 0% in the behaviour) and the defensive casting rule
(`--sup def-cast`, +8.2 by letting search choose the moment instead of a rule). A
timing-sensitive action expressed as a threshold loses.

That also predicts the obvious next test: give the SEARCH bot the upgrade-spam option as a
candidate rather than giving HeuristicBot a rule. Same relationship as def-cast, which is
the one configuration where letting search pick the moment beat both the rule and never.

Caveat: measured against ladder opponents, none of whom are Marc. The HumanClone rung
degrades too (95.4% -> 86.1% at k=0.30), which is the closest available proxy, but gadget
tiers may matter far more against a human who punishes a low-tier loadout.

Nothing shipped. All flags remain default-off.

# ============================================================
# 2026-08-12 (later) — ABLATION: doctrine closed. XP FARMING AS A SEARCH CANDIDATE: POSITIVE.
# ============================================================

Two follow-ups Marc asked for before leaving: ablate the five casting changes with
different numbers, and give the SEARCH bot upgrade-spam as a candidate rather than giving
HeuristicBot a threshold rule.

## 1. Casting doctrine ablation — closed, and cleanly

Ladder, 400 setups x 2 sides, --nostart, seed 12345, each arm paired in-run against the
default contender. Reference: 48.2% vs HeuristicBot, 87.4% overall, 6.96 earned inv.

| arm | vs HeuristicBot | delta | earned inv |
|---|---|---|---|
| AoeTradeOnly (margin 1.0) | 46.8% | -1.4 | 6.91 |
| AoeTrade15 | 47.1% | -1.1 | 6.92 |
| AoeTrade20 | 47.4% | -0.8 | 6.93 |
| AoeTrade30 | 46.9% | -1.3 | 6.94 |
| DivineOnly | 47.4% | -0.8 | 6.86 |
| RageSiegeOnly | 47.6% | -0.6 | 6.94 |
| BlackholeBuyTimeOnly | 48.2% | **0.0** | 6.96 |
| SiegePreCastOnly | 46.8% | -1.4 | 6.88 |
| SiegeMin3 | 46.4% | -1.8 | 6.89 |

**The deltas sum to -4.2 and the bundle measured -4.4: purely additive, no interaction.**
There is no combination worth hunting for. Individually every CI overlaps the reference,
so the evidence is the CONSISTENCY OF SIGN across four arms plus the additive bundle, not
any single row.

**The AoE margin sweep is flat from 1.0 to 3.0**, so the trade rule does not fail by taking
bad trades -- the committed "never cast with an ally in radius" was already close to right.
`SiegeMinUnits` 3 is worse than Marc's 2.

**`BlackholeBuyTimeOnly` is UNTESTED, not neutral.** Identical to the reference on every
column across 5,600 games -- win rate, overall, earned invests, even avg seconds 262.7.
Identical output across a config change is this project's own bug signal. `bhBuyTime` sits
behind `earlyStall ||` in the same disjunction and earlyStall is already true in most
states with an enemy force present, so the clause probably never binds. Needs a byte-level
check before anything is concluded about blackhole buy-time.

## 2. XP farming as a SEARCH CANDIDATE: +1.17 points, p=0.015

New macro `MacroGadgetUpgrade` (103). It is a COMMITMENT, not a one-shot cast, and that is
forced by the evaluator: **gadget XP is not one of EvaluateBoard's six features**, so a
single XP cast is invisible-to-negative at the leaf and search would never choose it. The
rollout drives our side with a farming-enabled HeuristicBot for the whole horizon so the
upgrade LANDS inside it and the leaf prices the upgraded gadget's downstream effects. Same
reasoning as MacroSaveInvest and the Armageddon macro.

n=600 paired first, then resolved at n=2400 against the existing control (verified
600/600 byte-identical on shared setups, so only the treatment arm needed re-running):

| arm | fires on | overrides | win rate | delta | b/c | p |
|---|---|---|---|---|---|---|
| control | -- | 8.2% | 74.8% | -- | -- | -- |
| margin 0.10, n=600 | 0.4% | 8.4% | 76.3% | +1.50 | 11/20 | 0.150 |
| margin 0.00, n=600 | 6.8% | 14.5% | 74.0% | -0.83 | 36/31 | 0.625 |
| **margin 0.10, n=2400** | **0.3%** | 8.0% | **76.58%** | **+1.17** | **48/76** | **0.0150** |

**CI [+0.26, +2.08]. The effect survived 4x the data.** Recorded plainly because this
session's standing prior said it would not: the Armageddon macro showed +1.9 at n=600
(p=0.185) and came back -1.04 with the sign reversed, and the staleness +2.5 at p=0.14 was
noise. This one held. A small positive at n=600 is still not evidence -- but it is not
automatically noise either, and the only way to tell is to run it.

## THE HEADLINE: same behaviour, opposite sign, depending on who picks the moment

    XP farming as a HeuristicBot THRESHOLD RULE   -4.0 points (monotone in k; optimum off)
    XP farming as a SEARCH CANDIDATE             +1.17 points (p=0.015, fires 0.3%)

This is the cleanest demonstration yet of a pattern now seen four times -- the save-invest
macro (Stage 0: 100% of the value is in WHEN), the defensive casting rule (--sup def-cast,
+8.2), and now this -- because it is the SAME mechanism measured both ways in the same
build. **A timing-sensitive action expressed as a threshold loses; expressed as an option
search may take, it wins.** Marc's technique was right; the way it was first encoded was
not.

Margin 0.0 forcing it to 6.8% of decisions costs 2 points and doubles the override rate to
14.5% -- the intervene-rarely invariant's sixth sighting. Rare and well-timed is the whole
mechanism.

## Limit worth recording

The leaf evaluator has no gadget-tier feature. The commitment form means the upgrade lands
inside the 300-tick horizon so its effects DO reach the leaf, but any value of holding a
higher tier beyond that horizon is invisible. If gadget tiers pay off on a longer timescale
than the horizon, no margin tuning finds it and the fix is a seventh evaluator feature, not
a better macro.

## State

Shipped config unchanged; every flag opt-in. The two things that have now beaten the
committed bot are `--sup def-cast` (+8.17, p=0.00001) and `--upgrade-macro` (+1.17,
p=0.015), and **they have not been measured together** -- both change gadget behaviour and
could easily overlap.
