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

## The game must work on mobile, in LANDSCAPE

This game is meant to be playable on a phone held sideways. **Any front-end change has to
be verified at a mobile LANDSCAPE viewport before it is called done** — 812x375 is the
reference size. Check for text or UI clipping off the edges, page scroll, and controls
that have become too cramped to tap.

Portrait is not a supported layout and is not worth designing for: the game asks the
player to rotate instead (see below). Landscape is the target because it supplies the
width the pixel font needs — at 812 wide all 11 `h1` headings fit with room to spare,
while at 375 wide 8 of them overflow the screen.

**Height is the scarce resource in landscape, not width.** A landscape phone is only
375px tall, and the main menu already fills it to within 3px (last button ends at 372).
Anything that adds vertical space to a screen — a heading line, a button, a margin — is
what will break first. Check the bottom edge.

`index.html` sets `maximum-scale=1.0, user-scalable=no`, so the player CANNOT pinch-zoom
out to rescue a layout that overflows. Anything that runs off the edge is simply lost.

**If a requested change does not look right on mobile, say so** rather than shipping it
quietly. Report what breaks and offer the fix; do not silently redesign around it.

### Portrait lock

A touch device held in portrait gets a full-screen "ROTATE YOUR DEVICE" overlay
(`#rotate-prompt` in `index.html`, styled in `global-styles.css`) and the game underneath
is hidden. It is **pure CSS** — a `@media (orientation: portrait) and (pointer: coarse)`
query — so it re-evaluates on rotation with no JS, no listener and no state to get stuck.

Two deliberate details: the `pointer: coarse` gate is what keeps a merely narrow or tall
DESKTOP window from being blocked, since such a window matches `orientation: portrait`
too; and `#bgCanvas` / `#app-container` are set `visibility: hidden` under the same query
so nothing peeks past the overlay or catches a stray tap. On the way back to landscape,
`view.js`'s existing `resize` listener re-fits the canvas on its own.

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

### The browser wire format is NOT GameState

Added 2026-08-30. The loop broadcasts state to every client 30 times a second, and it used
to serialise the ENGINE's objects — every public field of every unit, most of which cannot
change (Width, Damage, Range, AttackSpeed, Weight, AttackType…), plus a Description string
and a serialised IGadgetEffect on each of the six GadgetDefinitions. It now sends
`GameStateWire` (`CastleDefenseGame2/Services/GameStateWire.cs`). Measured over 1,710
sampled ticks across three complete HeuristicBot games:

| | per tick | per viewer | 5-min game |
|---|---|---|---|
| engine state | 17,222 B | 505 KB/s | 148 MB |
| GameStateWire | 4,356 B | 128 KB/s | 37 MB |

**3.95x average, 6.49x at peak** (185,967 B → 28,663 B) — and peak is the number that
matters, because that is a busy field on a phone.

This is a hosting-cost change and a playability change at once. **Egress is the only
resource this game uses in quantity**: a whole game costs 0.14% of a CPU core, and even the
search bot at horizon 1600 single-threaded is 15% of one core, so the bill is bandwidth.
148 MB per game is also simply not playable on cellular data.

**Units are packed POSITIONALLY** — a bare JSON array, no keys. They are the only part that
scales with the battle (37 on an average tick, 158 at peak), and their fourteen key names
cost more per unit than the values. **The ordering is a two-sided contract**:
`UnitWireConverter.Write` in GameStateWire.cs, and `UNIT_FIELDS` in
`wwwroot/src/game-connection.js`. Change one without the other and every field after the
edit shifts by one slot. `expandState` puts the long names back at the single seam where
state arrives, so view.js, visual-unit.js, end-game-show.js and game.js are untouched and
know nothing about any of this.

**The trade is a hand-maintained allowlist.** Adding a field to GameState, PlayerState or
Unit no longer makes it visible to the client — it has to be added to GameStateWire too.
A new client feature that reads a state field and gets `undefined` is this.

Two things fell out of it: `PlayerState.ConnectionId` was being handed to the OPPONENT every
tick (the rejoin note below explains why anything on PlayerState is — the token is kept off
it for exactly this reason; the connection id had been missed), and `GET /api/games/{id}`
was returning the raw state to anyone who knew a game id.

**Compression is not available and was checked, not assumed.** gzip on the raw state
measures 6.2x, but permessage-deflate is reachable only through
`WebSocketAcceptContext.DangerousEnableCompression`, which applies to the raw WebSocket
middleware — SignalR accepts its own socket, and the whole of
`Http.Connections.WebSocketOptions` is `CloseTimeout` and `SubProtocolSelector`.

Remaining levers, in order: the six GadgetDefinitions (~857 B/tick, constant except on
upgrade — not cached client-side because a client that missed an upgrade would misprice a
button with no visible symptom), then delta encoding against the previous tick.

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

`InvestmentPrice` replaced `InvestmentCount` (in `GameState.GetStateVector`) — it's directly
actionable for savings decisions, and `InvestmentCount` is derivable from it plus
income. Gadget cooldown timers are still absent from the state.

**Action space (14 discrete, 0-13):**
- 0: wait
- 1–8: spawn unit of that tier
- 9: invest (economy upgrade, increases income)
- 10: repair castle
- 11: offensive gadget
- 12: defensive gadget
- 13: signature gadget

Action masking blocks invalid actions (insufficient funds, gadget on cooldown, UNIT OUT OF
CHARGES since 2026-09-01, etc.).

**Action 14 (auto-spawner) exists but is NOT in the action space.** `GetActionMask` still
returns 14 slots, so no policy can select it and every trained model, its observation vector
and every pinned bot-vs-bot benchmark are unaffected by the feature existing -- confirmed by
`bot-checksum --games 24` returning the same hash with and without it. The id is reachable
through `ApplyAction` anyway because recordings store one action byte per tick: without it a
human game in which the auto-spawner was bought could not be replayed, and every tool that
rebuilds a game by resimulating actions would silently diverge. Giving bots the auto-spawner
is a deliberate, separate change -- it means widening the mask and the policy head, which
invalidates the models.

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

### RE-MEASURED 2026-09-02 after charges, the auto-spawner and ChargeAwareFallback

`best-loadout` (new; see `CastleDefense.BotArena/BestLoadout.cs`) sweeps all 128 of the
BOT's own loadouts against a discriminating pool, seat-alternated, common random numbers,
n=100 specs x 2 seats per cell, two seeds:

```
CastleDefense.BotArena.exe best-loadout --games 100 --seed 12345
```

**`White,nuke,reinforcements` is rank 1 in both seeds** (100.0% / 99.5%). Ranks 2-3 in both
are the same team and defence with a different offence, so the offence slot barely matters
once White + reinforcements is fixed.

Marginals, both seeds (seed 12345 / seed 777):

| team | | offence | | defence | |
|---|---|---|---|---|---|
| White | **94.2 / 92.3** | freeze | 90.3 / 87.7 | reinforcements | **93.1 / 90.0** |
| Orange | 92.7 / 89.2 | nuke | 90.0 / 87.5 | wall | 89.5 / 85.9 |
| Black | 91.8 / 87.6 | firebomb | 89.1 / 86.1 | heal | 89.2 / 85.8 |
| Purple | 91.3 / 88.7 | snipe | **83.4 / 80.4** | speed | **81.0 / 79.8** |
| Yellow | **77.8 / 76.7** | | | | |

The ORDERING replicates the 2026-08-18 counter-matrix marginals (White best, Yellow/Green
worst, snipe worst offence, speed worst defence), which is a useful validity check on both
instruments. **The one real change is that reinforcements pulled AWAY from heal and wall**,
which were a near three-way tie before (55.7 / 55.6 / 55.2) and are now 93.1 vs 89.2 / 89.5.
That is the charge mechanic showing up exactly where the mechanism predicts: reinforcements
spawns free units with `ignoreCost`, so it is one of the only ways to put bodies on the field
without spending a charge.

Note the reinforcements builds earn FEWER investments than the wall/heal builds (4.61 vs
5.72) and still win more, i.e. they convert money into board presence faster than the economy
compounds it.

**BOTH SWEEPS ARE STALE FOR REINFORCEMENTS SINCE 2026-09-03** — it paid a FLAT FIVE units of
a fixed tier when they were measured, which is the payout the rebalance below removed. Its
93.1 / 90.0 and its 3.6-point lead over wall and heal describe a gadget that no longer
exists. The rest of each table is unaffected.

**`vsClone` is at a 100% ceiling for every top-15 cell and carries no signal** — HumanClone is
far weaker than Marc. Do not read that column as evidence about him.

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

White/nuke/reinforcements was chosen because `mirror-fixed White nuke reinforcements 100`
returned 100/100 DRAWS, no seat advantage either way. That refined the 2026-08-11 seat-bias
table above, which recorded "P2 always wins for Black/Red/White" — measured at nuke/wall.
**Seat bias is a property of (team, gadgets), not of team alone**, so the per-team summary
cannot be read as applying to every loadout of that team.

**STALE SINCE 2026-08-27 — THE LOADOUT IS NO LONGER ENOUGH TO PIN.** Map effects made the map
a gameplay input, and the same command now returns 14 / 23 / 63 draws because each game rolls
a different map. The draw was a KNIFE EDGE, not robustness: measured per map at n=40, it
survives on White, Blue, Orange, Yellow, Black and shadow-White, and collapses to a 40-0 sweep
on Purple (+10% speed, P1) and Green (−10% speed, P2), with Red (heal pulse) landing 21/12/7.

**This is not a side-dependent bug in the map code** — a control that rewrites every unit's
speed by ±10% from plainly side-independent code, on a map with no speed effect and without
touching MapEffects at all, breaks the same mirror the same way (40-0). Any perturbation of
unit speed tips the equilibrium into the seat bias the engine already has.

Consequence for the mirror recordings this section exists for: **pinning the loadout no longer
pins the rules** on its own, because the map varies game to game. Pin it too:
`appsettings.json` -> `Map:ForcedMap` (a team colour, empty for the normal random roll), read
in `Program.cs` and applied in `GameHostingService.CreateGame` -- the single place every hosted
game is born, so it covers multiplayer, singleplayer, league and practice alike. It also clears
`ShadowMap`, since a pinned map that is sometimes greyed out is not a pinned map, and the
server prints `[map] FORCED map active: ...` on startup as the reminder it is set.

**It is gameplay-affecting, not cosmetic**, for exactly the reason this section exists: while
it is set, every game is played under one map's rules, so nothing measured with it set is
comparable to anything measured with it clear. Pick one of the maps the mirror survives on
(White, Blue, Orange, Yellow, Black, shadow-White) and clear it afterwards.

## Map effects (the map is now a gameplay input)

Added 2026-08-27. Until then `GameState.Map` / `ShadowMap` picked which art the client drew
and NOTHING else. Every map now changes the rules, symmetrically for both players.

| map | name | effect |
|---|---|---|
| White | Calm Hills | +10% HP |
| Purple | Warehouse | +10% movement speed |
| Blue | Rainy Dock | −25% fire damage |
| Green | Marshy Swamp | −10% movement speed |
| Yellow | Sunbaked Desert | −10% damage |
| Orange | Rumbling Volcano | +10% fire damage |
| Red | Cherry Forest | every 10–30s, heal every unit 10–50% of max HP |
| Black | Distant Planet | knockback ×1.5 and DOUBLE flight time |
| *shadow* | Shadow Maps | +10% damage, multiplied on top of the underlying map |

`MapEffects` (Engine/Models) is the single source of truth. The numbers are HAND-SYNCED with
the Effect column of `wwwroot/assets/master_maps.csv`, which is the text the Collection screen
shows the player — the engine never reads that CSV, so changing one without the other makes
the game lie about its own rules.

**Every effect is applied at exactly one choke point**, which is what makes "no caller has to
remember" true rather than hopeful:

- **Spawn stats** (HP/damage/speed) in `SpawnUnit`. Every unit that reaches the field goes
  through it, so the Reinforcements squad, a Wall gadget's wall and the free opening squad all
  pick the effect up without knowing map effects exist. Applied AFTER the weirdo roll, so the
  two compose.
- **Fire** in `ProcessStatuses`, as the Burn tick lands — covering the firebomb's zone, the
  meteor's ignite and anything added later. Poison/Heal/Blackhole are deliberately untouched.
- **Knockback** where displacement happens in `MoveAndFight`, AFTER the anti-stunlock clamps,
  so low gravity throws units farther without reopening the stunlock those clamps close.
- **The heal pulse** in `ProcessMapHealPulse`, off `GameState.NextHealPulseTick`.

Four details that are load-bearing rather than incidental:

- **HP and damage round; speed does not.** Speed is a float everywhere in the engine and is
  already scaled fractionally by Slow/Speed statuses every tick. Rounding it would also make
  the effect wildly uneven — speeds run 1 to 23, so ±10% rounds to NO CHANGE for a speed-1 or
  speed-2 unit while moving a speed-5 unit a full 20%.
- **`MapEffects.ScaleStat` keeps 0 at 0.** `WallDefinition` sets Damage = 0, and a blanket
  floor of 1 would hand every wall a point of damage. Same trap the random-stat unit records.
  A multiplier of exactly 1 returns the input with no arithmetic, so a map without an effect
  is byte-identical to before this feature by construction.
- **Rounding is per application, so small numbers quantise.** A burn of 12 on Orange becomes
  round(13.2) = 13, i.e. +8.3% and not +10%. Unavoidable while damage is an int; it bites
  hardest on the smallest values.
- **The heal pulse draws from `Rng`, the seeded stream**, and ONLY on the Red map — so every
  other map's RNG sequence is untouched and existing per-map results stay comparable. One roll
  per pulse shared by every unit, walls included. The 1s "Heal" status it attaches has VALUE 0
  and is purely the visual marker: the client spawns heal particles from a status's name and
  ignores its value, and a non-zero value would be re-applied by ProcessStatuses every pass for
  the whole second.

**Black's doubled flight time is split across the server and the client and they must agree.**
The engine moves a knocked-back unit instantly and holds it under hard CC for
`MapEffects.KnockbackStaggerTicks`; the client animates the arc over
`VisualUnit.knockbackDuration` (view.js sets it per frame from the map). Draw the arc for
longer than the server staggers and units act while still drawn mid-flight. The re-knockback
immunity window is deliberately NOT doubled — it stops juggling, and doubling it would change
the map's stunlock economics rather than its gravity.

### What this invalidates

- **Every benchmark that does not pin the map**, in the same way the 2026-08-26 opening-squad
  change made everything before it stale. A sweep over random maps now averages eight rule
  sets. The clean-mirror claim in the ForcedLoadout section above is the first casualty.
- **The AI cannot see any of this.** The map is not among GetStateVector's 348 floats, so the
  trained policy plays every map identically and cannot learn map-specific play. Deliberately
  deferred: adding it grows the vector and invalidates every ONNX checkpoint. HeuristicBot
  reads GameState directly and COULD be made map-aware with no retraining; it is not.
- v3 replays already record Map and ShadowMap, so reconstruction reproduces the effects. v2
  replays do not, and already played on a different map than the recorded game.

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
race. Every tier-8 unit has AttackSpeed clamped to the roster floor, and across all 8 teams
**one chump per 5.00s holds to the 1200s horizon while one per 5.17s does not**. Price of delay
against a lone attacker: **$2.00/s**, 0.04% of the test income.

> **STALE FOR TIER 8 SINCE 2026-09-03.** The whole 22,700-run sweep was measured with
> `MIN_ATTACK_SPEED` at **0.20/s**; it is now **0.33/s**, one swing per 3.03s instead of 5.00s.
> Only the eight aces sat on that floor, so **every tier-8 number in this section and in
> `stall/FINDINGS.md` scales by 1.65x and no other tier moves at all**: the holding threshold
> goes 5.00s → ~3.03s and the price of delay $2.00/s → **~$3.30/s**. That is DERIVED from the
> survival law (`r = S` for infinite survival, and S is a sum of exactly these attack rates),
> not re-measured — the sweep has not been re-run. Force-size multipliers, escort behaviour and
> the anchor findings are ratios between tier-8 cells and should survive; the absolute dollar
> figures should not be quoted.

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
where a tier 8's clamps at the BOTTOM (0.33/s since 2026-09-03, 0.2/s when this was measured).
The 28x is therefore an overstatement now — the spread between the clamps narrowed from 25x to
15x — but the direction is unchanged. Untested against a defended castle.

## Disconnection, rejoin, and WINS BY DEFAULT

Closing the tab, reloading, crashing or dropping the network used to be an instant loss:
players were identified by their SignalR ConnectionId, which is per-socket, so a returning
browser matched neither seat and could not act -- while the game kept ticking, undefended.
There was no way back into a game at all.

```
browser  --token (localStorage)-->  ReconnectService  <-- pause/resolve --  GameHostingService
```

- **Identity is a TOKEN, not a socket.** `ReconnectService.RegisterSeat` mints one per human
  seat, sends it to that browser alone (`SessionToken`), and the browser keeps it in
  localStorage. `RejoinGame(gameId, token)` re-points `PlayerState.ConnectionId` at the new
  socket. **The token is deliberately NOT in PlayerState**: the loop broadcasts the whole
  GameState to the group every tick, so anything stored there is handed to the opponent.
- **A game with an empty human seat is not stepped at all** -- no tick, no bot decisions, no
  recorded actions, no state broadcast. The remaining player sees a pause overlay with a
  countdown broadcast BY THE SERVER once per second, because the server's deadline is the
  one that actually ends the game.
- **Actions are dropped while paused, not queued.** `GameEngine.Tick` drains the action
  queue, and a paused game is not ticked, so without the guard in GameHub every click made
  during the overlay would fire in one burst on resume.
- **60 seconds (`ClaimAfterSeconds`) is when the win becomes CLAIMABLE, not when it is
  taken.** Ending the game automatically would force a result on someone who would rather
  wait for their friend's router to come back, so at 60s the waiting player is offered a
  **Claim Win** button and the game stays paused until they press it. Three things resolve a
  pause instead: the claim, the missing player pressing **Abandon**, and the
  `MaxPauseSeconds` ceiling (30 min) that stops a doubly-abandoned game living forever.
- **A pause with nobody connected resolves as soon as it is claimable**, because no one
  could ever claim it. That is every singleplayer disconnect: exactly one human still
  connected wins **by default**, nobody connected is **abandoned** with no winner -- so the
  bot is never handed a win, there being no human to award it to.
- Spectator modes (`league`, `defwatch`) get no token, so a spectator closing their tab
  pauses nothing. A lobby that has not started is discarded rather than paused.
- **A defaulted game gets the NEUTRAL end-game show** (`game-over.js` passes winner 0 to
  `endGameShow`) whether or not it has a winner: no castle fell and nothing on the field was
  decided, so the armies mill about rather than one side celebrating an opponent who left.
  Only the scoreboard knows there was a winner.

### The storage trap this hit twice

The browser side is per-SEAT, not per-browser, and both bugs here were the same shape.

`localStorage` is shared by every tab on the origin. A single session key meant that with
both seats of one game open in one browser -- exactly how this gets tested -- the second
join OVERWROTE the first, so player 1 reloaded, was handed player 2's token, rejoined into
player 2's SEAT, and lost the game they were winning while their own seat sat empty. Sessions
are now a LIST keyed by (gameId, side), with a `sessionStorage` pointer naming which entry
belongs to THIS tab -- per-tab and reload-surviving, which is the one distinction
`localStorage` cannot make. `CheckRejoin` additionally refuses a seat that is currently
connected, so even a tab that lost its pointer skips the occupied seat and finds the empty one.

The second bug was the fix's own cleanup pass: a page load asked about another tab's session,
got "not valid" because that game was still in a LOBBY, and **deleted a live session belonging
to a different tab**. A tab may only prune its OWN seat. Anything touching these keys has to
assume other tabs are using them concurrently.

### A WIN BY DEFAULT IS NOT A WIN -- exclude it from analysis

`games.end_reason` is NULL/`"normal"` for a game decided by play, `"disconnect"` for one
awarded because the loser never came back, and `"abandoned"` for one nobody came back to.

**Every tool that reads recordings must exclude `disconnect` and `abandoned` games unless
Marc explicitly asks for them.** `ReplayFile.SelectHumanGames` already does, via
`ReplayFile.IsRealResult`, and prints how many it dropped; `--all` is the deliberate
opt-in. Anything new that reads `game_records.db` or the replay folder has to make the same
check -- and must make it against the DB, because **nothing in the replay file marks
either case**: the winner byte of a default win is byte-identical to an earned one, the
action stream merely stops early, and a game that paused and resumed looks exactly like one
that never paused.

The reason is not bookkeeping tidiness. Such a game measures a network, not a player: its
duration is however long someone played before their wifi dropped, its ending was not
fought, and counting it drags win rate, game length and earned investments toward whatever
a disconnection happens to look like. The human play record in this file
(92.1% vs HeuristicBot, 84.3% vs SearchBot) is exactly the kind of number that would rot.

### AN AGENT'S OWN GAMES MUST NOT ENTER THE HUMAN CORPUS

`recordings/` is Marc's play record. `--divergence`, `--export-policy-table`,
`--analyze-actions` and every human win-rate number read it BY PATH, and `game_records.db`
sits in the same folder. A game played by an agent driving a browser to test a feature is
indistinguishable from one of his: same `game_mode` (`sp`), same `opponent_type`, and it
lands in the same folder and the same database. It then counts as one of his losses.

**`--RecordingsDir` moves the whole recordings root**, replay files and database together
(`RecordingPaths`, read by both `Program.cs` and `GameHostingService`). Half-separating them
would be worse than not separating at all -- files in their own folder but rows still in
`game_records.db`, which is where the win-rate queries actually look.

`CastleDefenseGame2/.claude/launch.json` passes `--RecordingsDir=recordings-agent`, so any
server started through the agent preview tooling is redirected and prints
`[Recordings] REDIRECTED ...` at startup. Marc's own runs pass no flag and are unaffected.

**Two traps, both hit while building this:**
- **There are TWO `.claude/launch.json` files** -- one at the repo root and one in
  `CastleDefenseGame2/`. The tooling reads the one under the working directory, which is
  `CastleDefenseGame2/`. Editing the root copy alone does nothing.
- **That config runs `dotnet run` without `-c`, i.e. Debug.** A `dotnet build` without
  `-c Release` is what it picks up; building only Release (or only Debug) and testing the
  other silently runs stale code.

Check the startup line before playing anything. If it is absent, the games are going into
the human corpus.

## The random-stat unit (Black tier 4, "weirdo")

One unit does not read its stats off its roster row. Every time a `weirdo` spawns,
`GameEngine.SpawnUnit` rolls a uniform multiplier in [0.5, 2.0] for **health, damage and
speed** independently, and sets `Unit.VisualScale` to the **mean of the three** -- so it is
drawn anywhere from 27px to 99px around a 50px base and the size advertises the roll.
Keyed on `GameEngine.RandomStatUnitId`, the same way the "monky" half-health case is.

### SIZE IS APPEARANCE ONLY, AND THAT IS NOT A SHORTCUT -- IT IS THE FIX

The first cut scaled the unit's real `Width`/`Height`, and it broke combat. `ClampToContact`
stopped a unit using its INSTANCE width while `FindTargetsFast` measured reach from the
DEFINITION's, so an 83px weirdo halted 33px short of what its own targeting believed it
could reach and **stood there never attacking**, while its opponent hit it without being hit
back. `FindTargetsFast`'s own doc comment already warns that this error is signed and runs
the opposite way for each seat.

**The engine is not size-aware and was never designed to be**: half of it reads
`UnitDefinition.Width`. So the logical size of every unit is now always its definition's, and
only the sprite scales. Scaled sprites therefore do not line up with where the unit actually
fights -- a deliberate, cheap trade.

**The invariant to protect is `unit.Width == def.Width` for every spawned unit.** That is
what a guard should assert; the downstream "can they hit each other" symptom does NOT
reliably reproduce (verified by reinstating the bug -- a head-to-head damage test still
passed, while the width invariant caught it immediately).

### What IS per-instance

`Unit.Damage` and the new `Unit.BaseSpeed` are read by the combat loop instead of the
definition. `BaseSpeed` exists because `CurrentSpeed` is the LIVE value -- MoveAndFight
zeroes it in contact and rewrites it every tick -- so it cannot hold a stat. For every
ordinary unit instance and definition are equal by construction, so the distinction is
invisible until it is wrong. `ScoreUnit` in HeuristicBot still takes a definition on
purpose: it asks "how good is this unit type to buy", which has no instance, and so prices a
weirdo at its base row rather than its 1.25x expected roll.

Two traps worth keeping:

- **The rolls come from `Rng`, the engine's seeded stream.** An unseeded `new Random()`
  would break replay reconstruction and search-rollout determinism at once -- the exact bug
  class the measurement-pitfalls section already records twice. `clone-check` passes.
- **Every other unit takes its definition's values verbatim** -- no multiply, no rounding, no
  clamping, and the RNG is not drawn from at all. A blanket `Math.Max(1, ...)` damage floor
  would have handed every WALL 1 damage (`WallDefinition` sets `Damage = 0`), quietly turning
  defensive scenery into an attacker.

### Consequence for measurement: BLACK MIRRORS ARE NO LONGER DETERMINISTIC

A perfect mirror used to be decided entirely by seat geometry (see the seat-bias entry
below). Black now fields a unit with random stats, so that tie is broken by real variance.
`mirror-fixed Black nuke reinforcements 100`, HeuristicBot both seats, all three arms run in
the same build with only the feature toggled:

| arm | result | avg length |
|---|---|---|
| randomisation off (control) | P2 100/100 | 366.2s |
| stats + logical size (BUGGY, withdrawn) | P1 87 / P2 13 | 285.6s |
| stats only, size visual (shipped) | **P1 19 / P2 75 / 6 draws** | 339.9s |

**The 87/13 was the collision bug, not a balance finding** -- it is retracted. With the bug
fixed the mirror sits near the control's P2-favoured bias, with variance breaking some of
the determinism, which is what adding noise to a knife-edge should look like.
`mirror-fixed White nuke reinforcements 100` returns 100/100 draws at 298.6s in every arm,
confirming non-Black play is untouched.

**The seat-bias table below is stale for Black**, and any Black measurement now needs a real
sample size where it previously needed n=1. Whether Black's overall strength moved against
other teams has not been measured.

## Game opening: pre-game, the opening squad, and starting money

Added 2026-08-26. Two of these three are BALANCE CHANGES, not presentation.

**Pre-game (presentation only).** `GameHostingService.PreGameSeconds` = 4 seconds during
which the game exists and is broadcast but is **not stepped** -- no tick, no bots, no
recorded actions -- and the hub refuses actions exactly as it does during a disconnect
pause. The client opens the camera on the OPPONENT's castle, holds a second, pans home over
two, settles for one, and drops a "3 / 2 / 1 / BATTLE!!" banner over the last three.

The remaining time is pushed from the server every loop pass and the client anchors a local
deadline against it, so the pan interpolates smoothly between updates and a browser that
joins mid-intro lands in the middle of it rather than missing it. **In multiplayer both
browsers must open the battle on the same tick**, which is why none of this is client-timed.
Spectator modes (`league`, `defwatch`, `watch`) get no pre-game -- there is nobody to
introduce the battle to.

**The opening squad (BALANCE).** `GameEngine.OpeningSquadSize` = 5 free tier-1 units per
side, spawned one per second on ticks 1, 31, 61, 91, 121 -- so the first runs on as the
battle opens rather than after a second of nothing. Spawned with `ignoreCost`, which keeps
them out of the action recording, the purchase counters and the money-spent totals: nobody
decided them, and a replay that recorded them as spawn actions would replay them twice.

Keyed on absolute `CurrentTick`, so a league game (which starts at `30*30*timeSkip`) gets no
squad. The client's decorative crowd derives its count from the same tick rather than a
local timer, so the units standing outside the castle empty onto the field exactly in step.

**Starting money (BALANCE).** `PlayerState` now starts at **$10**, not $0.

## Reinforcements pays a VALUE BUDGET, not a fixed squad

Rebalanced 2026-09-03, on the `balance-updates` branch. It used to spawn a flat **five units
of tier `BaseValue`** — so reinforcements_3 handed over five tier-7 units, **$10,330 of White
roster value for a $3,000 cast**, and reinforcements_2 five tier-5 for $405 at $180. The
payout was untethered from the price.

`BaseValue` is now an **EFFICIENCY MULTIPLIER**, not a tier, and `BaseLabel` reads
`EFFICIENCY` instead of `UNIT TIER`:

| level | cost | multiplier | budget |
|---|---|---|---|
| 1 | $12 | x1.33 | $16 |
| 2 | $180 | x1.5 | $270 |
| 3 | **$2,000** (was $3,000) | x3 | $6,000 |

The budget is `ceil(Cost * BaseValue)` — ceiling, like every other price in the game, which is
what makes level 1 $16 rather than $15.

**Composition is greedy from the top, but it MOVES ON ONE UNIT EARLY**, then the squad is
sent in reverse. Each unit type is bought while there is still room for TWO of it; the moment
only one more would fit, the budget drops to the next type down. The count is
`floor(remaining / cost) - 1`, not `floor(remaining / cost)`.

That single `- 1` is what makes it an army rather than a handful of big units. $6,000 of White:

| | old rule | current rule |
|---|---|---|
| legg $2,066 | 2 | 1 |
| bread $338 | 5 | 10 |
| alpacco $81 | 2 | 5 |
| ringo $18 | 0 | 7 |
| squirt $9 / catto $4 / doggo $3 | 1 / 1 / 1 | 1 / 2 / 2 |
| **total** | **12 units, $6,000** | **28 units, $6,000** |

Each tier hands its "last affordable copy" of budget down, where it buys several bodies instead
of one. **The cheapest unit is exempt** — there is nothing below it to hand a remainder to, so
it buys `floor(remaining / cost)` outright; otherwise up to two tier-1 prices would be stranded
at the bottom of every cast.

Cadence is **one unit every 15 ticks** (0.5s at 30 Hz). The flat-five version ran
`delay = 0,10,20,30,40` and the first cut of this rewrite kept that 10-tick spacing; at 28
units it read as a firehose. At 15 ticks a level-3 squad takes ~14 seconds to finish walking
out, so its tail joins a battle its head is already in. Squad sizes now run 3–7 at level 1,
11–17 at level 2 and 23–32 at level 3 depending on the team's price curve.

**Two rules that are the balance content rather than bookkeeping:**

- **ORDER IS REVERSED — the lowest tier walks out FIRST.** The chumps meet whatever is already
  on the field and the expensive units arrive behind a screen instead of leading it. The
  chump-blocking section explains why that matters more than it looks: a body absorbs one enemy
  swing whatever it is, so the cheap units are the efficient blockers and the tier 7 wants to
  arrive after something else has taken contact.
- **THE LAST DOLLARS ARE NOT DISCARDED.** If anything is left once even the cheapest unit is
  out of reach, the squad **overspends by one tier-1** rather than letting it evaporate.
  Without it a $15.96 level-1 cast rounds down to nothing on a team whose tier 1 costs $4,
  and the gadget's value would depend on how neatly a roster divides the budget. Eight of the
  24 cells land over budget as a result, by at most one tier-1 price: Blue $17 and White $18
  against $16 at level 1; Black, Blue, Orange and White $271 against $270 at level 2; Black
  $6,001 and Orange $6,003 against $6,000 at level 3.

**Tier 8 is unreachable at every level** — the cheapest is Purple's $12,760 against a $6,000
maximum budget — so this cannot hand anyone an ace. Nothing enforces that; it falls out of the
prices, and a tier-8 repricing below $6,000 would put one in the squad.

**`ReinforcementsEffect.BuildSquad` is the single source of truth**, and there is a second
caller: `OpponentEconomy.ObserveGadgetCast` has to know which free units to expect so it does
not charge the opponent for them, and it now asks the effect rather than owing "5 of tier
BaseValue". That call needed the caster's TEAM, which is why `ObserveGadgetCast` takes a
`TeamColour` — the team colour is drawn on screen, so reading it keeps the tracker's fairness
rule intact. Reimplementing the composition there is the drift that section already warns
about for the economy ladders.

**Guarded by `--reinforcements-check`** (`CastleDefense.Simulation`), which checks the CSV
against an independently transcribed design table, reproduces Marc's hand-worked White level-3
squad unit for unit, asserts four composition invariants across all 8 teams x 3 levels, and
then casts the gadget in a real tick loop to confirm the cadence, the arrival order, and that
the units are free of both money and charges.

### This is a balance change and it invalidates every bot-vs-bot benchmark

`bot-checksum --games 24` moves `C9C7E5C0342D07AA43D65113768E5B9A` →
`17659AA8C69B0F9548D376D362BA9DDC` (and again to `4821D039CFF0A7D4FF63925ACE2ABCDD` once the
move-on-one-early rule and the 15-tick cadence landed). Reinforcements is one of the four defence gadgets every
sweep rolls, so this reaches `FLAGSHIP_BASELINE.md`, the counter table, `best-loadout` and
every ladder baseline — see the note added to the 2026-09-02 `best-loadout` section above.
Trained ONNX models are unaffected in shape (the mask is still 14 wide, the observation vector
untouched) but were trained against the old payout.

**A second CSV rebalance the same day moved it again, to
`2331685BD8897846177C7BD579056662`** — Marc's edits to `master_gadgets.csv`: cash cost
$65/$975/$7,800 → $75/$1,125/$9,000 with `Delay` 48 → 115 and cooldowns 15s → 18/18/20s,
nuke `Radius` 150 → 300 and 300 → 400, meteor `Radius` 750 → 500. `17659AA…` is reproducible
by reverting those rows, which is how the cash animation rewrite below was shown to be
presentation-only.

## The cash drop flies in on a plane

Rebuilt 2026-09-03, on the `balance-updates` branch. The crate used to materialise above the
drop point and float down with nothing to explain where it came from. A plane now flies in
from the OPPOSITE side of the map, takes 3 seconds to make its run, and releases the crate
over the allied castle — which is what the extra cast delay in the CSV was added to cover.

**The level-3 bug this fixed is worth naming, because it was a two-place mismatch, not a
timing bug.** `CashEffect.Execute` raised `TriggerGadgetAnimation` at cast time AND again on
each of level 3's eight payouts — **nine animations for eight crates**. On screen that read
as one crate falling alone, a pause, and then the rest. The effect now raises it exactly once
per cast at every level, and the client draws the whole run from that single trigger.

**PAYOUTS ARE UNCHANGED AND THAT WAS VERIFIED, NOT ASSUMED.** Level 3 still pays 8 x
`BaseValue` at `Delay + 0/10/.../70` ticks; levels 1-2 still pay once at `Delay`. Proof:
`bot-checksum --games 24` with Marc's CSV edits reverted in the build output returns
`17659AA8C69B0F9548D376D362BA9DDC`, bit-identical to the pre-change run. The rewrite is
presentation only.

### The timeline is a two-sided contract, and one side is a CSV column

```
cast ──PLANE_MS(3000)──▶ release 1 ──CRATE_FALL_MS(2000)──▶ crate lands ──TEXT_MS(1500)──▶
                            └─PAYOUT_INTERVAL_MS─▶ release 2 ...   (level 3 only)
```

- `CashAnimator.PAYOUT_INTERVAL_MS` **mirrors `CashEffect.PayoutIntervalTicks`** (10 ticks).
  One crate per payout, on the payout's own cadence. Change one without the other and the
  crates drift away from the money.
- A crate lands at `PLANE_MS + CRATE_FALL_MS` = **5,000 ms = 150 ticks**, and `Delay` IS
  150 (set 2026-09-03), so the money arrives exactly as the crate touches down. Those two
  numbers are a contract: shortening the fall or lengthening the plane's run without moving
  `Delay` desynchronises the payout from the crate.

### Why the level-3 plane does not hold the level-1 speed

Levels 1-2 release one crate, so the plane's 3 s run ends over the drop point and that is the
whole story. Level 3 releases eight over 2,333 ms, and a plane still travelling at the
level-1 speed covers **1,322 px in that window** — it would rain most of its crates off the
edge of a 2,000 px map.

So the speed is solved for the WHOLE run instead, from the constraint that the run-in takes
`PLANE_MS` and the LAST crate lands on the drop point:

```
releaseX0 = (dropX + f * planeStartX) / (1 + f),   f = dropWindowMs / PLANE_MS
```

Seat 1 level 3: the plane enters at x=2209, releases its first crate at x=1135, and the eight
land evenly across ~835 px ending at x=300 in front of the castle. Seat 2 mirrors it exactly.
**The trade is that at level 3 the plane's total crossing takes 5.3 s rather than 3 s**; the
run-in before the FIRST crate is 3 s at every level, which is the part the cast delay covers.

Two details that are not incidental:

- **Both plane sprites face LEFT**, so seat 1 draws them as authored and seat 2 mirrors them
  about the sprite centre. `cash_plane_3` is the level-3 variant, `cash_plane` levels 1-2;
  both are in `asset-loader.js`'s `gadgetAssetList` or they silently do not load.
- **The crate now leaves the plane's belly, not the top of the screen.** `PLANE_Y` is 10
  (raised from 30 so the plane reads as up in the sky rather than skimming the hilltops), so
  the crate falls ~266 px instead of 410 in the same 2,000 ms — visibly slower, correct for a
  parachute, and the 2,000 ms is the payout anchor and must not be shortened to compensate.
  Seat 1's run never passes behind the money box: the box covers world x ≈ 13–277 with the
  camera home, and the plane travels 2209 → 300. Seat 2 enters through it, at the start of its
  run rather than at the drop.

### Verifying an animation in this repo

`requestAnimationFrame` is throttled when the browser pane is not frontmost, so screenshotting
a 9-second animation samples frozen frames and proves nothing — three attempts produced a
static `$104` and no plane. What works is a throwaway page under `wwwroot/` that imports the
real animator, sets `timer` to fixed values and draws each into its own canvas: one static
filmstrip, no timing dependence, and it exercises the shipped module rather than a copy.
Delete the page afterwards — `wwwroot` is served.

## The wave has a knockback budget

Added 2026-09-03, on the `balance-updates` branch. A wave used to sweep the full map for its
whole `HazardDuration` and launch everything it overlapped, so **its power scaled with however
many units happened to be on the field** — against a big army it was unbounded. It now carries
a cap, spends it, and collapses.

| level | cap | where it comes from |
|---|---|---|
| 1 | 50 | `BaseValue / 10` (KNOCKBACK 500) |
| 2 | 100 | `BaseValue / 10` (KNOCKBACK 1,000) |
| 3 | **1,000** | flat, NOT BaseValue/10 (which would be 300) |

Level 3 is deliberately outside the rule: the Tsunami is meant to be effectively uncapped, and
nothing puts 1,000 units on one side of the field, so a level-3 wave still crosses the whole
map and expires on `HazardDuration` exactly as before. `WaveEffect.CapFor` is the one place
this is decided.

**DISTINCT UNITS, NOT HITS.** A unit standing inside the wave is pushed on every tick it
overlaps, exactly as before; only the FIRST of those ticks costs budget. Counting hits would
make the cap depend on the wave's width and speed rather than on how many units it caught —
a 50-unit cap would then be spent on a handful of bodies, which is a completely different
balance change wearing the same number.

**Measured bite:** a level-1 wave against 120 tier-1 units spends its 50 and dies at tick 41
of a 150-tick duration — roughly a quarter of the way across. Against a thin field it still
crosses normally.

### The variable duration needed no new plumbing, and that is not luck

`wave-animator.js` already drove **both** its position and its lifetime off the real
`WaveHazard` in the broadcast state rather than off a client clock — a previous fix for the
wave disappearing before units stopped being knocked back. So "the wave falls away early"
is one line in `WaveHazard.ProcessEffect` (`ExpiresAtTick = CurrentTick`) and the client
follows on its own. **The hazard leaving `GameState.Hazards` is the ONLY signal the client
gets**; there is no separate message, and adding a field to the wire would cost per-tick
egress on a wire that was deliberately trimmed 3.95x.

What the client adds is `FALLOUT_MS` (400ms): the wave dives out of the world and the screen
shake ramps to zero, instead of blinking off the instant the hazard vanishes. That path is now
the NORMAL one rather than a rarity.

**THE FALL-OUT IS A DIVE, NOT A DROP, and both halves of that matter.** It keeps travelling at
its OWN real speed (`MAP_WIDTH / duration`, so the slow tsunami leans less than a fast level-1
wave — 80px against 160px over the 400ms) while the sink runs on **p squared** rather than p.
The quadratic is what makes the path read as a dive: the first frames are almost all forward
motion and the last almost all downward, i.e. an object that carried its momentum and then
fell. Linear sink with no drift — the first cut — looked like the wave stopping dead and
falling through the floor.

### WaveHazard holds the only reference-typed field on any hazard

`_launched` (a `HashSet<Guid>` of units already charged against the cap) is exactly the case
`Hazard.Clone`'s own doc comment warns about: a `MemberwiseClone` would share it between a
search rollout and the live game, so a rollout's wave would silently spend the real wave's
budget — only in games where a search bot happened to be thinking while a wave was crossing.
`Clone()` is overridden to copy the set, and `--wave-check` asserts the isolation directly by
advancing a clone and checking the original's count did not move.

### The wave's crest was going through the HUD, and one constant could not fix it

The sprite is drawn `waveSize` tall upward from its foot, where `waveSize` is the gadget's
`Radius` — 100 / 200 / 400. At the shared foot of y=400 (the top edge of `#hud-bottom`) that
puts level 3's crest at **−5**, i.e. off the top of the canvas and through the money box.
Levels 1 and 2 top out at 295 and 195 and were never close.

**Lowering all three was tried and measured, and it is worse**: level 3 needs +138 logical
units to clear the HUD on a landscape phone, and moving level 1 down by that buries it
entirely behind the shop bar. So the foot is a CLAMP — each level is pushed down only by its
own overshoot. The baseline is 410 (10 into the shop bar rather than balanced on its lip) and
level 3 clamps to ~548, putting its crest 10 below the HUD; levels 1 and 2 sit on the baseline.

`HUD_MARGIN` (10) is the gap the clamp keeps between the crest at the peak of its bob and the
HUD's bottom edge. Without it the clamp lands the crest EXACTLY on that edge, which still
reads as touching — the wave visibly kisses the income line once a second as it bobs. Crests
now sit at 305 / 205 / 143 against a HUD bottom of 133.

**The clearance is MEASURED, not hardcoded, because the HUD and the canvas do not scale
together.** The HUD is a DOM box in fixed CSS pixels (~100px); the canvas is scaled by
`innerHeight / 500`. That 100px is **133 logical units on a landscape phone and nearer 50 on
a tall desktop window** — a constant tuned for either is wrong on the other. `wave-animator.js`
reads `#hud-top`'s bottom once in the constructor and falls back to 133.

### A string-vs-number bug this uncovered

`asset-loader` stores every gadget CSV column as the **string** it parsed. `waveSize` had
always been that string; it survived because the only uses were `waveSize / 2` and
`drawImage`, both of which coerce. The moment it met a `+`, in the clamp above, `"400"`
concatenated instead of adding and the clamp silently evaluated to the unclamped baseline —
no error, no warning, just the old behaviour. `waveSize` and `hazardTicks` are now `Number()`d
at the source. **The same trap waits for any other `gadgetData` field used in arithmetic.**

### Two pre-existing wave defects found while reading, and deliberately NOT fixed

Both are balance-affecting, and Marc is mid-testing; changing them silently would muddy his
results.

- **`knockbackDist` is not restored after a tier-8 unit.** `ProcessEffect` does
  `if (enemy.Tier == 8) knockbackDist = 25;` inside the loop over enemies and never puts it
  back, so **every unit processed after the first tier-8 in that tick gets 25 instead of the
  level's real distance**. Order-dependent, and invisible unless a tier 8 is in the wave.
- **Level 2's knockback disagrees with its own CSV row.** `master_gadgets.csv` says
  `KNOCKBACK 1000` and the Collection screen prints that; `WaveHazard` hardcodes **1500**.
  Levels 1 and 3 match (500 / 3000). The cap reads the CSV, so the two halves of the gadget
  are now sourced differently.

### This is a balance change

`bot-checksum --games 24` moves `2331685BD8897846177C7BD579056662` →
`BC3AFB8FE8F9384DA568B03123FBF511`. Wave is Blue's signature, so it is in every sweep that
rolls Blue. The reinforcements composition change that followed took it to
`4821D039CFF0A7D4FF63925ACE2ABCDD`, and raising the attack-speed floor took it to
**`609A34BBDC80D7FE520606B3B0776F42`**, which is the current value.

## The attack-speed floor is 0.33/s, and it is a tier-8-only knob

Raised 2026-09-03 from **0.20** (one swing per 5.00s) to **0.33** (one per 3.03s).
`GameDataManager.MIN_ATTACK_SPEED` / `MAX_ATTACK_SPEED` name the two bounds; the CSV's own
`AttackSpeed` column is ignored, the value being recomputed from the Doggo Formula and then
clamped.

**IT TOUCHES EXACTLY EIGHT UNITS, AND THAT WAS CHECKED RATHER THAN ASSUMED.** Running the
formula over all 64 roster rows: the eight tier-8 aces produce raw values of **0.013 to
0.054/s** — one to two orders of magnitude below anything playable — so every one of them
lands on the floor, while **no other unit in the game is anywhere near it** (the next lowest
is comfortably above 0.33). So this is a **65% DPS buff to every ace and a byte-for-byte
no-op for the other 56 rows**.

The two consequences worth carrying:

- **The clamp spread narrowed from 25x to 15x.** Tier 4/5 units still clamp at the TOP
  (5.0/s). The survival law asks for one body per enemy SWING, so the defensive rate a tier 8
  demands rose by the same 1.65x while tier 4's did not move.
- **Every tier-8 figure in the chump-blocking record is stale by 1.65x** — the holding
  threshold 5.00s → ~3.03s, the price of delay $2.00/s → ~$3.30/s. Those are DERIVED from the
  law, not re-measured; `stall/FINDINGS.md` carries the same banner and the 22,700-run sweep
  has not been re-run.

`bot-checksum --games 24` moves `4821D039CFF0A7D4FF63925ACE2ABCDD` →
**`609A34BBDC80D7FE520606B3B0776F42`**.

## "Update the balance dashboard" means `balance-matrix`

Added 2026-09-03 to Marc's spec. **This is the command; it replaces `dashboard` for balance
work.**

```
CastleDefense.BotArena.exe balance-matrix [--games N] [--out DIR] [--from-csv]
```

128 loadouts x 128 opponents = **16,384 ordered cells**, one game each, HeuristicBot on both
sides. Every unordered matchup is therefore played TWICE, once in each seat. **16,384 games in
2m07s** on 18 threads (estimate printed before the run is 2m10s). Writes
`dashboard/balance_matrix.csv` (one row per game) and regenerates `dashboard/dashboard.html`.

`--from-csv` rebuilds the HTML from an existing CSV without replaying anything, so a
presentation tweak costs nothing.

### Why neither existing sweep answered this

| | `dashboard` | `counter-matrix` | `balance-matrix` |
|---|---|---|---|
| opponent loadout | random, **not recorded** | swept | swept |
| seats | alternated | **fixed** (sp seating) | both, by construction |
| map recorded | no | no | **yes** |
| games / time | 24,960 / 4m55s | 16,384 / 2m10s at n=1 | 16,384 / 2m07s |
| ONNX opponents | 15, **~80% of wall time** | none | none |

The old `dashboard` marginalised over exactly the variable balance work wants to condition on,
and spent most of its wall clock on ONNX models that say nothing about game balance.

### THE MAP SEED IS THE ONE REAL DESIGN DEPARTURE

`counter-matrix` derives its map from the GAME INDEX ALONE — deliberate common random numbers,
so cell i of every cell plays the identical board. **At one game per cell that would put all
16,384 games on a single map**, and every map changes the rules, so the whole sweep would
describe one map and the map column would be a constant.

So `balance-matrix` seeds the map and the engine from the **unordered pair**. The two seatings
of {A,B} share a map and an RNG stream — which is the one pairing that matters here, since it
makes the seat comparison within a matchup exact — while different matchups spread across all
maps. Observed spread: ~2,000 games on each of the 8 base maps plus ~110-150 on each shadow
variant.

### Reading it

**Marginals are the signal; cells are noise.** A team appears in 16 loadouts against 128
opponents in both seats, so even n=1 gives it 4,096 games (95% CI ±1.5pp). A single loadout
gets 256 games (±6pp) and a single loadout PAIR gets 2. Read the marginal tables and the
team-vs-team matrix; treat the top/bottom-12 list as an ordering, not as rates.

**Every tally counts a game once per SIDE, not once per game.** That is what makes the
marginals seat-unbiased, and it is also the subtle bit: on the team-vs-team DIAGONAL the row
team is on both sides, so the game lands in the bucket twice, once as a win and once as a loss,
and a mirror correctly reads ~50%. Counting it once instead scored every mirror as a guaranteed
win and put the whole diagonal at 93-98% — caught on the first render, and the diagonal sitting
near 50% is the check that it is still right.

## Unit charges

Added 2026-09-01, restoring a mechanic from the old version of the game. Every buyable unit
holds up to **5 charges** and regains **one per second**; spending the last one puts that
unit on cooldown until the next charge lands, exactly like a gadget, and the unit button
gets the identical wash. The purpose is to stop mindless spamming -- money alone no longer
gates how fast one unit can be poured onto the field.

**ABSENT MEANS FULL.** `PlayerState.UnitCharges` only holds units that have been used, and
`GetUnitCharges` treats a missing key as `UnitMaxCharges`. This is not an optimisation: a
`PlayerState` does not know its `Team` when constructed (only the hub assigns it, and the
timeSkip constructor never does), so there is no point at which a "seed every roster entry"
loop would be correct for every caller. Lazy makes the live game, timeSkip, rollout clones
and a bare `new GameState()` in a harness all correct with no initialisation step.

**A FLAT RULE FOR EVERY UNIT.** `UnitDefinition.MaxCharges` / `CooldownMs` still carry the
older price-scaled formula (`max(1, 25/price)` charges) which would give a $3 tier-1 eight
charges and a $23,000 tier-8 exactly one. Those fields are NOT what the live rule reads.
The regeneration loop in `TickCooldowns` used to read them and was dead code regardless --
nothing ever spent a charge or seeded the dictionaries, so `CooldownTimers` was permanently
empty and the block never executed.

**Free spawns do not consume charges.** The check sits inside `SpawnUnit`'s `!ignoreCost`
branch, so the opening squad, the auto-spawner and the reinforcements gadget are unaffected.

**Enforced in three places that must agree:** `SpawnUnit` (refuses and spends),
`TickCooldowns` (regenerates), `GetActionMask` (gates the policy). A mask that disagrees
with `SpawnUnit` is the dangerous one -- the bot picks a unit it cannot buy, the purchase
silently fails, and the decision is wasted with nothing in any log to say so. Guarded by
`--unit-charge-check`, which also asserts free spawns and rollout-clone isolation.

### This invalidates every bot-vs-bot benchmark taken before 2026-09-01

Unlike the auto-spawner, this one changes the rules for POLICIES, not just for humans:
`bot-checksum --games 24` moves from `817BC80B8A4AF4DC569F01844C73BB50` to
`47EC146D660B0D721B4DC224D8ACB7F9`. Measured over the same 24 seeded games, HeuristicBot's
unit purchases fall from **20,196 to 6,658** -- 101.9 to 35.4 units per 1000 ticks, a **67%
reduction** -- while earned investments barely move (6.79 to 6.88), so the economy ladder is
intact and the cut is specifically the spam.

That the drop is so large is itself a finding: the per-unit cap allows 8 units/sec across a
full roster, yet the bot fell to ~1.06/sec, which means it was pouring nearly everything
into a SINGLE tier. That is the behaviour already recorded in the heuristic unit-selection
notes, and this mechanic hits it directly.

Trained ONNX models are not invalidated in shape -- the mask is still 14 wide and the
observation vector is untouched -- but they were trained against a game where a tier could
be bought every decision, so their effective strength is an open question until re-measured.

## The auto-spawner

Added 2026-08-31. A third economy upgrade in the bottom-left HUD stack, between INVEST and
+HP. Each level buys a faster and stronger **free** stream of units, spawned with
`ignoreCost` exactly like the opening squad -- so they cost nothing beyond the upgrade and
stay out of the action recording, the purchase counters and the money-spent totals.

**19 levels.** The table lives in `PlayerState.AutoSpawnCycles`: one repeating tier pattern
per level (level 5 is `[3,2,1]` = tier 3, then 2, then 1, then round again). **Units per
second is the cycle LENGTH**, deliberately not a separate column -- the two are equal at
every level, so the pattern repeats exactly once per second and deriving one from the other
makes the invariant unbreakable.

**Pricing walks the shared economy curve at a different scale.** `EconomyCurve` is now the
single source of truth for `e^(0.0109x^3 + 0.0011x^2 + 0.4351x + 0.5268)`, used by invest,
repair and the auto-spawner. Investing walks it one integer step per purchase; the
auto-spawner starts at **x = 2.5 and steps 0.25**, with multiplier `(x*4 + 7)` against
investing's `(x*4 + 8)`. That is what makes this ladder 19 rungs where investing is 8.
Levels 17/18/19 are hardcoded (43614 / 100000 / 100000), same reasoning as investing's top
two. Prices go through `WholeDollars` (ceiling) like every other price.

**The cadence accumulator is an INTEGER counting ticks**, not a double counting fractions of
a unit. Rates do not all divide the 30 Hz tick rate -- 4 units/s is one unit every 7.5 ticks
-- so a modulo would deliver 4.29/s or 3.75/s. The first implementation accumulated
`perSecond / TICKS_PER_SECOND` as a double and was measurably wrong: 1/30 added thirty times
is 0.9999999999999999, so every level delivered one spawn fewer per cycle than the table
promises (0.97/s where it wanted 1.00). Counting whole ticks is exact at every rate and
sidesteps any float reproducibility question in code that feeds replay reconstruction and
search rollouts.

State lives on `PlayerState` (level, price, accumulator, cycle index) rather than
`GameEngine`, because `GameEngine.Clone` is shallow and engine-side mutable state would be
shared with every search rollout.

**Guarded by `--auto-spawn-check`** (`CastleDefense.Simulation`), which measures cost, rate
and tier order for all 19 levels against a copy of the design table transcribed
independently of the implementation, by running the real tick loop.

### The art

Four frames in `assets/buildings/`, 74x62, team-independent and loaded once (like
`dead-castle`). 1/2 are the idle conveyor loop; 3/4 are the same loop with the lever pulled.
`View.drawAutoSpawner` runs 1<->2 continuously and swaps to 3<->4 for `LEVER_MS` (110ms)
each time the machine produces a unit -- short on purpose, so the throw snaps back rather
than reading as a lever resting in the pulled position.

**Drawn BEFORE the castles**, so the overlapping half is embedded in the stonework and only
the conveyor projects into the field. Safe for the readout: the castle sprite is under 5%
opaque across the machine's panel.

**The lever is RATE-matched, not phase-locked.** It fires on a client clock running at the
level's real units-per-second (`AUTO_SPAWNER_RATE`, mirroring `PlayerState.AutoSpawnCycles`
where the rate IS the cycle length), so pulls happen as often as spawns really happen but
not necessarily on the same tick. Phase-locking would mean sending the engine's spawn
accumulator every tick for both players -- a per-tick wire cost on a wire that was
deliberately trimmed 3.95x -- to move the pull by a frame or two.

**Geometry is authored from the castle's bottom-left corner, y UP**, because that is the
frame in which "it lines up with the hole in the wall" is stable. The machine's bottom-left
sits at (151, 11) and the level number's at (2, 15) from the machine's own bottom-left.
Conversion to canvas coordinates (y DOWN) happens in exactly one place.

**Vibration** ramps `SHAKE_MIN` (0.12px) at level 1 to `SHAKE_MAX` (2.6px) at 19, driven by
two incommensurate frequencies so it reads as a buzz rather than a wobble. The curve is
CUBIC (`SHAKE_CURVE`), not linear: linear put half the ladder above half the maximum
amplitude, which was far too violent for one small conveyor. Cubing keeps the top end and
collapses the bottom. The number shakes with the machine -- it is painted on it.

**The number is drawn OUTSIDE the mirror transform.** Inside it, P2's digits would render
back-to-front; only the POSITION is mirrored (right-aligned against the mirrored anchor),
not the glyphs. It is 18px Press Start 2P, which measures ~17px tall and 18px wide per digit
-- so a single digit fits the panel and TWO do not (the panel gives 26px from NUM_X). Levels
10-19 are auto-scaled down to fit rather than allowed to spill onto the conveyor.

**KNOWN COSMETIC ISSUE: "Lv." is painted into the sprite, so it mirrors with it** -- on P2
the caption reads backwards while the number above which it sits reads forwards. Fixing it
means either taking "Lv." out of the art and drawing it as text beside the number, or
shipping a pre-reversed variant of the four frames for seat 2.

### The max-level crash (fixed 2026-08-31, same day it shipped)

Buying level 19 froze the buyer's client. `AutoSpawnPriceFor` returns `PositiveInfinity` for
"no such level" and `ApplyAutoSpawnStep` asked it for level 20, storing infinity in
`AutoSpawnPrice` -- a field serialised to both clients every tick. `System.Text.Json` cannot
write a non-finite number, so the throw surfaced inside SignalR's per-connection write
pipeline and aborted that one client while the server kept simulating; the player saw a
freeze, then a rejoin that failed on malformed JSON, and the game was saved `abandoned`.

**This is the second instance of the identical failure class** -- `wall_3` writing
`float.PositiveInfinity` into `Unit.AttackCooldown` (commit 29d64bfe) was the first, and the
rule adopted then was that nothing may write a non-finite value into serialisable state. The
sentinel is now marked query-only at its source, the price is left untouched at the top of
the ladder (matching ARMAGEDDON), and `--auto-spawn-check` section 6 walks the whole ladder
and runs the real serialiser, so a third instance in this field fails the check.

Confirmed from the recording rather than inferred: `E1B2E1` shows P1 with exactly 19
auto-spawn actions and P2 with 18, `winner=0`, `end_reason=abandoned`.

### This is a balance change and it is not yet in any benchmark

Bots never buy it, so bot-vs-bot numbers are unchanged and still comparable. Any HUMAN game
recorded from 2026-08-31 onward may contain it, which makes such games not comparable to the
existing human play record.

### Every benchmark taken before 2026-08-26 is stale

Ten free units on the field and $10 in hand move every opening. Both changes are symmetric
and cannot favour a seat, but nothing measured before this date is comparable to anything
measured after it -- win rates, game lengths, earned investments, the counter table, the
human play record. `OpeningSquadSize = 0` and the money constant are the two knobs to
restore the old opening if a historical comparison is needed.

## Iterating on HeuristicBot

Three files are the working set, added 2026-09-01. **Read them instead of re-deriving the
engine or re-reading a previous session's transcript.**

- **`BOT_MECHANICS.md`** — the engine's rules, written out once. Combat, targeting, the
  derived-stat fallbacks, the three economy ladders with their price tables, charges,
  `ignoreCost` paths, gadget XP, map effects, and a map of `Decide()`. Deriving this cost
  ~3,000 lines of reading; the file exists so nobody pays that twice.
- **`BOT_BACKLOG.md`** — the hypothesis queue. Every entry carries a MECHANISM and a
  PREDICTED SIGNATURE naming what must move *and what must not*.
- **`BOT_ITERATION_LOG.md`** — append-only results, kept or reverted.

**The measurement is `ladder --variant <profile>`**, which plays a named
`HeuristicBotSettings` profile against the committed reference on identical pre-generated
specs, side-swapped, with Wilson intervals. It now also reports **units/sec, idle money and
end-of-game castle HP**, because win rate alone cannot express a predicted signature. Use
`--only HeuristicBot`; `--only Heuristic` also matches the four `SavingHeuristic@` contenders.

**Every change ships behind a new flag defaulting to false**, and accepted flags stack in
`HeuristicBotSettings.Accepted` rather than being promoted into the defaults. Promotion moves
`bot-checksum`, every ladder baseline, `FLAGSHIP_BASELINE.md` and the shipped singleplayer
opponent at once, so it is a deliberate decision, not a side effect.

### PROMOTED 2026-09-02: `ChargeAwareFallback` is now the default

Marc's call, after iteration 1 measured +2.6 to +7.3 points head-to-head across four arms
(two seeds x two modes, 5,600 games per arm) with earned investments unmoved to two decimals.

**`bot-checksum --games 24` moves `47EC146D660B0D721B4DC224D8ACB7F9` ->
`C9C7E5C0342D07AA43D65113768E5B9A`.** Every bot-vs-bot number recorded before this date
describes the old bot, including `FLAGSHIP_BASELINE.md` and the counter table. Set
`ChargeAwareFallback = false`, or use `HeuristicBotSettings.PreChargeAware`, to reproduce it.

**Still open, and it is a real gap:** `RolloutSearchBot` uses `HeuristicBot` as both its
policy prior and its rollout policy for BOTH sides, and this change costs ~35% throughput.
Nothing has measured what that does to search, because `search-test` takes no settings
profile. The search numbers in `FLAGSHIP_BASELINE.md` should be treated as unverified until
it does.

### The rule that came out of the first seven iterations

One change was accepted and four were rejected, and **the four failures shared one
mechanism: each opened a NEW SPENDING CHANNEL, and each lost earned investments.** The
accepted change only re-allocated *which* unit was already being bought and moved earned
invests by 0.00. Every cap in `HeuristicBot.cs` — `AttackSpendFraction`,
`InvestPaceTargetSeconds`, `ReactiveFlowCap`, `AttackGateMinInvestment`,
`reactiveSpendBudget` — was tuned assuming no other channel exists, and a new one bypasses
all of them at once.

**Prefer changes that re-allocate spend the bot is already making.** If a change must add a
channel, take its money from an existing budget. That was tested directly: the identical
auto-spawner feature cost **0.81** earned investments funded from savings and **0.01** funded
from the attack allowance.

### A ladder rung can be blind to the change you are measuring

`ChipperBaseline` was added because the ladder could not adjudicate a defensive fix at all:
every existing rung that the fix helped was already pinned at a 100% win-rate ceiling, so the
ladder saw only its cost. **A benchmark that observes one side of a trade will reject every
version of it forever and look rigorous doing so.** The new rung invests normally and keeps
exactly one cheap unit alive on the enemy castle — the exploit Marc and `RolloutSearchBot`
both use. Against it the fix moves end-of-game castle HP from 52.7% to 95.9%, and still does
not move win rate, because that exploit does not beat HeuristicBot. Some changes can only be
priced by Marc playing.

**Note this rung changes the OVERALL figure**, so OVERALL is not comparable across the commit
that added it. The per-rung rows are.

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
  `Simulation/Program.cs` feeds it to `batchEval` for N-step potential-based reward
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
- **A WIN BY DEFAULT IS NOT A WIN.** A game whose loser disconnected and never came back
  is awarded to the survivor after a 60-second grace window (`games.end_reason =
  "disconnect"`), and one nobody came back to is `"abandoned"`. Neither was decided by
  play, and NOTHING IN THE REPLAY FILE MARKS EITHER -- the winner byte is identical to an
  earned one. Exclude both from any analysis of recordings unless Marc asks for them;
  `ReplayFile.IsRealResult` is the single place that decision is made. See "Disconnection,
  rejoin, and WINS BY DEFAULT" above.
- **`recordings/singleplayer/` contains games seat 1 did NOT play, and they must be
  excluded.** Anything treating seat 1 as "the human" has to filter them out;
  `ReplayFile.SelectHumanGames` does, via `ReplayFile.IsHumanPlayed` (drops `game_mode`
  `watch`/`league` and any `leaguewatch:` opponent) and then `IsRealResult`.
  **Re-counted 2026-08-29: 267 replay files**, not the 153 this entry used to claim —
  12 league-watch, 255 human-played, of which 252 survive the default-win filter. The mode
  mix is now 196 `sp`, 58 `practice`, 12 `league`, 1 `accept`. **Do not quote these numbers
  either**; they move every time Marc plays. Re-derive them from the DB, which is the whole
  point of the filter living in code rather than in a list here.
- **The engine CAN now be cloned** — this entry used to say it could not, and that is
  stale. The PendingEffect refactor converted delayed gadget effects from `Action`
  closures to data records, so `GameEngine.Clone(rngSeed)` produces an independent copy.
  It deliberately drops event subscribers, the queued input actions, and the RNG stream.
  `RolloutSearchBot` and `--divergence` both depend on this working; `CloneCheck.cs` in
  BotArena is the guard. **Updated 2026-08-29:** the legacy closure list (`_scheduledEvents`,
  `ScheduledEvent`, `ScheduleAction`) and the `Clone()` guard that threw on a pending legacy
  event have all been DELETED, having had zero callers since the migration finished. An
  earlier version of this entry described them as still present.
