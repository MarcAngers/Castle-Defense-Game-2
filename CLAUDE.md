# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run Commands

**Build everything (Release):**
```
dotnet build -c Release
```

**Run the web game (single/multiplayer frontend):**
```
dotnet run --project CastleDefenseGame2
```
Serves at `http://localhost:5168`. The AI opponent loads from `CastleDefenseGame2/AI_Models/castle_defense_bot.onnx`.

**Run a single training arena (for debugging):**
```
dotnet run --project CastleDefense.Simulation -- 5000
```
Listens on port 5000 for a Python AI connection. Port is the only argument.

**Run the full 14-arena training cluster:**
```
cd CastleDefense.Simulation/bin/Release/net10.0
StartTrainingCluster.bat
```
Then in a separate terminal (from `CastleDefense.PythonAI/`):
```
ai_env\Scripts\activate
python train_ai_cluster.py
```

**Export trained model to ONNX (for use in the web game):**
```
python export_onnx.py
```
Output goes to `CastleDefense.Engine/Data/AI/castle_defense_bot.onnx`. The build system copies it to the output directories.

**Python virtualenv setup:**
```
cd CastleDefense.PythonAI
python -m venv ai_env
ai_env\Scripts\activate
pip install -r requirements.txt
```

## Architecture

### Data Flow: Training

```
StartTrainingCluster.bat
  → 14× CastleDefense.Simulation.exe [port 5000-5013]  (C# TCP servers)
       ↕ JSON over TCP
  train_ai_cluster.py (Python)
    → SubprocVecEnv (14 parallel CastleDefenseEnv instances)
    → MaskablePPO.learn()
    → ProgressCallback → training_progress.csv
                       → plot_training.py --watch (subprocess, live graph)
```

Each arena runs matches in a loop. On Python disconnect, it writes `training_stats_{port}.json`. The bat file waits for all 14, then runs `aggregate_stats.py` to produce `combined_training_stats.txt`.

### Data Flow: Web Game

```
Browser (JS/Canvas)
  ↕ SignalR (GameHub)
ASP.NET Core (CastleDefenseGame2)
  → GameHostingService  (manages active/lobby GameEngine instances)
  → AIBrain             (ONNX inference for singleplayer opponent)
  → GameEngine          (shared with training — same C# logic)
```

### C# Engine Layer (`CastleDefense.Engine`)

This is the core shared library used by both the web game and the training simulation. Key classes:

- **`GameEngine`** — owns the game loop. `Step(p1Action, p2Action, denseRewardWeight)` advances one tick and returns a `StepResult` with state vectors, action masks, rewards, `IsDone`, and `WinnerSide`.
- **`GameState`** — holds all mutable game state. `GetStateVector(side)` returns the 348-float observation; `GetActionMask(side)` returns a 14-int mask.
- **`GameDataManager`** — loads `master_roster.csv` (unit stats per team/tier) and `master_gadgets.csv` (gadget definitions) at startup.

### AI Observation & Action Space

**State vector (348 floats):**
- Teams: 16 floats (one-hot for P1 + P2 team, 8 colors each)
- Gadgets: one-hot for current offensive + defensive gadget tier
- Own castle stats: health, max health, log10(money), log10(income), **log10(InvestmentPrice)**, repair count
- Enemy castle: health + max health only (money/income are deliberately hidden)
- Own units: up to 50 × 3 floats (position [0=own castle, 1=enemy castle], HP%, tier/8)
- Enemy units: same format

`InvestmentPrice` replaced `InvestmentCount` (GameState.cs:116) — it's directly
actionable for savings decisions, and `InvestmentCount` is derivable from it plus
income. Gadget cooldown timers are still absent from the state.

**Action space (14 discrete):**
- 0: wait
- 1–8: spawn unit of that tier
- 9: invest (economy upgrade, increases income)
- 10: repair castle
- 11: offensive gadget
- 12: defensive gadget
- 13: signature gadget

Action masking blocks invalid actions (insufficient funds, gadget on cooldown, etc.).

**Decision cadence differs between training and evaluation.** Training decides every
**9 ticks** (`Simulation/Program.cs`, `for (int fi = 0; fi < 9; fi++)`). `AIModelOpponent`
and every BotArena mode decide every **3 ticks**, using greedy argmax rather than
sampling. Benchmark numbers for ONNX checkpoints are therefore measured at 3x the
decision rate the policy trained at.

### Reward Function (`GameEngine.CalculateReward`)

All values are divided by 100 before return. Current structure:
- **Time penalty:** −0.01/tick
- **Combat:** +1 per enemy killed, −1 per ally lost; castle damage dealt/taken is ±pct × 500 (damage taken scales up to 5× as HP approaches 0)
- **Economy:** +3000 per income increase; early invest bonuses (+800/+400/+150 for invests 1/2/3); −1000 when spending money while >60% of the way to next invest threshold; +savings progress reward when InvestmentCount < 4
- **Repair:** +healthDelta / 200
- **Gadget use:** +0.01 × denseRewardWeight (dense phase only)
- **Win/loss:** ±10000

Dense reward annealing runs over `total_anneal_steps` (set in `CastleDefenseEnv.__init__`; currently 12.5M).

### Training Opponents

`CastleDefenseEnv` picks a random opponent each episode from a pool that includes:
- `None` → Random Dummy (random valid action each step)
- Loaded `MaskablePPO` / `PPO` model files (league sparring partners)

The opponent's action frequency is throttled by `speed // base_speed` to simulate different APM levels.

### Game Data

- `master_roster.csv` — unit stats: one row per (team × tier). 8 teams × 8 tiers + special units.
- `master_gadgets.csv` — gadget definitions with upgrade chains (e.g. `snipe → snipe_2 → snipe_3`), cooldowns, costs, and effect parameters.

Both files live in `CastleDefense.Engine/Data/` and are copied to all output directories at build time.

## Measuring progress toward "a bot Marc cannot beat"

Win rate vs HeuristicBot is **partly self-referential**: HeuristicBot is also
RolloutSearchBot's policy prior AND its rollout policy for both sides, so that number
can rise by getting better at exploiting the bot's own simulator. Two instruments
exist to measure something HeuristicBot-exploitation cannot move.

**Where the target actually is** (verified 2026-08-07 against `game_records.db`,
excluding the 11 quarantined abandoned rerolls, whose rows are still in the DB):

| matchup | record | rate | Elo gap |
|---|---|---|---|
| Marc vs HeuristicBot | 58W/5L | 92.1% | +426 |
| Marc vs SearchBot | 43W/8L | 84.3% | **+292** |
| SearchBot vs HeuristicBot | — | 75.0% | +191 |

**ARITHMETIC CORRECTED 2026-08-11.** The middle row read +241 and the transitivity claim
read "within 8 Elo"; both were wrong. `400·log₁₀(43/8)` = +292 (+241 corresponds to
80.0%, not 84.3%). The ladder is transitive to **~59 Elo** (426 vs 292+191 = 483) — still
one consistent strength axis, but not the tight one previously claimed. The gap to close
is ~290 Elo, not ~240.

**The "11-0 vs SearchBot" figure is stale** — that was 2026-07-30 against the old
horizon-900 config. Against the shipped config Marc is 32-8.

**The acceptance bar is stricter than "large majority."** Ten games, pass if Marc wins 0
or 1: at a true 20% he passes it 37.6% of the time, at 15% 54.4%, at 10% 73.6%, at 5%
91.4%. Reliable passage needs Marc at ~10%, i.e. the bot ~+382 Elo above him — a ~674 Elo
swing from today, against +191 for the whole search programme so far. Do not read a single
ten-game run as a verdict in either direction. The test itself is the **Acceptance Test**
main-menu button (`gameMode: "accept"`); see `FLAGSHIP_BASELINE.md`.

### 1. `--divergence` — behavioural similarity to Marc, no play required

```
CastleDefense.Simulation.exe --divergence <recordings/singleplayer> <out.csv> \
    [--bot search|heuristic|clone|replay|none] [--interval N] [--half a|b] [--all]
```

Puts a bot in Marc's seat on his real replays and scores how differently it plays.
~2 min for the full search run over 141 games. Appends one row to `<out>_summary.csv`
per run for tracking. **Two headlines, and both must be read together:**

- `action_mix_tvd` — does it do the same THINGS (0 = identical mix)
- `timing_lift` — does it do them at the same MOMENTS (1.0 = no better than random
  timing at that bot's own action volume)

Current standings (holdout half, 70 games):

| shadow | timing_lift | action_mix_tvd |
|---|---|---|
| `replay` (oracle) | 55.39 | 0.0000 |
| `clone` | 3.39 | 0.0387 |
| `heuristic` | 3.22 | 0.3143 |
| `none` (control) | 0.00 | 0.1082 |

**Always run `--bot replay` after touching Divergence.cs.** It replays Marc's own
actions as the shadow policy and must score exactly 1.000 with zero divergence.
`--bot none` is the floor control and must score exactly 0.

### 2. `HumanClone` ladder rung — an opponent shaped like Marc

```
CastleDefense.Simulation.exe --export-policy-table <recordings/singleplayer> \
    CastleDefense.Engine/Data/human_policy_table.csv
```

Fits a conditional action distribution over `(investment count 0-7) x (enemy units
0 / 1-5 / 6+)` from Marc's games and writes it as a CSV that `HumanCloneBot` plays.
Re-run after recording new games; it is the only ladder rung that owes nothing to
HeuristicBot. HeuristicBot beats it 95.0% at n=300, and it earns 3.93 investments —
so it plays a real economy rather than spamming. Deterministic: seeded off the
engine's stream, so a ladder run at a given `--seed` is byte-reproducible.

**It is a conditional average of Marc, not Marc.** It cannot read a window, bait a
cooldown, or aim a gadget, and it is far weaker than he is. Its value is shape.

### Why neural BC is not the rung (measured, not assumed)

`--export-bc` records only ticks where the action id was non-zero, so it emits
**zero wait examples** — 0 of 8,109. Marc actually waits on **98.5% of ticks**. Any
policy fitted to that has never seen the wait label and will act on every decision,
i.e. become a spam bot. It also means the "69.3% action accuracy" that pipeline last
reported was measured on a label set with its majority class deleted. Fixing this
needs the exporter to emit subsampled wait examples plus class weighting, and it is
a prerequisite for BC being worth trying at all.

`bc_pretrain.py`'s `find_recordings_root()` also still points at the `bin/` paths that
caused the 2026-07-14 data loss — pass `--replay-dir` explicitly.

## Cleanup backlog

Deferred tidy-up work lives in `CLEANUP_BACKLOG.md` — stale comments, measurement tools
not yet re-audited, hand-maintained constants that drift, and dead code awaiting removal.
Add to that file rather than fixing opportunistically mid-task.

## Measurement pitfalls (read before trusting any benchmark number)

A 2026-07-28 audit found two of the two instruments it examined were broken. Both are
fixed, but the failure mode generalises: **code that produces a number once, which
then gets pasted into a document and never re-derived, has no feedback loop and rots
silently.** Treat any figure quoted in `TRAINING_CAMPAIGN_LOG.md` before that date
with suspicion.

- **A PERFECT MIRROR IS DECIDED BY THE SEAT, NOT THE PLAY.** Same team, same loadout, same
  bot both sides should draw. `mirror-fixed White nuke wall 100` returns **P2 100/100**.
  Measured per team (n=40 each, HeuristicBot, 2026-08-11): P2 always wins for Black/Red/
  White, **P1 always wins for Purple/Yellow**, Green 80% P2, Orange balanced, and **Blue is
  the only team that draws (40/40)** — which is what all of them should do. Reversing the
  within-tick poll order (`defence-duel --p2-first`) does NOT flip it, so it is engine
  geometry, not harness ordering. Consequences: (a) **any bot-vs-bot measurement that does
  not balance seats is worthless**, and it fails silently because aggregate numbers look
  sane; (b) in near-mirror configurations the effective sample size collapses, because the
  outcome is fixed by (team, seat) and carries no information — use a NON-mirror design for
  any A/B whose arms are near-mirrors. `dashboard` (6/6 per cell), `search-test` and
  `defence-duel` all alternate and are safe.
- **Headstart hands out free investments.** `CreateGame(true)` → `PlayerState(timeSkip)`
  calls `ApplyInvestmentStep()` `timeSkip` times, so both players start with
  `InvestmentCount == timeSkip` before either acts. `timeSkip = Math.Max(rng.Next(-8,9), 0)`
  ⇒ **E = 2.118**, SD 2.74. Always report invests as **earned (end − start)**, never as
  the raw end-of-game count. `invest-stats` now does this; older numbers in the log do not.
- **`EvaluateBoard()` is in the RL reward loop**, not just a diagnostic —
  `Simulation/Program.cs:446` feeds it to `batchEval` for N-step potential-based reward
  shaping. Changing `EvalWeight*` changes the training signal.
- **Evaluator weights are fit to the deployed `(w·x)/sum(w)` form** by
  `train_evaluator.py`. Do not reintroduce a no-intercept logistic on the raw [0,1]
  features — it is unidentifiable and forces `sum(w) == 0`. See `audit_evaluator.py`.
- **`calib_data.csv` has no `tick`/`game_id` column**, so autocorrelation thinning
  cannot fire for it. The C# `--collect-calibration` exporter should emit both.
- **Agreement metrics reward ACTING OFTEN, and it inverted a real comparison.** A bot
  firing a label in `p_b` of decisions and a human in `p_h` coincide in `p_h*p_b` of
  them with completely unrelated timing, so a spraying bot banks recall for free.
  Raw macro-F1 ranked HeuristicBot (acts in 33.6% of windows) *above* the fitted human
  clone (8.4%) while the clone matched Marc's action mix 8x better. Divide by the
  chance level at each bot's own action volume — that is what `timing_lift` is. The
  same correction is what makes such a metric fair to a stochastic policy at all.
- **Comparing two agents on different decision cadences is a phase trap.**
  `--divergence` used to score the human's PRECEDING window against the bot's decision
  at the current tick — conditioned on states that can never coincide. Marginal
  statistics survived it (they only re-bin the same actions) so it looked fine; every
  conditional statistic was meaningless. This is the second instance of this bug class
  in the project. The fix that catches it is an ORACLE: score the human's own actions
  as the shadow policy and require exactly 1.000.
- **Replay reconstruction was not deterministic.** Rebuilding a game from a `.replay`
  used an unseeded `new GameEngine(state)`, and the engine Rng drives unit y-position
  on spawn, which changes combat targeting — two runs of the same binary over the same
  replays disagreed on castle HP in 3 of 141 games. Now seeded from the game id
  (`ReplayFile.BuildStart`). Anything else reconstructing replays must do the same.
- **`.replay` never recorded gadget target positions**, only the discrete action id, so
  every reconstruction re-aims casts with the engine's auto-target. `--trace-human` was
  known to have this; `--divergence`, `--export-bc` and `--export-policy-table` all
  **inherit it**. Consequence: Marc's gadget doctrine (freeze/blackhole at the enemy's
  end) is invisible to every tool that reads replays, and a reconstructed trajectory
  diverges from the real game at his first cast. `gadget_uses` in the DB has the id and
  tick but still no position, so this cannot be repaired from existing recordings — the
  recorder has to change first.
- **12 of the 153 files in `recordings/singleplayer/` are league-watch bot-vs-bot
  games.** Anything treating seat 1 as "the human" must exclude them
  (`ReplayFile.SelectHumanGames` does, via `game_mode`). 141 are real human games.
- **The engine CAN now be cloned** — this entry used to say it could not, and that is
  stale. The PendingEffect refactor converted delayed gadget effects from `Action`
  closures to data records, so `GameEngine.Clone(rngSeed)` produces an independent copy
  (GameEngine.cs:143). It deliberately drops event subscribers, the queued input actions,
  and the RNG stream; `_scheduledEvents` (the legacy closure list) still exists and Clone
  THROWS if any are pending, but `ScheduleAction` has no callers. `RolloutSearchBot` and
  `--divergence` both depend on this working; `CloneCheck.cs` in BotArena is the guard.
