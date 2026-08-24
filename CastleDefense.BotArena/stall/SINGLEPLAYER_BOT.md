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

### RESULT: the brakes worked. The bot now loses by seconds, not by rungs.

Five games on the brake build. **The rung-7 stall is gone** — in every one the bot reaches
investment 8 and finishes on $2,500/s, where pre-brake `0A7658` finished on $750/s and rung 7.

| game | Marc | bot money at death | of Armageddon ($121,221) | seconds short | repairs |
|---|---|---|---|---|---|
| 54D732 | White | $117,841 | **97.2%** | **1.4s** | 6 / $2,560 |
| C7F159 | Blue | $119,198 | **98.3%** | **0.8s** | 5 / $764 |
| 2B69F2 | Orange | $107,748 | 88.9% | 5.4s | 5 / $764 |
| 8F1A28 | White | — | **DRAW** — both reached Armageddon | — | 7 / $11,398 |

**It is not refusing the purchase.** In `54D732` the balance crossed $121,221 for *exactly one
tick*, at 297.0s — the final tick of the game. It died in the instant it could first afford the
win. In the draw it crossed at 279.0s and bought Armageddon **0.1 seconds later**.

Repair spend is $764–$2,560 against $11,398 pre-fix. The over-repair is gone and is no longer
where the money goes.

**Caveat: this is five games, all against one player, with three changes stacked.** The direction
is unmistakable — a rung-7 stall to a photo finish is not a subtle effect — but which of the three
brakes is carrying it is not separable from this sample, and self-play could not distinguish any
of them from 50%.

### Three losses, three different causes (2026-08-24)

Marc won each of the three brake-build games by a different route. Dissected via
`--economy-dump --explain`, which names the branch behind every purchase.

**ORANGE `2B69F2` — the drip, and it is not a bug.** No single mistake. Across the whole game
the bot spent **$37,980 on units, 18% of everything it earned**; Marc spent **$5,933, 3%**. Six
times the outlay for the same ladder. The bot actually *led* rungs 2–7 and lost only rung 8 (225s
against 220s). The composition is the tell: T5×221 ($17,901) and T6×29 ($9,802) against Marc's
T4×284 ($5,112) — the bot buys mid-tier pressure continuously, Marc buys the cheapest body that
does a job.

This is the constant-pressure stream working as designed and losing anyway. **It is not obviously
correctable**: pressure is what makes the bot dangerous in most games, and a blanket reduction
would trade away the games it currently wins. The honest framing is that 18% is a *policy*, not a
mistake, and the question is whether it should be state-dependent — cheap when the opponent is
also saving, expensive when they are not.

**WHITE `54D732` — a 32% tax on the rung-7 climb.** Rung 6 at 115.0s, rung 7 at 164.0s: **49
seconds** for a rung that is 32 seconds of pure saving. Marc did the same rung in 34.9s. Over that
window the bot earned $12,372 and spent **$3,991 (32%)** on units:

| tier | bought | spend | share |
|---|---|---|---|
| 7 | **1** | $2,066 | **52%** |
| 4 | 53 | $954 | 24% |
| 1 | 148 | $444 | 11% |
| others | 14 | $527 | 13% |

157 `attack` decisions against 2 `killerInstinct` in that window — this is a *drip*, not a burst,
and **one tier-7 purchase is half of it**. Skipping that single unit alone would have brought the
rung in around 40s. Marc: "the bot plays well enough that it almost makes up the entire deficit" —
the ~10s it lost here is the whole margin.

**BLUE `C7F159` — killerInstinct buying into a saturated field.** The bot took rung 7 at 149.0s
against Marc's 159.7s, **10.7 seconds ahead**. Then at 167.2/167.4/167.5s it bought three tier-7
units in 0.3 seconds for **$6,198** — `killerInstinct`, confirmed by the shadow. It reached rung 8
at 230.0s against Marc's 221.5s: **8.5 seconds behind**. A 19-second swing bought with one burst.

**What makes this one different from the 0A7658 stall:** the bot was **39.6 seconds** from rung 8
($10,289 of $40,000 at $750/s), so the 5-second near-invest lockout correctly did not fire. The
brake is not at fault and widening it would be wrong.

**The actual defect is saturation.** At the moment it bought, the bot already had **135 units on
the field against Marc's 1**. The three extra tier-7s changed nothing: over the following 18
seconds Marc's castle went *up* (21,372 → 31,701 — he repaired through it) while the bot's army
grew to 186 and never broke through. `SpendOnUnits` does cap at `MaxOwnUnitsOnField = 120`, but
gadget-spawned reinforcements bypass the cap, so the real field size runs well past it and the
purchase path never sees saturation.

**Candidate fix, cheapest yet and narrow:** make `killerInstinct` refuse to buy when the field is
already saturated — count ALL own units, not just purchased ones, and require that the last N
purchases actually moved the enemy castle. Buying unit 136 against a castle that is gaining HP is
not closing anything out. This is a different failure from both brakes already shipped and neither
addresses it.

## WHAT MAKES A killerInstinct ACTIVATION VALUABLE (145 activations, 42 games)

`--killer-audit` replays every recorded game against a shadow HeuristicBot **matched to the arm
the game was played on**, groups the decisions where the flag is up into activations, and scores
each on the two things Marc named: castle damage (kill as the best case) and the spend it forced
out of the opponent. Cost is the money the bot spent on units during the activation.

Restricted to the 42 games whose seat-2 bot was a HeuristicBot variant — search-arm games had a
different agent, so the shadow would be fiction there.

**119 activations spent money, $550,294 in total, for 24 kills.** 29% of them did *nothing* —
under 500 HP of damage and under $500 of forced response.

### The three predictors

| split | did nothing | median dmg | kills |
|---|---|---|---|
| **enemy castle under 40% HP** | **7%** | 3,202 | 11 |
| enemy castle 40–69% | 19% | 4,200 | 10 |
| **enemy castle 70–94%** | **37%** | **0** | 3 |
| | | | |
| **after 220s** | **0%** | **11,019** | 8 |
| 160–220s | 20% | 3,037 | 11 |
| **100–160s** | **40%** | **0** | 5 |
| | | | |
| own field under 90 units | 11–18% | 1,594–3,998 | 14 |
| **own field 90+ units** | **41–50%** | **0–997** | 10 |

Read together: **killerInstinct is valuable when the enemy castle is already hurt and the game is
late, and wasted when it fires at a healthy castle early with a saturated field.** The
100–160s window is the worst — 40% do nothing, median damage zero — and that is exactly the
rung 6→7 climb where the economy stall happens.

### The gate: a CONJUNCTION, not either condition alone

| gate | blocks | $ saved | kills lost | blocked did-nothing | kept did-nothing |
|---|---|---|---|---|---|
| own units ≥ 90 alone | 31 | $173,610 | **10** | 45% | 15% |
| castle > 70% alone | 45 | $111,924 | 3 | 38% | 14% |
| castle > 80% alone | 23 | $55,296 | 2 | 39% | 19% |
| **castle > 70% AND own units ≥ 90** | **18** | **$60,544** | **1** | **56%** | **17%** |

**The conjunction is the answer.** It blocks 18 of 119 activations, saves $60,544, costs exactly
**one kill out of 24**, and **56% of what it blocks did literally nothing** against 17% of what it
keeps.

**NEGATIVE RESULT, and it corrects the fix proposed from the Blue game alone: saturation by
itself is a BAD gate.** At every threshold from 60 to 140 units it costs 6–11 of the 24 kills — a
big army that is not getting through *right now* still converts often enough to matter. The Blue
game was one instance and it did not generalise. It is only in combination with a healthy enemy
castle that saturation identifies waste, and the reading is intuitive: *"I have a huge army, it
is not getting through, and the castle is fine."*

### Checked against the known cases

| game | t | cost | own | castle | dmg | gated? |
|---|---|---|---|---|---|---|
| C7F159 | 167.2s | $6,198 | 136 | 92% | 0 | **BLOCKED** — the burst that lost the Blue game |
| 0A7658 | 153.2s | $4,150 | 10 | 88% | 11,960 | allowed — productive, correctly kept |
| 0A7658 | 146.2s | $4,132 | 62 | 77% | 0 | allowed — the near-invest brake already covers this one |
| 54D732 | 127.5s | $2,404 | 36 | 48% | 0 | allowed |

It catches the specific burst that cost the closest game while leaving the productive
high-damage activation alone.

**Caveat on the whole audit.** Activations in won games are followed by damage partly *because*
the bot was winning — some of the split between won and lost games is reverse causation. The
state-conditioned splits above are the defensible part, since they compare activations by the
board at the moment of firing rather than by how the game ended.

### THE REAL CAUSE: the defender repairs out of it, and the bot cannot see that coming

Marc: the White game's losing decision was still allowed by the castle-HP gate. Correct, and
chasing it found the actual mechanism.

**Clarification first: lost games were always in the audit** — 46 of the 145 activations come from
the 16 games the bot lost, plus 7 from the draw. The earlier caveat was that the *won-vs-lost*
comparison is contaminated by reverse causation, so the state-conditioned splits are the ones to
read; every game is in all of those.

**54D732 at 127.5s, traced tick by tick.** Marc's castle: **1,000/2,000 with 27 bot units on the
field and ZERO defenders**. The flag's estimate — kill in 3.8 seconds — was *correct*. Then:

```
127.7s   repair    955  ->  6,995 / 12,000
128.2s   repair         -> 17,840 / 23,000
```

**He multiplied his castle HP eighteen-fold in 0.9 seconds for $34** — repairs #0 and #1, the
bottom of a ladder that reaches $63,195. The bot then spent $2,404 chasing a castle with 18× the
HP it had committed against.

`killerInstinct` divides by `enemy.CastleHealth`. **That denominator is a number the defender can
change by an order of magnitude for pocket change**, and nothing in the estimate accounts for it.

Measured across all 119 paid activations:

| defender repairs during the window | n | kills | med dmg | did nothing |
|---|---|---|---|---|
| **none** | 54 | **20 (37%)** | **9,410** | **2%** |
| 1 | 38 | 4 (11%) | 0 | 42% |
| 2 | 19 | **0** | 0 | 37% |
| 3+ | 8 | **0** | 0 | 38% |

**Kill rate is 37% when the defender does not repair and 6% when they do**, and they repaired
during **55%** of all activations. A repair inside the window is the single strongest signal of a
wasted attack — and it is *not observable at commit time*, because it is a choice they make after.

**But the proxy is observable.** Castle max HP encodes how many repairs someone has already taken,
so it reads directly as "how much cheap repair is still available to them":

| their castle max HP | repairs taken | n | kills | med dmg | did nothing |
|---|---|---|---|---|---|
| **≤ 12,000** | **0–1** | 31 | 4 | **0** | **55%** |
| 12k–34k | 2–3 | 50 | 8 | 450 | 20% |
| 34k–56k | 4–5 | 33 | 10 | 10,405 | **0%** |
| 56k+ | 6+ | 5 | 2 | 22,540 | **0%** |

Perfectly monotonic. **A defender who has barely repaired is a defender who can undo your push for
$34.** One who has already taken five repairs faces a four-figure price and cannot.

### The gate that catches both games

| gate | blocks | $ saved | kills lost | blocked did-nothing | kept did-nothing |
|---|---|---|---|---|---|
| their max HP ≤ 12,000 | 31 | $69,657 | 4 | 55% | 11% |
| castle > 70% AND own ≥ 90 | 18 | $60,544 | 1 | 56% | 17% |
| **either** | **42** | **$105,157** | **4** | **52%** | **6%** |

The union blocks 42 of 119 activations, saves $105,157, costs 4 of 24 kills, and **drops the
did-nothing rate of what survives from 29% to 6%**.

| game | cost | their max HP | own units | castle | dmg | caught by |
|---|---|---|---|---|---|---|
| 54D732 @127.5s | $2,404 | 2,000 | 36 | 48% | 0 | **cheap-repairs** |
| C7F159 @167.2s | $6,198 | 23,000 | 136 | 92% | 0 | **saturation** |

Two different failure modes, two different terms, both game-losing, both now covered. And the
cheap-repair term is the first concrete instance of the *uncast potential* gap from the bait-wave
note — the defender's unspent capacity, made observable through a number already on the board.

### Pricing the value in dollars — and a correction to how "forced response" was measured

Marc: *"as their castle max HP increases, the value of every damage point we deal increases
exponentially, since the repair price increases exponentially."* That is right, it is the correct
way to put damage on the same scale as money, and it changes the conclusion.

**FIRST, A DEFECT IN THE EARLIER NUMBER.** `response_spend` was
`MoneySpentOnUnits[1]` over the window — **unit purchases only**. It missed repairs and gadgets
entirely, which is indefensible given repairs are the dominant response (55% of activations).
Across all 119 activations it captured **$404,782 of a real $1,114,029** — about a third. Every
"forced response" figure quoted before this section is understated by roughly 3×.

Now measured as total outflow: income earned over the window, less what stayed banked, less any
investment (a choice, not a forced response).

**SECOND, THE DAMAGE PRICE.** From `PreviewRepairStep` the max rises 11,000 per repair and the
castle heals 20 points of the new max, against `ApplyRepairStep`'s exponential price:

| their repairs taken | their max HP | repair price | HP it buys | **$ per HP** |
|---|---|---|---|---|
| 0 | 1,000 | $8 | 11,750 | **0.001** |
| 2 | 23,000 | $66 | 16,150 | 0.004 |
| 4 | 45,000 | $493 | 20,550 | 0.024 |
| 6 | 67,000 | $8,837 | 24,950 | 0.354 |
| 7 | 78,000 | $63,195 | 27,150 | **2.328** |

**A point of damage is worth 3,230× more against a defender who has repaired seven times than
against one who has not repaired at all.**

### The result: killerInstinct is economically negative below 4 defender repairs

Value = damage priced at their cost to undo it, plus their total forced outflow. Cost = what the
bot spent during the activation.

| their repairs at commit | n | bot cost | damage $ | their outflow | **value / cost** |
|---|---|---|---|---|---|
| 0–1 | 31 | $69,657 | $48 | $44,171 | **0.63** |
| 2–3 | 50 | $235,519 | $1,436 | $141,864 | **0.61** |
| 4–5 | 33 | $180,470 | $32,251 | $595,623 | **3.48** |
| 6+ | 5 | $64,648 | $307,888 | $332,371 | **9.90** |

**Below four defender repairs the bot loses money on every activation. Above it, it makes 3.5×
and then 10×.** The median damage value tells the same story: $0, $2, $668, **$61,257**.

Cumulative, firing only when their repair count is at least N:

| N | activations kept | bot cost | value | value/cost | kills kept | $ saved |
|---|---|---|---|---|---|---|
| 0 (today) | 119 | $550,294 | $1,455,652 | 2.65 | 24 | — |
| 2 | 88 | $480,637 | $1,411,433 | 2.94 | 20 | $69,657 |
| **3** | **55** | **$333,546** | **$1,354,773** | **4.06** | **14** | **$216,748** |
| 4 | 38 | $245,118 | $1,268,133 | 5.17 | 12 | $305,176 |
| 5 | 26 | $163,586 | $1,183,427 | 7.23 | 9 | $386,708 |

**This is a better rule than either gate proposed earlier**, and it subsumes the cheap-repairs
one: "their max HP ≤ 12,000" was just repair count ≤ 1 wearing a different hat. It is a single
observable integer, it is monotonic, and it is denominated in money rather than in a
did-nothing rate.

**Where to set it is a judgement, not a calculation.** N=3 keeps 93% of the value for 61% of the
spend but gives up 10 of 24 kills. N=2 is nearly free — 20 kills kept, $69,657 saved. The kills
lost are real games, so the aggressive settings are not obviously right even though the ratio
improves monotonically.

**Caveat.** Value and cost are on the same scale only by assumption. Damage-to-undo is a real
price the defender would pay *if they chose to repair*; forced outflow is money they actually
spent but not all of it was caused by this activation. Both are defensible as directional
measures and neither is exact.
