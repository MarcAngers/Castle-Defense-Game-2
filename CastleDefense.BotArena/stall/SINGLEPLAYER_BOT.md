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

---

## THE RUNG-7 STALL: it is `killerInstinct`, and the bot thought it was winning

Game `0A7658`. The bot led every rung through 6 (9/21/38/53/78/114s against Marc's
9/21/39/59/81/117s), then took **112 seconds** on rung 7 where Marc took 33, and never bought
another. Asked directly — via a shadow HeuristicBot run on a clone of the engine each tick, so
its reasoning can be read without its decisions touching the replay — it names the branch:

```
  146.0s  P2 would spawn  reason=attack           money=$7362  investPrice=$8080  own=62  foe= 0
  146.2s  P2 would spawn  reason=killerInstinct   money=$5296  investPrice=$8080  own=63  foe= 1
  146.3s  P2 would spawn  reason=killerInstinct   money=$3230  investPrice=$8080  own=64  foe= 1
  153.2s  P2 would spawn  reason=killerInstinct   money=$2573  investPrice=$8080  own=11  foe= 0
  153.3s  P2 would spawn  reason=killerInstinct   money=$ 507  investPrice=$8080  own=12  foe= 1
```

**At 146.0s it held $7,362 and rung 7 cost $8,080 — it was $718 short, under three seconds of
income.** It then bought four tier-7 units at $2,066 each across two bursts, $8,264 total, more
than the rung it was three seconds from affording.

### What it was thinking, in its own terms

```csharp
float ownPushDps = EstimateOwnCastleDps(engine, myUnits, enemyUnits);
killerInstinct = ownPushDps > 0.01f
              && enemy.CastleHealth / ownPushDps <= _settings.KillerInstinctSeconds;   // 12s
```

The comment above it reads *"we are N seconds from winning, stop saving and close it out."*

At 146s the bot had **62 units on the field and Marc had zero**. `EstimateOwnCastleDps` counts
own units that are in contact with the enemy castle *and not engaged with enemy units* — with no
defenders present, nothing was filtered out, so the estimate was huge and the bot concluded it
was inside twelve seconds of victory. By its own rule that is precisely when saving stops.

**The bot's own dominance is what triggered the spending.**

### The four things wrong with that

1. **It assumes the push persists.** There is no term for the defender clearing it. Marc did:
   the bot's army went **64 → 11 units between 146.4s and 153.2s**.
2. **It re-fires after the push dies.** At 153.2s, down to 11 units, it triggered again and spent
   to $489.
3. **It bypasses every spending limit.** `killerInstinct` skips both the attack flow-rate cap and
   the reactive budget — `if (!preferDefense && !killerInstinct)` and
   `else if (preferDefense && !killerInstinct)`. That is how $4,132 left the balance in 0.2s when
   the banked attack allowance was about $1,062 ($42.5/s flow × 25s cap).
4. **It cannot price what it is giving up.** `DeferForInvestment`, the only hard
   "stop spending, you are saving" rule, is `me.InvestmentCount < 3` — it does not exist at
   rung 7.

### Why this is the right thing to fix next

The stall is not repairs (this game: 5 repairs, $764) and not the attack budget (which was
correctly throttling at ~$42/s). It is one rule that switches the discipline off entirely,
triggered by a state the bot reaches *because it is winning*, using an estimate that ignores the
only thing that can take the win away.

Candidate fixes, cheapest first, none yet measured:

- **Require the push to be unopposed for a sustained period**, not instantaneously — the estimate
  already knows which of its units are engaged, so it can require the condition to hold across
  several consecutive decisions rather than one.
- **Do not let `killerInstinct` spend money that is within one rung of an investment**, or at
  least cap it at `Money − InvestmentPrice` when the rung is close.
- **Latch it once per push.** Re-firing at 153s with 11 units left is the clearest single defect.

Instrument: `--economy-dump --explain <from>:<to>` runs the shadow and prints the reason behind
every P2 spawn in the window.

### The two brakes, implemented 2026-08-24

Both default off; flag-off guard verified at `33DD370A7EA7E402F7B2DBBEDA76BFC8`.

| setting | value in `EconomyBrakeProfile` | what it does |
|---|---|---|
| `KillerInstinctInvestLockoutSeconds` | **5.0** | suppress killerInstinct while the next rung is within 5s of income |
| `KillerInstinctPushLatch` | **true** | after a push collapses (army more than halves), refuse to fire again until the bot holds an income advantage |

Applied *after* the trigger rather than folded into it, so the raw signal stays readable in
`LastKillerInstinctRaw` and each brake can be attributed separately via `LastKillerLockReason`.
The latch reads `LastIncomeAdvantage` — the previous decision's value — because
`hasIncomeAdvantage` is computed further down the same method; a 0.17s lag is not worth
reordering that block for.

**5 seconds is Marc's call**, and the trace supports it: the 0A7658 stall happened at **2.8
seconds** from the rung, so 5 covers it with margin without blocking a genuine finish from
further out.

**Verified on the actual decision, via the shadow explainer:**

```
without   146.2s  reason=killerInstinct  killRaw=True  lock=-            money=$7362  price=$8080
with      146.2s  reason=attack          killRaw=True  lock=near-invest  money=$7362  price=$8080
```

The brake fires exactly at the moment in question and the bot falls back to the *budgeted*
attack path rather than the bypass.

**A tooling bug this exposed, worth not repeating.** The shadow originally ran *after* the
recorded actions were applied, so at 146.2s it evaluated with $5,296 — the balance already
reduced by the very purchase being explained — which put the bot 11 seconds from the rung and
made the brake look inert. Any shadow that explains a decision must run BEFORE the recorded
action for that tick.

**Measured, n=300 paired against the flagship: 51.7%** (SE 2.9pp — still not separable from 50).
The brake fires in 79/300 games, median 33 decisions, and investments move +0.05. As with every
other change in this series, self-play cannot resolve it; the situation is one a human creates.

### The bait wave — why this is an exploit, not a misfire

Marc, 2026-08-24: *"a really devious and effective tactic that I use is to send a 'bait' wave of
attackers. I don't spend much on this, the entire goal is to get the opponent to spend on
important gadgets or large defending units, at which point I can send my actual attack."*

This reframes `killerInstinct` from a tuning error into an **exploitable trigger**, and the two
are not the same problem:

- A misfire happens at some rate and costs an expected amount.
- A trigger a human can pull happens **whenever they choose**, at the moment of their choosing,
  and the cost is paid at the worst time rather than the average time.

The mechanism is symmetric and the bot is on the losing side of it both ways. Marc baits the bot
into spending its *defensive* capacity; and `EstimateOwnCastleDps` reading an empty field baits
the bot into spending its *economy*. Both work because neither side's model contains the other
side's **uncast potential** — money in hand, gadgets off cooldown, units affordable but not yet
bought.

**Everything the bot reasons about is on the field.** `EstimateOwnCastleDps` counts units in
contact. `ThreatModel` counts units present. `EstimateProjectedThreatDps` counts units touching
the castle. There is no term anywhere for "what could my opponent do right now if they chose to",
even though the state vector deliberately hides enemy money for the RL agent precisely because it
is decisive information.

**The three brakes shipped so far do not address this and were not meant to.** They limit the
*consequences* — do not bypass the budget near a rung, do not re-press a dead push, do not attack
into CC. A bait wave still fires the trigger; it just costs less when it does.

**The direct fix, if it is ever wanted:** give the attack-commitment decision a model of the
defender's uncast capacity — their visible gadget tiers and cooldown states, and a bound on their
money from observed income and observed spending. That is real, observable signal that nothing in
the bot currently uses. It is also a substantially bigger change than anything in this series, so
it waits on whether the brakes move Marc's games.
