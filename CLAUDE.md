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
- **The engine cannot be cloned.** `_scheduledEvents` holds `Action` closures
  (GameEngine.cs:38) from 14 gadget-effect callsites; `_actionQueue` is a
  `ConcurrentQueue<Action>`. Any search/lookahead work needs these converted to
  data-based records first.
