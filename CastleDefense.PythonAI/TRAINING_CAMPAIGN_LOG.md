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

---
*(Log continues below as the campaign progresses — periodic benchmark results,
plateau diagnosis if one occurs, and any further tuning.)*
