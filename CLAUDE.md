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

## Loadout counter-picking (singleplayer)

Singleplayer's bot no longer rolls a random team/loadout. It picks SECOND, with the human's
choice already locked in, and now answers with a measured best response
(`CounterPicker`, `CastleDefense.Engine/Data/counter_table.csv`).

**The `dashboard` sweep cannot produce this table and never could.** It fixes the bot's
(team, offense, defense) and hands the opponent `AssignRandomLoadout`, then never writes the
opponent's loadout to the CSV. Its numbers are marginalised over exactly the variable a
counter table conditions on. `results.csv` says which loadout is good on average, not which
beats which; no re-analysis recovers the missing column.

### Pipeline

```
CastleDefense.BotArena.exe counter-matrix --games N [--pairs cells.csv] [--game-offset K]
python CastleDefense.BotArena/counter/analyze.py <sweeps...> --emit CastleDefense.Engine/Data/counter_table.csv
CastleDefense.BotArena.exe counter-eval --games N --fixed White,nuke,wall   # held-out check
```

Built 2026-08-18 from 466,944 HeuristicBot-vs-HeuristicBot games: a full 128x128 sweep at
n=16, plus n=200 refinement on the top 8 answers per human loadout.

**Three deliberate departures from every other harness here, all load-bearing:**

- **Seats are FIXED, not alternated.** Everything else alternates because it wants unbiased
  bot strength and the seat asymmetry is severe. Here the deployed configuration IS
  fixed-seat (`sp` always puts the human on P1), so alternating would average away an
  asymmetry the bot gets to keep. **The table is meaningless transposed.**
- **No headstart**, matching `sp`'s plain `new GameState()` at tick 0.
- **Common random numbers.** Game index i draws the same map, shadow roll and engine seed in
  every cell, because the setup seed comes from i alone and never from the loadout pair. The
  map is a real gameplay-affecting roll; without pairing a cell can look strong purely for
  having drawn friendlier maps. Two runs at the same settings are byte-identical.

### What it found

**Loadout choice is a bigger lever than the entire search programme.** Held-out, 76,800
fresh games, HeuristicBot both seats:

| bot seat | human seat | counter | random (old behaviour) | fixed White/nuke/wall |
|---|---|---|---|---|
| heuristic | heuristic | **99.94%** | 50.64% | 95.52% |
| heuristic | clone | 99.63% | 94.67% | 99.99% |
| search | heuristic | 99.09% | 82.73% | 100.00% |

**The average is NOT the reason to counter-pick — the holes are.** A single fixed loadout
averages 95.5% but has **5 deterministic 0% cells** out of 128: human loadouts that hard
counter it, where the bot loses every game (Black/firebomb/wall, Blue/firebomb/heal,
White/nuke/reinforcements, White/firebomb/reinforcements, White/freeze/wall). The counter
table's worst row is 92.5% and nothing is below 50%. Marc picks first and would find those
holes; that is what the table buys, not the +4.4 average points.

The +4.4 that counter beat fixed by against HeuristicBot **did not replicate** against
either other configuration (clone: -0.36, search: -0.91). Treat the fine-grained per-row
choices as HeuristicBot-specific noise; the robust content of the table is "play a strong
White loadout, and avoid the near-mirror that hard-counters your default."

Interaction is real but mostly lives in the losing half of the matrix: 41% of logit variance
is additive, and 44% is genuine interaction after subtracting binomial sampling noise — yet
the argmax collapses to **5 distinct answers across all 128 rows**, and
White/nuke/reinforcements answers 112 of them.

Marginal bot-seat win rate, stage 1 only (equal n=16 everywhere, so uncontaminated by the
refinement pass which only deepened the top cells):

| team | | offense | | defense | |
|---|---|---|---|---|---|
| White | **78.3%** | nuke | **65.9%** | reinforcements | 55.7% |
| Black | 59.7% | freeze | 53.3% | heal | 55.6% |
| Yellow | 57.1% | firebomb | 50.5% | wall | 55.2% |
| Blue | 54.8% | snipe | **33.9%** | speed | **36.9%** |
| Orange | 50.6% | | | | |
| Purple | 46.3% | | | | |
| Red | 33.2% | | | | |
| Green | **26.9%** | | | | |

Speed defence and Green/Red replicate the independent mirror-sweep balance findings, which
is a useful validity check on the whole instrument.

### What this table is NOT

- **It is not fitted against Marc.** Both seats are HeuristicBot. The absolute rates will not
  transfer to a human; only the ordering has any claim to. He beats HeuristicBot 92.1%.
- **It is P2-specific.** Fitted for the bot in seat 2 only.
- **The top-8 rows are near-ties at ceiling** (~99.8%), so rank 0 within a row is close to
  arbitrary. Do not read meaning into rank 1 vs rank 3.

Knobs live in `appsettings.json` under `CounterPick`: `Enabled` (false restores the random
roll) and `TopK` (1 = always the single best answer, maximum win rate and fully predictable;
higher samples among the top K and trades some win rate for unpredictability).

### `ForcedLoadout` — pinning the bot for mirror matches

`CounterPick:ForcedLoadout` ("Team,offense,defense", empty to disable) makes the bot play one
loadout every singleplayer game, bypassing both the table and the random roll. **Currently set
to `White,nuke,reinforcements`** so Marc can record MIRROR games: counter-picking makes the
bot's loadout a function of his, which confounds play with loadout in exactly the comparison
`--divergence` is trying to make. Pinning both sides removes that variable.

It is not a strength setting — it reopens the deterministic holes counter-picking closed.
Clear it for normal play.

**White/nuke/reinforcements is a genuinely clean mirror: `mirror-fixed White nuke
reinforcements 100` returns 100/100 DRAWS**, no seat advantage either way. This refines the
2026-08-11 seat-bias table above, which recorded "P2 always wins for Black/Red/White" — that
was measured at nuke/wall. **Seat bias is a property of (team, gadgets), not of team alone**,
so the per-team summary cannot be read as applying to every loadout of that team. It also
makes this particular pairing an unusually honest instrument: the mirror Marc will play has
no built-in seat edge to explain away a result.

## Chump-blocking (stalling big units with tier-1 bodies)

**The full written record is `CastleDefense.BotArena/stall/FINDINGS.md`**, next to the raw
CSVs and the report generator that rebuilds the published HTML from them. Deliberately NOT in
`bin/` — that path is what caused the 2026-07-14 data loss. What follows is the summary.

```
CastleDefense.BotArena.exe stall-test [--teams all|<T,..>] [--tiers 5,6,7,8]
    [--forces 0,1,..] [--force-gap 30] [--escorts 0,4] [--escort-gap 30]
    [--anchors 0,5] [--anchor-gap 150] [--anchor-delay 0] [--anchor-max 0]
    [--intervals 1,3,10,..]
    [--blocker mirror|all|<Team>] [--seat both|1|2]
    [--hp 23000] [--income 5000] [--protect-attacker true|false] [--csv out.csv]
```

The attacker gets a fixed force — N copies of one tier spawned `--force-gap` apart, optionally
with a lower-tier escort streamed in behind — and nothing else ever. The defender either does
nothing (the `interval 0` control) or feeds tier-1 bodies every N ticks. No gadgets, no
investing, no repairs. 22,700 runs measured 2026-08-21.

**The stall threshold is the attacker's ATTACK PERIOD, not its DPS or HP.** `MoveAndFight` sets
`CurrentSpeed = 0` and attacks the UNIT whenever any enemy is in range, so reaching the castle
requires `FindTargetsFast` to come back empty: one body in contact is a hard stop, not a damage
race. Every tier-8 unit has AttackSpeed clamped to 0.20/s, and across all 8 teams **one chump
per 5.00s holds to the 1200s horizon while one per 5.17s does not**. Price of delay against a
lone attacker: **$2.00/s**, 0.04% of the test income.

**Force size costs superlinearly.** `ClampToContact` skips FRIENDLY units, so a force does not
queue behind its own front member — all of it overlaps at one contact point and all of it
swings. Chumps needed per enemy swing: ×1 → 1.0, ×2 → 2.5, ×3 → 5.0, ×4 → 6.25, ×5 → 15.0.
Defence is still the cheaper side and increasingly so: 0.04× the attacker's spend against one
tier 8, 0.07× against five. Tier 7 is the hardest tier to hold.

**A tier-4 escort at one per second is a TAX, not a counter.** It multiplies the chump rate the
defender needs by 2-120x (median ~7x), but an escorted tier-5 force is still held for 6 chumps/s
and an escorted tier 6 for 10-15/s; only tier 7 in numbers needs the engine's ceiling. The escort
does genuinely escort rather than merely being a better attack: the escort stream ALONE is stopped
by 8/8 teams, and adding a single high-tier unit collapses that.

**A defender ANCHOR (a tier-5 woven into the chump wave every 5s, ~$12/s) is a matchup tool**, not
a general upgrade. Against escorted forces it is the cheaper buy in 5 of 12 cells and pure chumps
in the other 7; where it wins it roughly halves the bill ($60/s -> $32.5/s vs a single escorted
tier 7 or 8). It also doubles as a blocker, so the two effects cannot be separated: a T5 every 5s
with ZERO chumps already holds a single tier 8, because 5s IS the tier-8 swing period.

**MATCHING THE THREAT (a same-tier defender) is a niche counter, not a staple.** New flags
`--anchor-delay` / `--anchor-max` model it. 1v1, same tier both sides: the defender neutralises
the attack in ~2/3 of the 64 team pairings per tier (T5 40, T6 41, T7 41, T8 46), but about HALF
of those are mutual kills — matching often means trading, not winning, and every mirror pairing
is a mutual kill. Best defenders: T5 Purple, T6 Blue, T7 Blue/Orange, T8 White, all 8/8; worst:
T5/T6 Orange and T7 Green at 1/8.

Against a multi-unit force with the chump line already holding, one matched defender is the
cheaper buy in only **4 of 24** cells. It pays where the chump bill is highest (T7 x5 unescorted:
$60/s -> $31.8/s) and is catastrophic where the bill is trivial (T8 x1: $2/s -> $441/s, 220x
worse). The survival law explains it: a matched defender's value is KILLING, and killing is the
expensive way to buy what blocking gives cheaply — so it only earns its price against many
fast-swinging mid-tier units.

**Timing is not the lever it looks like.** Unescorted, sending the defender at 0/2/5/10/20s all
give infinite survival at >=3 chumps/s — it does not need the stack. Escorted, there is no
optimum, only noise.

### THE SURVIVAL LAW — use this rather than the tables

Binary "does it hold" is the wrong question for a real game: money is finite and 30 spawns/sec is
not reachable. What the bot needs is seconds bought per dollar, and that has a closed form.

```
t(r) = T_walk + K / (S - r)        survival at chump rate r
r    = S - K / (T - T_walk)        rate needed to survive T seconds
```

- **S** = sum of the attack rates of every enemy unit ON THE FIELD. Directly observable, and it
  absorbs escorts and mixed forces with no special case.
- **K** = castle HP / damage per swing (Siege doubles it; the one-shot floor in DamageCastle makes
  a tier 8 exactly 2 swings at 23,000 HP).
- **T_walk** = remaining distance / move speed.

Fitted on unescorted chumps-only runs: **median R^2 = 0.972**, recovered S within 4% and K within
6% of roster values, median survival-prediction error **-4.5%**. Out of sample, never fitted:
treating an anchor as nothing but a body arriving at 1/cadence moves the error from -17.5% to
**-3.7%**.

Two consequences worth building the bot's policy on:

- **Returns accelerate**: dt/dr = K/(S-r)^2 GROWS as r approaches S. Spending a little is nearly
  worthless. This argues for a THRESHOLD policy (match the swing rate or save the money), not a
  proportional one.
- **Chumps dominate per unit of blocking.** A body absorbs one swing whatever it is, so only price
  matters: tier 4 costs 10x a chump per unit of blocking, tier 5 30x, tier 6 166x. Buy a bigger
  body ONLY for what the blocking law does not capture — killing an escort that is eating the line.

Practical scale: three tier-7s take an undefended castle in 20s; $3/s of chumps makes it 43s,
$7.50/s makes it 165s, $10/s makes it never — against a force that cost the attacker $4,578.

The law models BLOCKING ONLY. Chumps also out-damage low tiers, so it is conservative against
tier 5, and it predicts the survival time much better than it predicts the exact critical rate.

### Four methodology rules this harness established

- **n is 8, not the run count.** The engine RNG only sets unit y-position, which nothing in
  combat reads — seeds 999 / 4242 / 12345 give 0 differing cells out of 80. Teams are the only
  sample dimension.
- **Run BOTH arms.** `--protect-attacker true` shields the attacker's castle and isolates
  *blocking* from the *counter-attack*; with it false the chumps kill the force and then raze
  its castle, winning outright in 8/8 teams at ≥6/s unescorted. Conflating the two reads the
  counter-attack as if it were stalling.
- **A censored run has NO finite survival time.** When the castle is never destroyed, the
  `seconds` field records when the attacking FORCE died, which is short. Averaging that into a
  survival median makes defence look worse the more you spend — it produced a spurious negative
  marginal return before it was caught. Treat non-`castle_destroyed` rows as +infinity.
- **A scheduled purchase the defender cannot afford must stay due, not be skipped.** A tier-8
  matched defender costs up to $23,000 against $5,000 of starting money, so a one-shot schedule
  failed silently and the first tier-8 duel matrix ran with no defender on the field (0/64).
  Check `anchors_spawned` before trusting any run.
- **The defence must keep buying while ANY enemy is present**, not merely while the high-tier
  units live. Gating it on the force alone let the defender stand idle against a surviving escort
  swarm, which inflated the escort's apparent strength by up to 120x and produced two withdrawn
  conclusions before it was found.
- **The response is NOT monotone in spawn rate near a threshold.** It depends on how the spawn
  period lines up with the swing period — at ×5 the line holds 8/8 at 15 ticks, 6/8 at 12, and
  8/8 again at 10. Quote the ROBUST threshold (slowest period where every faster period also
  holds), never the slowest period that happens to hold.
- **Seats were checked, not assumed**: 0/480 cells differ between `--seat 1` and `--seat 2`,
  and 0/80 with forces and escorts, so the seat bias documented below does not reach this
  scenario.

Two smaller facts worth keeping: at 23,000 HP a tier 8 is exactly a two-swing castle kill
(the one-shot protection in `DamageCastle` floors the first hit at 1 HP), so each blocked swing
is worth half the castle; and against tier 8 the tactic NEUTRALISES rather than kills, since
killing one needs ~2,000 bodies, past the 600s game limit.

**Unrelated balance finding the sweep turned up:** against an undefended castle a tier-4 stream
razes it in comparable time to a single tier 8 for a median **28× less money** — tier 4 moves
7–23 px/tick against a tier 8's 1–5, and its attack speed clamps at the TOP of the range (5.0/s)
where a tier 8's clamps at the BOTTOM (0.2/s). Untested against a defended castle.

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
