# Cleanup backlog

Deferred tidy-up work. Deliberately **not** a general "improve the codebase" list — it
is scoped to the failure mode this project actually suffers from:

> Code that produces a number or a claim once, which then gets pasted into a document
> and never re-derived, has no feedback loop and rots silently.

The engine itself is exercised by millions of simulated games, so bugs there surface
fast. Documentation, one-shot measurement tools, and hand-maintained constants do not
have that pressure, and a 2026-07-28 audit found two of the two instruments it examined
were broken. Everything below is in that category.

Add to this list rather than fixing opportunistically mid-task — the point is to keep
the critical path clear while making sure nothing gets silently forgotten.

---

## Stale comments and docs

- [ ] **`CastleDefense.Simulation/Program.cs` ~1313–1340** — the comments explaining why
  `CloneStateForShadow` can shallow-copy `Hazards` refer to `engine.ScheduleAction`,
  which no longer exists (replaced by `ScheduleEffect` / `PendingEffect`, 2026-07-28).
  The *reasoning* is still correct — deferred effects never fire on the shadow clone
  because the clone engine is discarded before `Tick()` — only the names are wrong.
  Worth fixing carefully rather than casually: this comment documents a real bug that
  already bit the project once (shallow-copied Units letting the shadow bot attach
  Heal/Speed statuses to the *real* trajectory), and the shallow-Hazard caveat it flags
  becomes live the moment any gadget creates a hazard synchronously.
  **Note:** this is superseded if `CloneStateForShadow` moves into the Engine as part of
  the `Clone()` work — do that first and revise the comment in its new home.

- [ ] **`TRAINING_CAMPAIGN_LOG.md` headline numbers before 2026-07-28** are suspect.
  Every invests/game figure quoted prior to that date includes free headstart
  investments (E[timeSkip] = 2.118 per side) and so overstates policy behaviour by
  roughly 2.1. The log has a correction entry, but the older sections were not edited
  in place and still read as authoritative.

## Measurement tools not yet re-audited

Two of two instruments examined were broken. These are the same shape and have not been
checked:

- [ ] **`invest-counterfactual`** — highest priority. Its "+20 percentage points of win
  rate from one early investment" result is load-bearing for the entire invest thesis,
  and the 2026-07-28 ladder data found earned invests essentially uncorrelated with
  beating HeuristicBot. One of those two results is wrong.
- [ ] **`model-diag`** — the P(invest)-when-legal statistic is computed over states where
  invest is legal, which in cold-start games was 68 samples out of 338,609. Small-sample
  behaviour of that geometric mean needs checking.
- [ ] **`hunt`** and **`trace`** — not examined at all. `dashboard` was partly audited on
  2026-08-05: its protagonist was hardcoded to `HeuristicBotAdapter`, so it had been
  describing an agent that is no longer what singleplayer ships. Now selectable via
  `--bot search`. Its aggregation and HTML rendering are still unexamined.

## Open performance problem

- [ ] **`dashboard --bot search` runs at ~1 core on a 20-core box.** Two independent
  sweeps of identical work took 25 min and 49 min, and sampled CPU sat at 0.44-0.90
  cores while the machine was 94% idle and 22 threads were alive. The spam phase (one
  search per game) parallelises fine and `search-test` parallelises fine; only the
  MIRROR phase (two searches per game) stalls. Three things were tried on 2026-08-05:
  dynamic partitioning instead of the range partitioner (no effect — the diagnosis was
  wrong), flattening the work list so cells no longer run their games sequentially
  (correct, kept), and server GC (roughly doubled throughput, kept — every rollout
  clones the engine, so allocation pressure is enormous and workstation GC suspends all
  threads per collection). Something still serialises it. There are no `lock`s in the
  Engine and `Random.Shared` is thread-safe, so the next place to look is allocation
  stalls under the clone path, or `Parallel.ForEach` failing to ramp its worker count.
  Not urgent — the sweep does complete — but it caps how much mirror data is affordable.

## Data hygiene

- [ ] **11 abandoned games remain in `recordings/game_records.db`.** Marc rerolls until he
  gets the matchup he wants, and the abandoned attempts record as losses in which P1
  never acts. The `.replay` files were removed on 2026-08-05 (backed up to
  `recordings/quarantine_no_p1_actions_20260805/`) but the database rows were not
  touched, so any win-rate query straight off the DB reads **82.6% vs HeuristicBot when
  the true figure is 91.9%**. Either delete those rows or add an `abandoned` flag — a
  hand-maintained "exclude these ids" list is exactly the rot this file exists to stop.
  Detector: `CastleDefense.PythonAI/inspect_replays.py` (read-only unless `--delete`).

## Fragile hand-maintained constants

- [ ] **`train_evaluator.py: CURRENT_W` must match `GameState.cs: EvalWeight*`** and has
  drifted before, making the tool's "Current vs Learned" table compare against weights
  that had not shipped in months. Currently in sync. Wants a guard — either read the
  values from the C# source, or have the C# side emit them to JSON at build time.

## Data pipeline

- [ ] **`calib_data.csv` has no `tick` or `game_id` column**, so the autocorrelation
  thinning in `train_evaluator.py` can never fire for it and all ~1.7M within-game frames
  are used raw, swamping the human-replay data. Fix at the source: the C#
  `--collect-calibration` exporter should emit both.

## Dead code to remove once confident

- [ ] **`GameEngine.ScheduleAction` and `_scheduledEvents`** — zero callers as of the
  2026-07-28 conversion. Kept alive only so a half-finished migration would still run.
  Delete once the converted effects have been verified against the ladder baseline.
- [ ] **`RusherBaseline`** — dropped from the ladder as a duplicate of `Tier1Spam` (it
  buys "cheapest affordable", which is always the tier-1 unit). Class still exists and is
  still referenced by other BotArena modes.

- [ ] **`GameEngine.AttackCastle`** — zero callers; `MoveAndFight` inlined it so castle
  damage could be deferred with unit damage. Delete it. It is not merely dead but a
  *loaded gun*: it still contains the unguarded `1000f / def.AttackSpeed` that produced
  `float.PositiveInfinity` for a wall (AttackSpeed 0) and crashed live games by making the
  state unserialisable — fixed 2026-08-04 in the inlined copy only. Reinstating this
  method reintroduces the bug.

## Left-edge / centre convention mismatch (found 2026-07-31)

`Unit.Position` is the sprite's **left edge** — that is the renderer's convention
(`view.js drawUnit` computes `centreX = position + width/2`). Several places mirrored
side 1 → side 2 by flipping a *sign* instead of by reflecting the *geometry*
(`p → MAP_WIDTH - p - Width`), so each was off by one unit width.

**Fixed 2026-07-31** (all three verified by direct measurement, both seats identical):
`GetDistanceToEnemyCastle` (P2 opened fire a full width early, and that gap let P2's
units shoot over P1's blockers 74.6% of the time vs 0.0% the other way); `SpawnUnit`'s
default position (P2 walked `W` extra pixels on every spawn — both seats now cover 1700px
and reach the wall on the identical tick); and `FindTargetsFast`'s engagement distance
(against a 450-wide wall, attackers used to halt ~200px short as P1 and ~200px *inside*
it as P2; now 0px both ways). `maxEnemyWidthPad` was also a hardcoded 200f while `wall_3`
is 450 wide, so the backward scan could break before reaching a wall in contact — the
bound is now derived from the unit cache.

Still outstanding, left alone because each changes RL inputs or balance globally:

- [ ] **`GameState.GetStateVector` relative position (GameState.cs:181 and :211)** —
  `side == 1 ? u.Position / MAP_WIDTH : (MAP_WIDTH - u.Position) / MAP_WIDTH`. Both read
  the left edge, so mirrored boards do **not** produce mirrored observations: a side-1
  unit at `p` reports `p/2000` while its true mirror (a side-2 unit at `2000-p-W`) reports
  `(p+W)/2000`. The correct side-2 form is `(MAP_WIDTH - u.Position - u.Width)/MAP_WIDTH`.
  Deferred because it shifts the 348-float observation and so invalidates every trained
  checkpoint — do it at a retrain boundary, not mid-campaign.
- [ ] **`HeuristicBot` proximity thresholds** — `Math.Abs(u.Position - enemyCastlePos) <
  AttackEngageDistance` (HeuristicBot.cs:1655) and the `myCastlePos` reads around
  HeuristicBot.cs:1173 compare left edges against a castle x. For the same physical
  distance `d` from the wall, side 1 evaluates `d + W` and side 2 evaluates `d`, so the
  two seats flip "pushing the enemy castle" at different real distances. This is bot
  policy rather than engine truth, but it is the most likely remaining cause of the
  knife-edge result below.
- [ ] **`WallEffect` level-1 position (WallEffect.cs:21)** — `side == 1 ? 600 : 1400`.
  Same issue: the mirror of 600 is `1400 - W`.

**Instrument note.** A same-loadout, same-team HeuristicBot mirror match is a *knife-edge*
seat-asymmetry detector: mirrored play is deterministic, so any nonzero residual makes one
seat win 100% of games, and a harness that updates P1 before P2 each tick is itself part of
the residual. It reports the **sign** of the net asymmetry, never its magnitude — it read
P2 300/300 before any fix, P1 300/300 after the castle fix alone, and P2 100/100 after all
three. Do not read that as "no progress": the direct geometric measurements above are what
show the engine is now symmetric. For magnitude use random non-mirrored loadouts, where
P1's win share moved 48.2% → 48.5% → 46.8% across the same three states, all within the
±2.5pp standard error of 400 games.

## Known limitations, documented rather than fixed

- [ ] **The ladder's mirror rung cannot detect seat bias.** The contender always plays
  loadout A and the opponent loadout B, with only the *seat* swapped, so
  HeuristicBot-vs-HeuristicBot measures loadout-draw asymmetry instead. Detecting seat
  bias needs a same-loadout mirror rung. Not urgent — the ladder is seat-balanced by
  construction, so seat bias cannot contaminate its results either way.
