# Defence-only HeuristicBot: what was measured

Everything here is `bot-checksum --games 160 --p1-defence-only` — the defence-only bot in seat 1
against the shipped attacking bot in seat 2, random teams and loadouts, **the same 160 seeds in
every arm** so results can be compared paired. SE on a single arm is ~3.9pp, which is why the
paired sign test is quoted rather than the rate difference.

`HeuristicBotSettings.DefenceOnly` defaults to **false** and the shipped bot is untouched
throughout: the flag-off fingerprint has stayed `643A6CA19C1851CF04A2A0C9F873195C` across every
change below.

## The progression

| build | wins | defence spend | invests |
|---|---|---|---|
| settle gate (wait for the force to stop growing) | 30.0% | $2,305 | — |
| ceiling-aware option pricing | 36.9% | $11,033 | — |
| priced repair (refuse repairs that cost more than their seconds) | 39.4% | $22,317 | 7.08 |
| + 8s wiper cooldown | 42.5% | $14,804 | 7.09 |
| + value-gated wipe | 46.2% | $16,021 | 7.20 |
| + value-SELECTED wiper, 1s cooldown | **53.1%** | $9,642 | 7.35 |

Paired against the settle gate: **+37 games, 38 gained against 1 lost, p < 0.0001**. The individual steps are mostly not
significant on their own — only the cumulative move is.

## What each change was, and what it actually fixed

**Ceiling-aware pricing.** One purchase per decision at `DecisionIntervalTicks` is a hard six
units/sec. Pricing the block option at the rate the survival law *asks for* rather than the rate
the bot can *deliver* made blocking look like it solved problems it cannot touch — against a
45-unit wave the law wanted 100–280 bodies/sec. Valuing it honestly is what lets the other
options win when they deserve to. **+7pp, p = 0.035 paired.**

**Priced repair.** The repair branch fired whenever time-to-death dropped under 5.5s and money
covered it, then returned — so it took six repairs in seven seconds for $11,398, the last at
$8,837 for 0.89 seconds of life, while the wipe evaluation never ran. It now refuses repairs
whose seconds are worth less than their price. Repair spend on the traced mirror fell to $2,560.

Two things measured here that are worth not re-deriving: **moving repair out of its first-claim
slot cost 11 points** (35.0% → 23.8%) — the ordering comment in the file is load-bearing — and
the price check must read the *real* incoming DPS, not `EstimateProjectedThreatDps`, which counts
only units already in contact and reads far too low during a collapse.

**Value-gated wipe.** `netWipe` now uses the value one swing can REACH rather than everything
inside `WaveWipeRadius`, and caps the time a wipe buys at `reachValue / enemyCommitmentRate` —
the time the opponent needs to rebuild what was destroyed — instead of crediting it with clearing
the threat for the whole 41-second window. That old term swamped every other and approved
anything.

**Value-SELECTED wiper.** `FindWiper` required `d.Damage >= toughest` — the unit had to
one-shot the toughest thing in the band. Against forty tier-4s plus one tier-7 that forced a
$2,066 purchase to clear mostly $18 chaff, when an $81 tier-5 one-shots all forty and leaves the
tier-7 standing. It now picks the unit maximising **value killed minus price**:

| | kills | price | net per wipe | ratio |
|---|---|---|---|---|
| before | $715 | $1,528 | −$813 | 0.47× |
| after | $474 | $224 | **+$249** | **2.11×** |

**DO NOT QUOTE THOSE PER-WIPE FIGURES AS A TRADE RATIO.** They price what ONE swing kills at the
moment of purchase, which is unreliable in both directions: it misses everything a wiper kills
over the rest of its life, and at a short cooldown consecutive purchases price the same pile
before any of them lands. They remain valid as a BEFORE/AFTER on the selection rule, since both
sides are measured the same way — they are not valid as economics.

**`MoneySpentOnUnits` IS NOT THE ENEMY'S ARMY VALUE, and reaching for it as one produced a wrong
proof.** It deliberately excludes `ignoreCost` spawns (`GameEngine.cs:403`), so every unit the
`reinforcements` gadget spawns is free to it — about **20% of the opponent's army** in the random
loadout population, and far more in a pinned reinforcements mirror. The first version of this
entry argued the tally double-counts because it claimed 1.28× of what the opponent "ever bought";
that comparison was invalid, and the conclusion happened to be right for a different reason.

The instrument that settles it counts each distinct enemy unit once and asks whether it is still
alive at the end — it cannot inflate, and it captures free units:

| cooldown | their army | they paid | free | we destroyed | our spend | real ratio | the bot's claim | wins |
|---|---|---|---|---|---|---|---|---|
| 0.0s | $42,464 | $34,645 | 18% | $30,852 | $21,453 | 1.44× | $44,246 = **1.43× of everything that died** | 42.5% |
| **1.0s** | $42,823 | $34,300 | 20% | $29,479 | $7,925 | **3.72×** | $13,384 = 0.45× | **53.8%** |
| 8.0s | $34,888 | $27,330 | 22% | $23,354 | $2,604 | **8.97×** | $4,673 = 0.20× | 47.5% |

The double-count is real and now properly evidenced: at `cd 0.0s` the tally claims **more than
everything that died from all causes combined**. At the useful cooldowns it under-counts instead.

Two results worth keeping:

- **The defence trades extremely well** — it destroys about two-thirds of the enemy army for a
  fraction of what the army cost. Note ~20% of that army was free to them, so this measures
  combat effectiveness, not economic damage.
- **The better trade ratio loses more games.** 8.97× loses to 3.72×. Spending more to kill more
  wins at worse efficiency, so efficiency is not the objective and optimising it directly would
  have taken the bot the wrong way.

The counterfactual check agrees in 99% of wipes, and defence spend fell 82% ($16,021 → $2,838 at
the old cooldown). **On its own the selection change was win-rate neutral** (−4 paired,
p = 0.62) — the gain came from what it unlocked.

**The cooldown optimum MOVED once wipes became profitable**, which invalidated the earlier sweep.
Shorter was catastrophic when each wipe lost $813; it is correct now that each gains $249:

| cooldown | 0.0s | 0.5s | **1.0s** | 2.0s | 4.0s | 8.0s |
|---|---|---|---|---|---|---|
| wins | 40.0% | 41.9% | **53.1%** | 46.9% | 46.2% | 43.8% |
| spend | $26,139 | $13,372 | $9,642 | $5,411 | $4,368 | $2,838 |

1.0s is **+15 paired against 8.0s (p = 0.011)** and is now the defence-only default. Note this is
a genuine interaction: neither change is worth much alone, and the pair is worth 9 points.

## Things that were tried and did not work

- **A DPS engage threshold** replacing the settle gate. Swept 0 → 1500 DPS: flat, 27.5–30.0%.
  Even 0 ("engage on everything") measured the same. The threshold is not a lever.
- **Capping $/HP by time.** 15.6%, a disaster. During any serious attack `D × window` exceeds the
  whole castle, so every option's health term clamps to the same value and cancels — the
  comparison collapses to cost alone and "do nothing" at $0 beats blocking exactly when blocking
  is needed. Replaced by valuing seconds gained, which is immune to the clamp.
- **Shortening the wiper cooldown.** Monotonically worse: 4.0s → 39.4%, 1.0s → 36.9%, 0.0s →
  18.8%, with wipers/game going 18 → 132 and investments falling 7.08 → 6.27. The bot was
  over-buying, not starved.

## Open, and known

- **The value gate does NOT replace the cooldown.** Swept with the gate in place, `cd 0.0s` still
  collapses to 21.9% at 124 wipers a game. The gate prices the THREAT and knows nothing about the
  wiper bought two seconds ago that has not landed. Redundant purchases are a self-knowledge
  problem, not a valuation one. Subtracting in-flight wipers from the threat is the real fix.
- **Two survival clocks.** The defence reads `ThreatModel`; repair still reads
  `EstimateProjectedThreatDps`. They share a threshold (`RepairTtdSeconds`) but not a number.
  Unifying them changes the SHIPPED bot's repair timing and needs its own measurement.
- **ARMAGEDDON IS THE NINTH PURCHASE, NOT THE EIGHTH.** `GameEngine.Invest` fires it when
  `InvestmentCount` is *already* >= 8 and deliberately leaves income untouched ("this purchase
  buys the end of the game, not more income", GameEngine.cs:517). So investment 8 is an ordinary
  economy rung — $40,000 for $750/s → $2,500/s — and Armageddon costs **$121,221** on top.
  An earlier version of this file and of `mirror_anatomy.html` said investment 8 *was*
  Armageddon; both are corrected.

- **The bot is NOT losing an economic race — it fields MORE army than the opponent and dies.**
  This retracts the "savings gate for Armageddon" recommendation that replaced the earlier
  "no economic advantage" claim. In the pinned mirror the bot puts **$215,781** of units on the
  field against the opponent's **$172,781**. Fielding 25% more army value and still losing is a
  conversion problem, not a budget one, and no savings rule addresses it.

## REINFORCEMENTS IS THE BIGGEST SINGLE FACTOR MEASURED SO FAR

`reinforcements_3` costs **$1,440** and spawns **5 × tier 7** on a 10s cooldown. At White's
$2,066 per tier 7 that is **$10,330 of army for $1,440 — a 7.2× multiplier**, and no unit
purchase in the game comes close. In the pinned mirror it fires as a metronome: 13 casts, one
every 10s from 163s to the end, and the bot's decision arm during those windows is dominated by
`critical` and `outmatched`.

**The bot does use its defensive gadget** — 28 casts in the mirror, ending on `reinforcements_3`,
identical to the opponent. That worry is unfounded. The problem is what happens next.

Win rate by which defensive gadget each side holds (n=80, random loadouts, defence-only in seat 1):

| our defensive gadget | n | we win | our army fielded | we paid | free |
|---|---|---|---|---|---|
| wall | 20 | **75%** | $8,215 | $8,215 | 0% |
| reinforcements | 20 | 55% | **$66,542** | $6,165 | **91%** |
| speed | 20 | 45% | $9,657 | $9,657 | 0% |
| heal | 20 | 40% | $7,663 | $7,663 | 0% |

| THEIR defensive gadget | n | we win |
|---|---|---|
| speed | 19 | 84% |
| wall | 21 | 76% |
| heal | 19 | 37% |
| **reinforcements** | 21 | **19%** |

Two things, and the second is the actionable one:

- **The opponent holding reinforcements is worth about −60 points to us** (19% against 76–84%).
  It is the largest effect any instrument in this project has measured on this bot.
- **Our own reinforcements gives us 8× the army value and converts it WORSE than wall does.**
  $66,542 fielded at 55% against wall's $8,215 at 75%. We are handed the single strongest
  economic engine in the game and finish behind a gadget that grants no units at all.

Why it is squandered is not yet proven, but two mechanisms are visible in the code:

- **Reinforcement units march.** `ReinforcementsEffect.ExecuteScheduled` calls
  `SpawnUnit(side, id, true)` with no position, so they appear at the caster's castle and walk at
  the enemy like any other unit. A defence-only bot converts its free tier-7s into an attack it
  has otherwise renounced, meeting the enemy mid-map instead of holding the line.
- **`ThreatModel.EstimateRelief` scores reinforcements at ZERO** (the `default:` branch), on the
  reasoning that friendly-side gadgets "show up in the next decision's board anyway". So while
  five free tier-7s are inbound the bot keeps buying chumps at maximum rate, paying for blocking
  it has already bought.

Both are testable and neither has been tested. This is the next thing to work on, ahead of any
further wiper or cooldown tuning.

### WHY 170s: the reinforcements_2 -> _3 upgrade crosses the spawn ceiling

Traced on the pinned mirror, both sides White/nuke/reinforcements. **Both players upgrade to
`reinforcements_3` at exactly 152s** — the same second, because their economies are lock-step —
and the first tier-7 casts land at 163s. The trade flips between 160s and 180s:

| window | we lost | they lost | trade ratio | our castle damage | theirs |
|---|---|---|---|---|---|
| 100–150s (on `_2`) | $2,832 | $4,044 | **1.43×** | 600 | 600 |
| 152–175s (crossing) | $13,062 | $11,686 | 0.89× | 0 | 0 |
| 175–286s (on `_3`) | $192,445 | $144,045 | **0.75×** | **29,990** | 10,500 |

**The mechanism is the payload tier, and the stall harness already measured it.**

| gadget | payload | cost | army bought | chumps/s needed to hold (FINDINGS.md) |
|---|---|---|---|---|
| `reinforcements_2` | 5 × tier 5 | $180 | $405 | **2 /s** |
| `reinforcements_3` | 5 × tier 7 | $1,440 | $10,330 | **30 /s** |

The bot's hard spawn ceiling is **6/s** (`DecisionIntervalTicks = 5`, one purchase per decision).
So the upgrade takes the incoming wave from **3× inside** what a chump line can hold to
**5× beyond** it, in one step, and repeats it every 10s forever. Tier 7 is the single hardest tier
to hold in the whole stall matrix — "fast enough to close and cheap enough to mass" — and
`reinforcements_3` is a tier-7 ×5 delivery mechanism.

Corroborating: our army's mean position collapses from ~650 (mid-map) before the upgrade to
~280 after, against a castle wall at 200. The line is on our own doorstep from 175s on.

**The trades are not actually lopsided per dollar fielded** — we lose 97.1% of what we field and
they lose 93.0%. The absolute gap is mostly that we field $43,000 more army. What differs is
CONVERSION: after 175s their army does 29,990 damage to our castle and ours does 10,500 to theirs.
Both armies annihilate; only one of them is being spent on the objective.

Three consequences for the fix, in priority order:

1. **A chump-only response to `reinforcements_3` cannot work at any budget.** 30/s is not
   reachable. The bot must stop pricing "block" as if rate were the free variable once the
   required rate exceeds the ceiling — it already computes this (`MaxBlockCredit`), but it still
   spends into a losing option rather than switching.
2. **The 10s cooldown is a clock the bot can read.** The wave is perfectly periodic and its
   composition is known from the enemy's visible gadget tier. Nothing in the bot anticipates it.
3. **`ThreatModel.EstimateRelief` returning 0 for our own reinforcements is worse than neutral
   here**: our five tier-7s arrive at the same cadence and are the only thing on our side that
   can actually trade with theirs, yet the spawn logic counts them as nothing and buys chumps
   against a wave they were going to meet anyway.

### CONFIRMED: the bot buys the exact unit its own gadget is handing it free

Own arrivals split by provenance — `b*` = the bot paid, `f*` = a gadget spawned it free.
Pinned White mirror, **160s to 210s**, the window where the trade flips:

| tier | bought | unit price | spend | share of spend | arrived FREE in the same window |
|---|---|---|---|---|---|
| 1 | 163 | $3 | $489 | 2% | 0 |
| 5 | 8 | $81 | $648 | 3% | 0 |
| **7** | **11** | **$2,066** | **$22,726** | **95%** | **25** |

**It paid $22,726 for 11 tier-7 units while 25 identical tier-7 units arrived free from its own
`reinforcements_3`.** Five casts in fifty seconds, five units each — $51,650 of free tier 7 —
and the spawn logic, which scores that gadget at zero relief, bought eleven more.

Over the whole game: **36 tier-7 bought ($74,376) against 65 arriving free**, and tier-7
purchases are **96% of the bot's entire $77,756 unit budget**.

**The "chump-blocking" bot does not chump-block.** It bought 543 tier-1 bodies all game — $1,629,
**2% of its spend**. The blocking law that the whole design was built on governs a fiftieth of
what the bot actually does. Cross-referencing the wipe tally (`n=57 paid=76,130`) against total
unit spend ($77,756) shows **98% of every dollar goes through the wiper arm**, not the block arm.

So the previous entry's framing — "a chump-only response cannot work at any budget" — is true but
beside the point: the bot was never running a chump defence to begin with. The real defect is that
`FindWiper` maximises `value killed − price` against the CURRENT board and has no idea that five
free tier-7s are landing every ten seconds. It buys the reinforcement wave it is already getting.

Two fixes fall straight out, and both are cheap:

- **Subtract in-flight and imminent friendly arrivals from the threat before pricing anything.**
  This is the same "self-knowledge" gap already logged for redundant wipers under *Open, and
  known* — the bot cannot see what it has already bought. Reinforcements makes it far more
  expensive, because the arrivals are free, periodic and predictable from the enemy's visible
  gadget tier.
- **Price the wiper against the free alternative.** A $2,066 tier 7 bought 4 seconds before an
  identical free one lands is a pure waste of $2,066; the option comparison never considers
  waiting.

### WHAT MARC DOES INSTEAD (game 9A9A41, same bot, same pinned mirror)

Recorded 2026-08-22 against `new HeuristicBot(2)` — the same opponent every number above is
measured against — and rebuilt with `--economy-dump`. The rebuild reproduces the recorded row
exactly (income $2,500/$750, money $1,938/$24,526, HP 19939/45000 and 0, 8484 ticks).

**He buys ZERO units for the first 152 seconds and lets his castle fall to 999 HP.**

| rung | MARC | his bot | defence-only bot | its bot |
|---|---|---|---|---|
| 6 | 121s | **115s** | 115s | 115s |
| 7 | **153s** | 221s | 147s | 151s |
| 8 | **226s** | never | never | 227s |

He is *behind* for six rungs — slower than his opponent and slower than our bot — then takes rung
7 sixty-eight seconds ahead and rung 8 alone. Our bot's mirror is the exact inverse: it wins rung
7 and loses rung 8.

**Total unit spend: Marc $24,569, our bot $85,229.** He is not spending less in total so much as
spending it LATER — nothing before 152s, everything after, once the income exists to pay for it.

Two consequences that outrank everything else currently open:

- **The defence-only premise is vindicated and the bot is failing at the part it was built for.**
  Defend cheaply, out-invest, win late is exactly what Marc does. Our bot spends $85,229 defending
  and finishes a rung behind.
- **Marc treats castle health as a RESOURCE TO SPEND; the bot treats it as something to protect.**
  He sits at 999 HP holding money he could have spent on defence and does not spend it. The whole
  option comparison in `DefensiveResponse` prices every purchase against *dying*, so as survival
  falls it spends harder — the opposite response. That is the shape of the defect, and it is not
  reachable by tuning the wiper.

**Do not generalise the earlier 34BA36 finding.** That game (vs the SEARCH bot) had Marc winning
on 2 unit buys and $356, and `human_economy.html` led with it. Against HeuristicBot he buys 424
units for $24,569. How much he buys is a property of the opponent; the timing shape is what
transfers.

### The field-coverage rule: implemented, works, and does NOT replace the cooldown

`FindWiper` now prices a wipe on the **margin** — what a purchase adds over the units already
deployed — rather than on the whole pile as if the field were empty. Marginal rather than a
binary "is there a sufficient wiper out there" veto, because it is the same rule with no
threshold to tune: when the field already covers the pile the marginal value falls to zero and
`netWipe` goes negative on its own.

**The coverage test must score an existing unit exactly as the purchase test scores a candidate.**
The first version scored an existing unit only where it currently stands, while candidates are
priced as if already at the front of the pile. Since our reinforcements spawn at our own castle
and have to walk, that version credited them with nothing and changed almost nothing (50.0% vs
53.1%, wipes 38.7, spend unmoved). Fixed by crediting a live friendly unit with what it kills
**when it arrives**, from the same front, with its own reach — and skipping any unit that has
already passed the pile.

It fires hard. In the pinned mirror after 160s, **`already-covered` is the largest single veto
reason at 43.8%** of decisions.

Paired A/B on identical 160 seeds (`--no-coverage` is the off arm):

| cooldown | wins off → on | paired | unit spend | wipes/game |
|---|---|---|---|---|
| 1.0s | 53.1% → 51.9% | 10 gained / 12 lost, **p = 0.83** | $9,642 → **$6,576 (−32%)** | 42.4 → 32.1 (−24%) |
| 0.0s | 40.0% → **45.6%** | 16 gained / 7 lost, p = 0.093 | $26,139 → **$12,893 (−51%)** | 147.2 → 80.5 (−45%) |

**Keep it: the same win rate for a third less money, and it is the correct model.** But be clear
about what it is not — at the operating cooldown it is **win-rate neutral**, not an improvement.

**MARC'S PREDICTION THAT THIS WOULD REMOVE THE NEED FOR THE WIPER COOLDOWN DOES NOT HOLD.**
Measured after the fix: 8.0s → 41.9%, 4.0s → 45.0%, **1.0s → 51.9%**, 0.0s → 45.6%. The optimum
is still an interior 1.0s and removing the cooldown still costs 6 points. The coverage rule
halves the redundant buying at cd 0 without closing the gap, so whatever the cooldown is doing is
**not** "stop buying what you already have on the field". That is now an open question rather
than an assumption.

One correction to the previous entry's framing. "The bot buys the exact unit its own gadget hands
it free" is true as accounting but misleading tactically: in the mirror those free tier-7s
**march out and die attacking**, so at the moment the defence needs them they are past the pile
and genuinely unavailable. The coverage rule counts them only while they can still fight the
incoming wave, which is correct, and is why the mirror barely improves (286s → 302s, still a
loss) while the random-loadout population improves a lot.

## Reproducing

```
CastleDefense.BotArena.exe bot-checksum --games 160 --p1-defence-only
CastleDefense.BotArena.exe bot-checksum --games 160 --p1-defence-only --wiper-cd 8.0
CastleDefense.BotArena.exe bot-checksum --games 24                      # flag-off guard
CastleDefense.BotArena.exe bot-checksum --games 1 --p1-defence-only \
    --loadout White,nuke,reinforcements --dump mirror_dump_new.csv
```
