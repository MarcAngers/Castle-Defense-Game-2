# The Singleplayer bot, as configured 2026-08-23

Recorded because this configuration is beating Marc **68% of the time** and the previous
recorded baseline had him at 92.1% against what is, in code terms, the same bot.

## The exact setup

**Seat 2 opponent:** `new HeuristicBot(2)` — plain, stock `HeuristicBotSettings.Default`.
Constructed in `GameHostingService.SetupHeuristicOpponent`, which is the *identical*
construction `CastleDefense.BotArena`'s `bot-checksum` uses for seat 2. Not the search bot,
not ONNX. Selected by:

```json
// CastleDefenseGame2/appsettings.json
"Singleplayer": { "Opponent": "heuristic" },      // "search" restores the flagship
"CounterPick": {
  "Enabled": true,
  "TopK": 1,
  "ForcedLoadout": "White,nuke,reinforcements"     // overrides the counter table
}
```

**Loadout:** `ForcedLoadout` pins the bot to **White / nuke / reinforcements** every game,
bypassing both the counter table and the random roll. The human picks freely and first.

**No headstart.** `CreateGame` uses a plain `new GameState()`; the `timeSkip` free-investment
path belongs to league mode only.

**Decision cadence:** `DecisionIntervalTicks = 5` → 6 decisions/sec, one purchase each.

**Relevant defaults confirmed:** `UnifiedTimeToDeath = true` (so the repair gate uses the
sentinel-aware max of the observed-drain and geometric estimators, NOT the contact-only
`EstimateProjectedThreatDps`), `RepairTtdSeconds = 5.5`, `DefenceOnly = false`.

The Acceptance Test is deliberately unaffected and still faces the search bot.

## THE BOT IS NOT NEW. THE LOADOUT IS.

This is the same HeuristicBot that has been in the repo throughout. What changed is that it
now plays White/nuke/reinforcements every game. That is the counter-matrix result
(`CLAUDE.md`, "Loadout counter-picking") landing against a human for the first time:
loadout choice was measured as a bigger lever than the entire search programme, and this is
what that looks like from the other side of the screen.

**Caveat on the 32%.** Marc played Blue in 13 of the 28 games and Blue is one of the two
known-weak teams. Split by his team:

| Marc's team | W | L | rate |
|---|---|---|---|
| White | 4 | 2 | 67% |
| Orange | 1 | 1 | 50% |
| Blue | 4 | 9 | 31% |
| Purple / Green / Yellow / Black | 0 | 7 | 0% |

So part of the collapse is matchup selection, not bot strength. The White-on-White mirror —
the fair comparison — is 67% for Marc, still far below 92.1% but not 32%.

## What separates a win from a loss (n=27 rebuilt replays)

| | Marc won | Marc lost |
|---|---|---|
| game length | 277s | 233s |
| **bot final max HP** | **64,500** | **30,105** |
| bot peak units on field | 140 | 131 |
| MARC investments | 7.9 | 7.2 |
| MARC unit spend | $11,969 | $19,275 |
| bot money left at end | $26,809 | $7,729 |

**Reaching investment 8 is very nearly the whole game for Marc:**

| Marc's investments | record |
|---|---|
| 5–7 | **1W / 11L** (8%) |
| 8 | **7W / 8L** (47%) |

And he spends *less* on units in the games he wins — the same shape as 9A9A41.

## MARC'S TWO REPAIR OBSERVATIONS: BOTH CONFIRMED, ONE ROOT CAUSE

The shipped bot's entire repair rule is:

```csharp
bool worthRepairing = !_settings.DefenceOnly || RepairBuysItsPrice(...);   // TRUE for shipped
if (timeToDeathSeconds < 5.5 && me.Money >= me.RepairPrice && worthRepairing) Repair();
```

`!DefenceOnly` short-circuits to **true**, so the price check built for the defence-only bot
is **disabled on the shipped bot**. The gate is a pure rate-based time-to-death threshold with
**no price awareness and no absolute-HP floor**. Each failure mode is one of those two
omissions.

### Observation 1 — it lets itself die while dominating

`061479` (Marc won): the bot sat at **822/2,000 HP for eleven seconds**, with **138 units on
the field** against Marc's 4, while its bank grew from $42,096 to $67,745. Repair #0 costs
**$8**. It never repaired, all game, and died on the base castle.

`0BA923` (Marc won): **377/2,000 for twelve seconds**, **190 units** on the field against
Marc's 3, $9,237 → $15,141 in the bank. Zero repairs. Died.

**Why:** a rate-based time-to-death cannot see an absolute floor. With its own army winning
overwhelmingly, Marc's damage arrives as rare single hits, so the observed drain rate is tiny
and TTD reads long — correctly, on average. But at 297/2,000 the *next* hit is lethal
regardless of rate. This is exactly the Blue/Wave case: the wave pushes the line back, one
hit lands, and the bot's model says it is fine because usually it is.

**The missing rule is trivially cheap:** below some absolute fraction of max HP, repair while
the price is small. It is $8.

### Observation 2 — it panics and dumps the treasury

The repair price ladder is viciously superlinear:

| repair | #0 | #1 | #2 | #3 | #4 | #5 | #6 | #7 | #8 |
|---|---|---|---|---|---|---|---|---|---|
| price | $8 | $26 | $66 | $169 | $493 | $1,796 | $8,837 | **$63,195** | $1,409,308 |

`1269AD`: repairs #5, #6, #7 fired at **323.7s, 323.9s, 324.0s** — three repairs in
**0.3 seconds** for **$73,828**, taking it from $69,902 to $369.

`A8C7AC`: five repairs between 282.2s and 282.9s — **0.7 seconds, $74,490**, from $108,708
down to $37,386.

The bot has no notion that repair #7 costs 7× repair #6. As long as `money >= repairPrice` and
TTD is under 5.5s, it fires again on the very next decision, 0.17s later, before the previous
repair has changed anything about the threat.

**Aggregate:** in the games Marc won, the bot repaired 5.8×/game for $40,146 — **52% of its
total outlay**. In the games he lost, 2.6× for $4,739 (11%).

**Read that association carefully.** The bot repairs hardest when it is under most pressure,
and it is under most pressure when it is losing, so repair-heavy → loss is partly reverse
causation. What is *not* ambiguous is the specific pathology: spending $74,000 in 0.3 seconds,
most of it on a single $63,195 repair, is not a defensible response at any threat level, and
it is the difference between a close game and a free win for Marc.

## The fix these point at

One change addresses both, because both are the same missing idea — **the repair gate knows
about time but not about money or absolute health**:

1. **Enable the price check for the shipped bot.** `RepairBuysItsPrice` already exists and
   already measured well on the defence-only path (repair spend $11,398 → $2,560 on the traced
   mirror). It is disabled here purely by the `!DefenceOnly` short-circuit.
2. **Add an absolute-HP floor** that fires cheap repairs regardless of the rate estimate.
3. **Rate-limit the burst** — one repair per threat re-evaluation, not one per decision tick.

Item 1 is a one-line change with existing evidence behind it. It should be measured on the
shipped bot before anything else here, because it is also the change most likely to alter the
shipped fingerprint, and every benchmark in this project is anchored to that.

## Balance dashboard + the pinned loadout, rebuilt 2026-08-23

Both runs use the bot now in Singleplayer: plain `new HeuristicBot(2)`, stock settings.

### `dashboard` — 21,760 games, 128 cells x 23 opponents

```
CastleDefense.BotArena.exe dashboard            # --bot heuristic is already the default
```

HeuristicBot wins **95.5%** overall against the spam ladder and every ONNX checkpoint.

| team | | offense | | defense | |
|---|---|---|---|---|---|
| Purple | 98.8% | freeze | 97.2% | reinforcements | 96.6% |
| Black | 98.5% | nuke | 96.1% | heal | 96.3% |
| Orange | 97.3% | firebomb | 95.4% | wall | 95.0% |
| Blue | 96.4% | snipe | **93.1%** | speed | **93.9%** |
| White | 96.2% |
| Green | 95.0% |
| Red | 94.3% |
| Yellow | **87.2%** |

Hardest opponents: **Tier4Spam 85.2%**, then `v4` at 92.6%. Nothing else is under 93%.

**THAT TABLE IS NEARLY USELESS FOR BALANCE, AND THE MIRROR IS THE REASON.** Every one of those
23 opponents is a spam tier or an ONNX checkpoint — opponents HeuristicBot beats 93–99% of the
time. Against that spread, "the opponent is bad" dominates every cell and a real team or gadget
edge cannot show through. It also produced a wrong headline: Yellow looked like the weak team at
87.2%, which is an artifact.

### The mirror arm — added 2026-08-23, and it was missing entirely

The sweep had **no mirror at all** for a heuristic protagonist. The only mirror arm,
`SearchMirror`, is off by default *and* runs the search bot. `HeuristicMirror` now runs by
default (3,200 games, 25/cell, seats alternated — mandatory here, since a near-mirror is decided
by the seat). The opponent still draws a random loadout, so this reads "this kit against a
competent bot", not loadout-vs-loadout.

It sits at **49.9% overall**, as a mirror must, and the spread explodes:

| | mirror | all other opponents |
|---|---|---|
| White | **83.5%** | 95.8% |
| Yellow | 54.8% | 87.8% |
| Black | 54.2% | 98.5% |
| Orange | 47.5% | 97.4% |
| Purple | 46.8% | 98.3% |
| Blue | 45.8% | 96.0% |
| Red | 40.0% | 93.1% |
| Green | **26.5%** | 93.3% |
| | | |
| freeze | 57.1% | 96.9% |
| nuke | 54.2% | 95.7% |
| firebomb | 53.0% | 94.3% |
| snipe | **35.1%** | 93.2% |
| | | |
| reinforcements | **62.0%** | 95.9% |
| wall | 59.5% | 94.7% |
| heal | 52.1% | 96.2% |
| speed | **25.9%** | 93.3% |

Team spread goes from 11 points to **57**; defence from 3 points to **36**.

**It replicates the counter-matrix sweep, which is a different harness with a different design
(fixed seats, loadout-vs-loadout, common random numbers).** Spearman rho **+0.90 on teams**,
+0.80 on offence and defence, +0.80 across all 16 slots. Green worst (26.9 vs 26.5), White best
(78.3 vs 83.5), snipe worst offence (33.9 vs 35.1), speed worst defence (36.9 vs 25.9). Two
instruments that share no code agreeing to that degree is the strongest validity evidence either
of them has.

**Corrected:** Green is the weak team, not Yellow. Yellow's 87.2% in the spam arms was the
artifact; in the mirror it is mid-table at 54.8%, and the counter-matrix agreed (57.1%) all
along.

### What beats White/nuke/reinforcements

```
CastleDefense.BotArena.exe counter-eval --games 200 --fixed White,nuke,reinforcements     --out counter/vs_pinned.csv
```

All 128 human loadouts in seat 1 against the pinned bot in seat 2, 200 games each, fixed seats
and no headstart — the deployed `sp` configuration.

**Exactly two loadouts beat it, and both beat it every single game:**

| human loadout | bot wins | human wins |
|---|---|---|
| **White / firebomb / reinforcements** | **0.0%** | **100.0%** |
| **White / freeze / reinforcements** | **0.0%** | **100.0%** |
| White / nuke / reinforcements (the mirror) | 50.0% | 50.0% |
| *every other cell (125 of them)* | 100.0% | 0.0% |

Mean 98.0%, median 100%. This is the deterministic-hole pattern the counter-matrix work
predicted for any fixed loadout: a very high average hiding a handful of cells that lose
outright. Here the holes are unusually clean — the whole matrix is 100% except three cells.

Marginals, lower being better for the human: **White 84.4%** and every other team 100.0%;
**reinforcements 92.2%** and every other defence 100.0%. The counter is White plus
reinforcements plus an offensive gadget that is not nuke.

**Marc has been playing Blue** (13 of 28 games), which is one of the 100% cells. He still wins
31% of those, which is a measure of how much better a human is than HeuristicBot-in-seat-1 —
but he is choosing a loadout the sweep says never wins. His freeze/reinforcements instinct is
right; the team is not. **White/freeze/reinforcements is the same gadget pair on the team that
turns it into a 100% counter.**

**Caveat, and it is the same one the counter table carries.** Both seats are HeuristicBot, so
these are not predictions about Marc's win rate — a human plays nothing like the bot whose
0%/100% cells these are. What transfers is the ordering and the existence of the holes, not the
absolute rates. Treat "play White/freeze/reinforcements" as a hypothesis to test at the
keyboard, not a guarantee.
