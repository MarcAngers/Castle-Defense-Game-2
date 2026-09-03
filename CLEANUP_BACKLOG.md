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

- [x] **DONE 2026-08-29 - `CastleDefense.Simulation/Program.cs` stale `ScheduleAction` names.**
  The comments explaining why `CloneStateForShadow` can shallow-copy `Hazards` referred to
  `engine.ScheduleAction`, which no longer existed. Fixed as a side effect of deleting the
  legacy scheduler (below): the names now read `ScheduleEffect`, with a note recording the
  rename so the reasoning stays traceable. The reasoning itself was correct and is
  unchanged - deferred effects never fire on the shadow clone because the clone engine is
  discarded before `Tick()`, and the shallow-Hazard caveat still becomes live the moment any
  gadget creates a hazard synchronously.

- [x] **DONE 2026-08-29 - `HeuristicBot` "THE RULE" paragraph documented the ABANDONED wiper
  design.** (It had drifted to line 857 - the filed line numbers were stale too.) Rewritten
  to describe the shipped standalone economic test: cheapest unit that one-shots the
  toughest committed enemy, bought when it costs at most `WiperMaxCostVsStackValue` (0.35)
  of the stack's value, bounded by `WiperMinIntervalSeconds` (4.0). It is NOT priced against
  `RepairPrice` and NOT gated on a repair being about to happen. The paragraph now says
  outright that the "right before repairing" framing is the abandoned design, so a reader
  landing on the heading cannot pick up the wrong model of the gate.

- [x] **ALREADY FIXED - verified 2026-08-29 - `.replay` END-OF-GAME loadout.** Closed by the v3
  replay format; the entry was simply never ticked. `GameRecorder` takes and writes an
  explicit START loadout (its header comment carries the FC1462 nuke_3 story), and
  `ReplayFile.BuildStart` equips `P1StartOff/Def/Sig` when `HasV3`. Residual, already
  documented in code rather than here: **v2 files still carry only the end loadout** and
  `BuildStart` falls back to it, so callers working with v2 recordings must keep stripping
  the tier suffix themselves. Nothing to do unless v2 files are re-analysed.

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

- [x] **DONE 2026-08-29 - 11 abandoned games in `recordings/game_records.db`.** All 11 quarantined
  ids were still present as live rows (all `winner = 2`, `end_reason` NULL, so they read as
  ordinary losses). Now flagged `end_reason = 'abandoned'`.
  **The flag was the right option rather than deletion, and it got cheaper than when this
  was filed:** the `end_reason` column now exists (added by the disconnect/rejoin work) and
  `ReplayFile.IsRealResult` already treats `abandoned` as not-a-real-result, so every tool
  going through corpus selection inherits the exclusion with no code change, while the rows
  survive for anyone passing `--all` deliberately. DB backed up first to
  `game_records.db.bak-20260829`. Detector, still useful:
  `CastleDefense.PythonAI/inspect_replays.py` (read-only unless `--delete`).

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

- [x] **DONE 2026-08-29 - `GameEngine.ScheduleAction`, `_scheduledEvents`, and the private
  `ScheduledEvent` class.** Deleted, along with the `Clone()` guard that threw on a pending
  legacy event and the drain loop in `Tick()`. Verified zero callers first. The surviving
  `_scheduledEffects` loop kept its downward-iteration comment, restated to say why the
  direction is load-bearing: an effect scheduled while firing must run on a LATER tick, and
  `FreezeEffect`'s level-3 slow now depends on exactly that.

- [ ] **`RusherBaseline` - NOT dead; re-verified 2026-08-29, filed under the wrong heading.** It
  was dropped from the *ladder* as a duplicate of `Tier1Spam` (it buys "cheapest affordable",
  which is always the tier-1 unit), but it is still referenced from six live places in
  BotArena: the model-lookup fallback and the `"rusher"` name in `Program.cs`, two opponent
  pools, and a `RunMatchup`. So this is a **decide whether to keep it**, not a deletion -
  removing it means rehoming those six callers onto `Tier1Spam` and accepting that
  historical "vs Rusher" rows then describe a renamed opponent.

- [x] **DONE 2026-08-29 - `GameEngine.AttackCastle`.** Deleted; verified zero callers. It was
  worse than merely dead, and the reason is worth keeping: its copy of the cooldown line
  never received the `def.AttackSpeed > 0` guard that the inlined version in `MoveAndFight`
  got on 2026-08-04, so calling it on a wall (AttackSpeed 0) produced
  `float.PositiveInfinity` and crashed live games by making the state unserialisable - and
  it read damage off the DEFINITION, so it would additionally have missed the weirdo's
  per-instance roll added later. The five comments across `HeuristicBot`, `GameEngine` and
  `RepairAudit` that named it now point at the castle-damage branch of `MoveAndFight`, which
  is the only place a unit damages a castle.

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
- [x] **DONE 2026-08-29 — `WallEffect` level-1 position.** It read `side == 1 ? 600 : 1400`,
  reflecting the COORDINATE instead of the GEOMETRY. `Position` is the sprite's left edge,
  so the mirror of a left edge at 600 is `MAP_WIDTH - 600 - Width`. Now computed that way,
  with the width read from `GameDataManager.WallDefinition(1)` (the same definition the
  spawn path caches as `_unitCache["wall"]`) rather than hardcoded, so a change to the
  wall's size cannot reopen it.

  **Measured, both seats, same quantity** — the gap between the caster's own castle wall
  and the near edge of the wall it placed:

  | | left | right | width | gap from own castle |
  |---|---|---|---|---|
  | P1 (before and after) | 600 | 675 | 75 | 400 |
  | P2 **before** | 1400 | 1475 | 75 | **325** |
  | P2 **after** | 1325 | 1400 | 75 | **400** |

  So P2's wall used to stand 75px closer to its own castle than P1's did — a third of the
  225px of open ground the wall is meant to leave, and 18.75% of the gap. The spans are now
  exact reflections through the map centre, checked as a separate assertion rather than
  inferred from the gap alone. Reinstating the old line makes both checks fail, so the test
  is sensitive rather than vacuous.

  **This is a BALANCE CHANGE for P2**, small but real: their level-1 wall now spawns 75px
  further out. A/B on `mirror-fixed White nuke wall`, n=40 per arm, both arms built and run
  in the same session: control 0 P1 / 1 P2 / 39 draws (262.9s avg), fixed 0 P1 / 3 P2 /
  37 draws (263.3s avg). Read that as "no detectable swing" and NOT as "P2 got stronger" —
  n=40 across randomly rolled maps cannot separate 1 from 3, and the mirror is documented in
  this file as a SIGN detector, not a magnitude one. The case for the change is the direct
  geometric measurement above, not the win counts.

  This was the last item on the 2026-07-31 left-edge list that is not blocked on a retrain.

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

- [ ] **`WiperOverRepair` silently depends on `WaveWipeValue`, so the `NoWaveWipe` arm
  moves two flags.** `committedEnemyCount` / `committedEnemyValue` are only populated
  inside the `if (_settings.WaveWipeValue)` block, and the wiper gate (the
  `_settings.WiperOverRepair && committedEnemyCount > 0` condition) requires both to be
  non-zero. So the `NoWaveWipe` preset disables the wiper purchase entirely while
  `WiperOverRepair` still reads `true` — nothing in either flag's own comment says so.
  (Line numbers removed 2026-08-29: all three this entry cited had drifted by roughly a
  thousand lines. Anchor on the symbol names instead.) Any A/B using
  `NoWaveWipe` as an arm is measuring the budget-raise and the wiper together, and the
  attribution silently lands on the wrong flag. This is the file's own failure mode in
  code rather than prose: the coupling produces a number once and there is no test that
  would notice. Cheap fix is to hoist the stack scan out of the `WaveWipeValue` guard so
  the two flags read the same inputs independently; that changes `NoWaveWipe`'s measured
  values, so re-measure it rather than assuming the old row still stands.

- [ ] **The ladder's mirror rung cannot detect seat bias.** The contender always plays
  loadout A and the opponent loadout B, with only the *seat* swapped, so
  HeuristicBot-vs-HeuristicBot measures loadout-draw asymmetry instead. Detecting seat
  bias needs a same-loadout mirror rung. Not urgent — the ladder is seat-balanced by
  construction, so seat bias cannot contaminate its results either way.

- [x] **DONE 2026-08-29 - `ClonePlayerState` hand-written field-by-field copy.** Now
  `static PlayerState ClonePlayerState(PlayerState src) => src.Clone();`.
  **This entry's own prediction had already come true again.** When it was written the
  missing field was `ArmageddonUsed` (fixed 2026-08-24); by the time it was actioned the copy
  had silently lost **`CastleShield`**, so every `--trace-human` re-simulation ran the shadow
  player with no absorbing shield and charged its castle for damage the real one had
  absorbed. Delegating to the `MemberwiseClone`-based `PlayerState.Clone` closes the class of
  bug rather than the instance.

## Duplicated dance routine (added 2026-08-25)

The beat-based dance (flip facing on the beat, small hops with a bigger jump every third
beat, a hold after the big jump so the turn doesn't land on the same frame as the unit)
now exists twice: `wwwroot/src/menu-meander.js` for the main-menu wanderers and
`wwwroot/src/end-game-show.js` for the winning team's party. The constants were copied
across, so tuning one will silently leave the other behind.

They were left separate because the two drive different things -- meander owns its units'
x/y outright, while the end-game show writes offsets onto VisualUnits layered over a frozen
server state -- and extracting the shared part would have meant reworking meander, which was
already tuned and signed off. Worth pulling into one small routine that emits
{facing, hopOffset, swayDelta} for a given elapsed time, with both callers applying it their
own way.

## Shop unit buttons only respond to a click on the IMAGE - FIXED 2026-08-29

`game.js` wired the click on `.character` but resolved the unit from
`e.target.parentElement.id`, so it only worked when the event target was the `<img>`. A tap
on the div's own area resolved `parentElement` to `.character-icon-wrapper`, whose id is
empty, and `spawnUnit("")` silently no-opped - no unit, no money spent, no error.

Now reads `character.id`, the element the listener is attached to. Independently
re-encountered 2026-08-29 while driving the shop from script, which is the same way it was found
originally: a programmatic `.character` click bought nothing and cost nothing.

## Dead unit data under `wwwroot/static/` - DELETED 2026-08-29

`static/fullteams/*.json` and `static/characters/<team>/*.json` - 73 files, 113 KB - were
read by nothing (no importer or fetch referenced either path) and their stats contradicted
`master_roster.csv` outright (evilnom.json said price 8 / health 18 where the roster says
25 / 82), with team lists not even in tier order. Deleted rather than wired up: a second
contradictory copy of the roster is how the next person gets the wrong numbers.

## Dead code sweep 2026-08-29

A reference sweep over the whole solution (declared members counted against every
occurrence outside their own declaration, plus a wwwroot pass matching each file by
name, path and stem). Recorded here because the *method* is reusable and because two
of the findings were invisible to every other check the project runs.

### Removed

- **`CastleDefense.Server/` and `CastleDefense.Shared/`** - two entire projects, deleted.
  Both were untouched `dotnet new` scaffolding: Server was the default Web API template
  (`WeatherForecast`, `WeatherForecastController`, OpenAPI wiring, 62 lines of C# total),
  Shared was a single empty `public class Class1 {}`. **Neither was in
  `CastleDefenseGame2.sln`**, so neither was ever built by `dotnet build` at the repo root,
  and no csproj referenced them - which is exactly why they survived so long: nothing that
  compiles, runs or tests the project ever touched them. Confirmed by two independent
  searches (ripgrep and a full repo-wide grep including binaries) that no reference existed
  outside their own directories. The real web server is `CastleDefenseGame2`.
- **`static/views/singleplayer.html`** - a 6-line stub ("SELECT OPPONENT:", Back/Select)
  that no router entry loaded. The live flow is select-team -> select-loadout ->
  select-level. Its root element id was misspelled `singplayer`, so it likely never worked.
- **`static/views/view-styles/main-menu.css`** - a ZERO-byte file. View stylesheets are
  loaded from an explicit `<link>` inside each view's HTML (`router.js` splits it off before
  injecting markup), and `main-menu.html` has no link - its styles live in
  `global-styles.css`. Nothing could ever have fetched it.
- **`assets/env/background-white.png`** - orphan of an older flat naming scheme. The loader
  reads `../assets/env/${colour}/background.png` from per-colour SUBDIRECTORIES, so a flat
  file at the top of `env/` is unreachable by any code path.

Verified after removal: solution rebuilds clean (`--no-incremental`), `clone-check` passes
all four checks, and the whole singleplayer flow (main-menu -> select-team -> select-loadout
-> select-level -> game) serves every view and asset with no 404 and no request for any
deleted path. All removed files were git-tracked with no uncommitted changes, so every one
is recoverable with `git checkout HEAD -- <path>`.

### Still open - flagged, not actioned

- [x] **KEPT, deliberately - decided 2026-08-29 - `GameHostingService.SetupLeagueOpponent` has zero
  callers** (~40 lines: a 16-way bot selection with a spam tier derived from `timeSkip`).
  Marc's call: leave it, it may be wanted again. Recorded here so the next dead-code sweep
  does not re-raise it as a finding -- it is unreferenced ON PURPOSE, not by accident.

- [x] **DONE 2026-08-29 - `HeuristicBotSettings.WipeLeadMarginSeconds` deleted.** Declared `3f`,
  documented as headroom on the wipe deadline guard, and read by nothing -- its sibling
  `WipeLeadSeconds` is the one the guard actually subtracts. The comment left in its place on
  `WipeLeadSeconds` says where the headroom belongs if it is ever wanted (widen that constant,
  or add the margin AT the subtraction) and why NOT to reintroduce a settings field nothing
  consumes: a knob a sweep can randomise but no code reads reports "this parameter does not
  matter" when the truth is "this parameter is not connected".

- [ ] **`wwwroot/assets/env/maps.xcf` is a 2.5 MB GIMP source file in the served web root.**
  Not dead code - it is the working file for the map art - but it is publicly downloadable
  and ships with every deploy. Belongs outside `wwwroot`.
- [x] **DONE 2026-08-29 - `view.js` threw `drawImage: The provided value is not of type ...` on a
  clean page load.**

  **Cause, traced rather than guessed.** `resize` is registered in the View constructor, which
  runs at MODULE IMPORT time -- before `script.js` has even started `loader.loadAll()`.
  `script.js` is careful to call `view.resize()` only after `await assetsReady`, but the
  LISTENER is already live before that, so any resize event during the asset load (a tab being
  laid out, a phone rotating, a window dragged while the loading screen is up) reached `draw()`
  with nothing loaded. With no `latestState` and no `mapColour`, `draw()` takes its 'white'
  fallback, and `loader.get('background')['white']` was undefined.

  Confirmed, not inferred: a missing key throws a message byte-identical to the observed one,
  and after load all eight backgrounds are present -- so the throw can only have happened
  inside the load window. Reproducible on a clean tab load.

  **Fixed by guarding the layer fetch** (`#mapLayer`), which returns null and warns ONCE per
  bucket+colour instead of throwing. The guard is worth more than silencing a console line:
  `drawBackground` runs EARLY in `drawGameState`, so a throw there aborts the whole frame --
  no atmosphere, no foreground, no units, no castles. Missing art now degrades to a missing
  backdrop rather than a blank screen. Verified: the uncaught error is replaced by exactly two
  one-time warnings (`background/white`, `foreground/white`) that name the real cause, and a
  live game at 812x375 draws background and foreground correctly with zero console errors.

  **Worth noting how it was nearly missed.** It was first dismissed as an artifact of the
  absurd 320x40 viewport used to force the star-burst bug. It reproduces at 1280x800 and on a
  default tab. A console error seen only while testing something else is still a real error.

### Deliberate but unused - flagged only, no action proposed

Test hooks and diagnostics with no current caller, all documented as such where they are
declared: `HumanCloneBot.InvalidateTable` ("Test hook"), `CounterPicker.Ranked`
("Diagnostics"), `HeuristicBot.LastBlockCredit` and `RolloutSearchBot.OpponentCommitted`
(both "Diagnostic."), `ReconnectService.OccupiedHumanSeats`, and five computed-never-read
statistics: `Ladder.Record.WinRate`, `Divergence.Spend.TotalUnits`, and
`RecentWinrate` / `TotalWinrate` / `BaselineWinrate` in `Simulation/Program.cs`.

### False positives worth recording, so the next sweep does not re-raise them

Anything invoked by NAME rather than by a compiler-visible reference looks dead to a sweep
like this: the six SignalR hub methods (`JoinGame`, `JoinPracticeGame`, `CheckRejoin`,
`RejoinGame`, `AbandonGame`, `ClaimVictory`) are called from `game-connection.js` as strings;
`GameController.GetPracticeOpponents` is reached by route (`game-connection.js:513`);
`GameHostingService.ExecuteAsync` is a `BackgroundService` override called by the framework;
and all eight `*-castle.png` sprites are loaded through a template
(`` `${colour}-castle.png` ``) rather than by literal name. Every JS module under `src/` and
`view-logic/`, and every remaining view stylesheet, is referenced.

## Temporary map pin -- the entry three code comments already point at

**Added 2026-08-29.** `CastleDefenseGame2/Program.cs` and `GameHostingService.ForcedMap` both say
"see CLEANUP_BACKLOG.md", and there was no such entry -- the pointer had been dangling since
2026-08-27. Writing it rather than deleting the pointer, because the temporary code is still
in place.

- [ ] **`Map:ForcedMap` and `GameHostingService.ForcedMap` are temporary scaffolding from the
  map-atmosphere work (2026-08-27).** They pin every hosted game to one map so a map's
  ambient animation can be looked at on demand. **Currently EMPTY, so the roll is normal** --
  the risk is not that it is on today, it is that it is gameplay-affecting when it is on and
  nothing except a startup log line says so. Remove the knob once the atmosphere work is
  finished, or keep it and delete these three "temporary" claims.

## Live configuration worth a decision (noted 2026-08-29)

Not code rot -- a setting that is currently on and may no longer be wanted.

- [ ] **`CounterPick:ForcedLoadout` is set to `White,nuke,reinforcements` in
  `appsettings.json`.** Per CLAUDE.md this exists so Marc can record MIRROR games, and it is
  explicitly "not a strength setting -- it reopens the deterministic holes counter-picking
  closed. Clear it for normal play." Two reasons to look at it now:
  its purpose is **no longer achieved** -- CLAUDE.md's own "STALE SINCE 2026-08-27" note says
  pinning the loadout stopped being enough to pin the mirror once maps carried effects, and
  `Map:ForcedMap` is empty, so every game still rolls a different rule set; and meanwhile it
  is still paying the cost, since the bot plays one fixed loadout in every singleplayer game
  and the five hard-counter cells are open again. Either clear it, or set `Map:ForcedMap` to
  one of the maps the mirror survives on and finish the recordings it was set up for.

## Comment audit 2026-08-29

A pass over comments for claims that no longer hold, run the way the rest of this file
argues for: mechanically, by checking each claim against the code rather than by reading.
Three methods, in decreasing yield -- identifiers named in comments that no longer exist
anywhere in code; numbers quoted in comments compared against the constants they describe;
and TODO/"for now"/"temporary" markers checked against whether the temporary thing is still
temporary.

Fixed: the two `ScheduleAction` references in the shadow-clone comment; five comments naming
the deleted `AttackCastle`; `ActiveStatus.Name`'s doc listing `"SpeedBuff"`, a status that
has never existed (the real set is Blackhole, Burn, Freeze, Heal, Invulnerable, Knockback,
Poison, Rage, Slow, Speed, Stun); `Hazard.Type`'s doc listing `"Ice"` and `"PoisonCloud"`,
neither of which exists (the real set is Blackhole, Fire, Goo, Poison, Wave); `asset-loader`
claiming the map Effect column is "expected to be BLANK for now"; and, in CLAUDE.md, the
claim that `_scheduledEvents` still exists, a corpus count of 153 replay files that is now
267, three drifted `file.cs:NNN` references, and the map name "Rumbling Volcanoe".

**Two lessons worth keeping.** First, a comment can go stale in its REASONING while its
conclusion stays right, and that is harder to spot than a wrong number: the
"claim the investment first" paragraph in `HeuristicBot` blamed the overshoot on prices "not
being round numbers", which whole-dollar pricing made false, even though the overshoot it
defends against still happens -- for the other reason, fractional income. Second, **line
numbers in prose are a rot generator**: every `file.cs:NNN` reference checked in this pass
had drifted, several by more than a thousand lines. Prefer a symbol name; it moves with the
code.

## Two wave defects, found 2026-09-03 and deliberately left alone

Both surfaced while adding the knockback cap to `WaveHazard`. Both are BALANCE-affecting, so
neither was fixed in the same pass — a silent extra change would have muddied the in-game
testing the cap was built for. Fix them deliberately, and re-run `bot-checksum` when you do.

- **`knockbackDist` is not restored after a tier-8 unit.** `WaveHazard.ProcessEffect` does
  `if (enemy.Tier == 8) knockbackDist = 25;` inside its loop over enemies and never puts the
  value back, so **every unit processed after the first tier-8 in that tick is launched 25
  instead of the level's real distance** (500 / 1500 / 3000). It is order-dependent — it
  follows `state.Units` order — and completely invisible unless a tier 8 is caught in the
  wave. The fix is a local inside the loop; the question to answer first is whether any
  existing balance measurement was taken with it active.

- **Level 2's knockback disagrees with its own CSV row.** `master_gadgets.csv` gives
  `wave_2` a KNOCKBACK of **1000**, which is what the Collection screen prints to the player;
  `WaveHazard.ProcessEffect` hardcodes **1500**. Levels 1 and 3 match their rows (500 and
  3000), so this is a single wrong number rather than a wholesale disconnect. Note the new
  knockback CAP *does* read the CSV (`WaveEffect.CapFor` uses `BaseValue / 10`), so the two
  halves of the same gadget are now sourced differently — which is the real reason to close
  this rather than the discrepancy itself.
