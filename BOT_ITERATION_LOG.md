# BOT_ITERATION_LOG.md

Append-only ledger of `HeuristicBot` iterations. **Read this instead of a previous
session's transcript.** One entry per run, whether it was kept or reverted — a rejected
result is worth as much as an accepted one and costs a full run to rediscover.

Format, kept deliberately short so a session can read the whole file cheaply:

```
## <n>. <FlagName> — KEPT | REVERTED | INCONCLUSIVE
date · mechanism in one line
predicted: <what should move, what must not>
measured:  <the numbers>
verdict:   <why kept or reverted>
```

Acceptance rule: the predicted rows moved in the predicted direction, nothing unpredicted
regressed past noise, and the mechanism was stated **before** the run. A win with no stated
mechanism is rejected. See `BOT_BACKLOG.md` for why.

---

## 1. ChargeAwareFallback — KEPT

2026-09-01 · The pick pipeline in `SpendOnUnits` filters on price only and makes ONE spawn
attempt with no fallback, so once the chosen unit's 5 charges are drained the bot re-picks
the same uncharged id and buys nothing, silently, for most of the game.

**predicted:** units/s *must* rise from ~1.14 toward 3+; idle money *must* fall; earned
invests *must NOT* move; win rate up on tier-spam rungs.

**measured** — `ladder 400 --both`, seeds 12345 and 777, variant vs the reference on
identical specs (4 arms = 2 seeds x 2 modes, 5,600 games per contender per arm):

| arm | units/s (mirror) | idle $ (mirror) | earned inv (mirror) | h2h vs reference | overall |
|---|---|---|---|---|---|
| nostart s12345 | 1.01 → **2.47** | 9,580 → 7,753 | 6.80 → 6.80 | 45.9 → **48.5** | 89.2 → 89.5 |
| headstart s12345 | 1.16 → **2.87** | 9,625 → 8,948 | 4.97 → 5.00 | 50.6 → **54.1** | 89.3 → 90.0 |
| nostart s777 | 1.02 → **2.51** | 11,618 → 8,655 | 6.91 → 6.92 | 46.9 → **49.7** | 89.1 → 89.6 |
| headstart s777 | 1.19 → **3.01** | 10,879 → 9,318 | 4.82 → 4.87 | 47.9 → **55.2** | 89.1 → 90.6 |

Idle money fell on *every* rung, hardest on the weak ones (DoNothing 2,570 → 659;
HumanClone 3,620 → 1,702). End-of-game castle HP on the mirror rose +3.2 to +7.4 points.

**verdict:** KEPT. Both "must move" predictions confirmed at 2.4-2.5x, and the load-bearing
negative prediction held tightly — earned invests moved by at most 0.05 in any arm, so this
is not the "spend more, invest less" trade that sank four earlier `SpendOnUnits`
experiments. Head-to-head positive in all four arms across two seeds and two modes.
`--unit-charge-check` passes and `bot-checksum --games 24` still returns
`47EC146D660B0D721B4DC224D8ACB7F9`, so the default bot is byte-identical and the change is
entirely behind the flag.

**one prediction FAILED, recorded rather than buried:** win rate against Tier4Spam did not
improve — 81.8 → 80.4, 87.0 → 85.9, 80.0 → 79.7, 87.1 → 87.4, i.e. flat-to-slightly-down in
3 of 4 arms. Tier4Spam is the rung where the bot already ends games at ~14% castle HP, so it
is losing on a different axis and more cheap bodies do not address it. Do not claim this
change helps against tier-4 pressure.

**cost:** throughput 695k → 490k ticks/s (~30-38% across arms). Mostly *not* the roster
scan — average game length and units on the field both rise, which is the intended effect.
Still worth watching: `HeuristicBot` is `RolloutSearchBot`'s rollout policy for both sides,
so a slower prior directly costs search depth. Re-measure `search-test` before ever
promoting this to the default.

## 2. BlockSingleChipper (v1) — REVERTED

2026-09-01 · Marc's finding: a lone enemy on the castle is refused by all four defence
paths, so it chips permanently for free. v1 bought one cheap body per enemy swing whenever
an unblocked chipper was on our wall, priced at up to 2 seconds of income per body.

**predicted:** castle HP *must* rise; money spent on units rises only slightly; earned
invests *must NOT* fall; effect concentrated in long games and low-tier spam.

**measured** — `ladder 400 --both`, seeds 12345 and 777, stacked on iteration 1 so the
comparison is ChipBlock vs ChargeAware:

| rung | HP% (ChargeAware → ChipBlock) | win rate | earned inv |
|---|---|---|---|
| DoNothing | 83.7 → **99.7** | 100 → 100 | 4.29 → 3.96 |
| Tier1Spam | 47.1 → **84.4** | 100 → 100 | 5.38 → 5.39 |
| Investor | 57.7 → **83.9** | 98.6 → 95.4 | 5.40 → 5.29 |
| BalancedHuman | 56.3 → **87.5** | 100 → 99.8 | 5.56 → 5.59 |
| HumanClone | 52.8 → **71.5** | 98.8 → **88.4** | 5.78 → 5.57 |
| **Tier4Spam** | 14.3 → 10.9 | **80.4 → 27.8** | **4.72 → 2.28** |
| HeuristicBot | 37.6 → 40.3 | 48.5 → 45.6 | 6.80 → 6.64 |
| OVERALL | | **89.5 → 79.6** | |

**verdict:** REVERTED. The blocking mechanism works exactly as designed — castle HP rose on
every single rung, by up to +37 points. But the load-bearing negative prediction failed
outright: earned invests against Tier4Spam more than halved.

**diagnosis, and it is a specific arithmetic error, not bad luck.** The rate limit is the
survival law — one body per enemy *swing* — and swing rate is what the roster clamps
hardest. `GameDataManager` recomputes AttackSpeed and clamps it to [0.2, 5.0]; **tier-8
units clamp at the BOTTOM (0.20/s) and tier-4 units clamp at the TOP (5.0/s)**. So the law
demands ~0.2 bodies/sec against a tier 8 and up to **5 bodies/sec against tier 4** — a 25x
difference. The stall findings' famous "$2/sec holds anything" figure is specifically
*against a lone tier 8*. v1 gated the **price of each body** (2 seconds of income) but never
the **rate of spending**, so against tier-4 pressure it bought ~5 bodies/sec on a $2/sec
income and drained the wallet continuously — recreating the permanent-reactive-spend
pathology `SpendOnUnits`' history documents four separate times.

**the instrument is partly blind here, and that matters for reading this result.** Every
rung where chip blocking helped (DoNothing, Tier1Spam, BalancedHuman) was already pinned at
a 100% ceiling, so the ladder can show this change's *cost* but not its *benefit*. The
benefit is not bleeding castle HP to an unanswered chipper over a long game, which is what
Marc and `RolloutSearchBot` both exploit and what no ladder rung does. Weigh HumanClone
accordingly — it is the only non-HeuristicBot-derived rung, and it fell 98.8 → 88.4, which
is real evidence against v1 rather than an artefact of the ceiling.

**next:** v2 caps chip spending as a fraction of income rather than capping the price of
each body, so the survival law sets the rate only up to what the economy can actually fund.

## 3. BlockSingleChipper v2 (flow-capped) — REVERTED, but see the Chipper finding below

2026-09-01 · v2 adds the spend-rate cap v1 lacked: a banked dollar allowance capped at a
fraction of income, the shape `ReactiveFlowCap` and the attack allowance already use.

**measured** — `ladder 400 --both`, seed 12345, vs the iteration-1 baseline:

| arm | OVERALL | Tier4Spam | HumanClone | mirror | invests (mirror) |
|---|---|---|---|---|---|
| ChargeAware (baseline) | 89.5 | 80.4 | 98.8 | 48.5 | 6.80 |
| v1 (uncapped) | 79.6 | 27.8 | 88.4 | 45.6 | 6.64 |
| v2 @ 0.25 income | 85.4 | 64.1 | 90.1 | 46.9 | 6.72 |
| v2 @ 0.10 income | 87.0 | 71.9 | 92.7 | 47.7 | 6.81 |

**verdict:** REVERTED. The cap works exactly as intended — the damage shrinks monotonically
as the fraction falls — but it never reaches zero cost, and **the response is monotone all
the way down, which means the ladder's optimum for this mechanism is spending nothing.**

## 4. Chipper ladder rung — INSTRUMENT FIX, KEPT

The three arms above all regressed, and the reason was not that the mechanism failed. Every
rung it helps (DoNothing, Tier1Spam, BalancedHuman) was already pinned at a 100% ceiling, so
the ladder could see the change's cost and was structurally blind to its benefit. **A
benchmark that can only observe one side of a trade will reject every version of it forever
and look rigorous doing so.**

So `ChipperBaseline` was added: invests normally, keeps **exactly one** cheap unit alive on
the enemy castle, never more. It plays the line Marc reported and `RolloutSearchBot` found.

**measured** — `ladder 200 --nostart`, seed 12345:

| arm | Chipper win rate | **castle HP vs Chipper** |
|---|---|---|
| reference | 99.0% | **52.7%** |
| ChargeAware | 99.0% | **52.7%** |
| ChipBlockV2 @0.25 | 99.5% | **95.9%** |
| ChipBlockV2 @0.10 | 99.3% | **90.6%** |

**The mechanism is confirmed and it is large: +43 points of end-of-game castle HP.** It does
exactly what it was written to do.

**And it still does not move win rate — 99.0 → 99.5.** That is the finding worth keeping:
*the chip exploit does not beat HeuristicBot, so no bot-vs-bot ladder will ever value fixing
it.* It matters against an opponent who converts a 47-point HP deficit into a win, which is
Marc and which is SearchBot (it chips **and** presses). Pricing this change is therefore not
something the loop can do — it needs Marc's own games.

**recommendation, explicitly deferred to Marc:** leave `BlockSingleChipper` off by default,
and try `ChipBlockV2Tight` (0.10 of income) for ten games. The ladder cost of that arm is
−2.5 OVERALL and −0.04 earned invests; the benefit is the hole closing. Only he can say
whether that trade is worth it, and the numbers above are what it costs.

## 5. BuyAutoSpawner (v1) — REVERTED

2026-09-01 · Buys auto-spawner levels (capped at 5, $860 cumulative) out of money the
investment claim has already declined.

**predicted:** units on field up without unit *purchases* rising; `MoneySpentOnUnits` must
NOT move; invests may fall slightly — *if they fall a lot, the cap is too high*.

**measured** (seed 12345): mirror rung **48.5 → 36.5**, OVERALL 89.5 → 87.1, earned invests
on the mirror **6.80 → 5.99**, units/sec 2.47 → **1.76** (down), idle money 7,753 → 4,204.
Headstart agrees: mirror 54.1 → 43.4, invests 5.00 → 4.37.

**verdict:** REVERTED, and the "cap is too high" branch of my own prediction fired. Invests
fell 0.81 on the mirror, which is enormous on a ladder where the whole spread between the
reference and a good variant is ~0.05. units/sec falling is the tell: money went into the
machine instead of into units, so this **displaced** spending rather than adding free bodies.

## 6. CheapGadgetUpgrades — REVERTED (hard)

2026-09-01 · Replaces `GadgetUpgradeSpam`'s income test with an absolute-cost one, on the
grounds that an upgrade is a finite purchase of `ceil((UpgradeCost − xp)/100)` casts.

**measured** (seed 12345): mirror **48.5 → 18.5**, castle HP 37.6 → **14.4**, OVERALL
89.5 → 81.6, HumanClone 98.8 → 82.9, invests 6.80 → 5.62. Headstart: mirror 54.1 → 29.9.

**verdict:** REVERTED. The worst arm of the night by a wide margin. The reasoning about XP
being finite is still correct; the gate built on it spends far too much, far too early. Any
retry needs a total-dollar budget for the whole upgrade path, not a per-cast affordability
test — and it should be measured with `--defense wall` pinned, since a gadget is 1 of 4
draws and this change is otherwise diluted into ~25% of games.

---

## THE PATTERN ACROSS ITERATIONS 1-6 — read this before proposing another change

One change was kept and four were rejected, and **the four failures share a single
mechanism**: every one of them opened a NEW SPENDING CHANNEL, and every one lost earned
investments.

| change | new spend? | Δ invests (mirror) | verdict |
|---|---|---|---|
| ChargeAwareFallback | no — re-allocates *which* unit | **0.00** | KEPT |
| BlockSingleChipper v1 | yes | −0.16 (−2.44 vs Tier4Spam) | reverted |
| BlockSingleChipper v2 | yes, capped | −0.08 | reverted |
| BuyAutoSpawner | yes | **−0.81** | reverted |
| CheapGadgetUpgrades | yes | **−1.18** | reverted |

This bot's binding constraint is the investment race, and every cap in `HeuristicBot.cs`
(`AttackSpendFraction`, `InvestPaceTargetSeconds`, `ReactiveFlowCap`, `AttackGateMinInvestment`,
`reactiveSpendBudget`) was tuned assuming no other channel exists. A new channel bypasses all
of them at once. It is the same finding the file's own history already records three times
under "TESTED AND REJECTED", arrived at independently from four fresh directions.

**Practical rule for the next hypothesis: prefer changes that re-allocate spend the bot is
already making over changes that add a new reason to spend.** If a change must add a channel,
it has to take its money from an existing budget rather than from savings — e.g. fund the
auto-spawner from `_attackSpendAllowance` (it produces units, so it should compete with unit
buying) rather than from money investing declined.

## 7. AutoSpawnFromAttackBudget — INCONCLUSIVE (principle confirmed, change inert)

2026-09-01 · The rule from iterations 1-6 applied: same feature as iteration 5, funded from
the attack allowance (which buys units) instead of from savings (which buys rungs).

**measured** — `ladder 400 --both`, seeds 12345 and 777, vs the iteration-1 baseline:

| arm | mirror win rate | mirror earned invests |
|---|---|---|
| nostart s12345 | 48.5 → 48.6 | 6.80 → 6.81 |
| headstart s12345 | 54.1 → **55.4** | 5.00 → 5.02 |
| nostart s777 | 49.7 → **51.0** | 6.92 → 6.93 |
| headstart s777 | 55.2 → 55.4 | 4.87 → 4.87 |

**verdict:** INCONCLUSIVE — kept in the tree, NOT added to `Accepted`.

**the principle held, and this is a clean controlled test of it.** Identical feature, only
the funding source changed: from savings it cost **0.81** earned investments on the mirror
rung; from the attack allowance it costs **0.01**. That is as direct a confirmation of
"re-allocate, don't add" as this harness can produce.

**but the change is close to inert.** Tier4Spam and Chipper come back *byte-identical* to the
baseline in all four arms, which means it never fired at all in those matchups. It only fires
in the long mirror games, where it is +0.1 / +1.3 / +1.3 / +0.2 — real in two arms, nothing in
the other two.

**why, and this is the useful part.** The attack allowance only accrues inside
`SpendOnUnits(preferDefense: false)`, which `Decide()` gates behind
`InvestmentCount >= AttackGateMinInvestment (6) && (Income >= 50 || hasIncomeAdvantage)`. So
the budget this version spends from **does not exist until investment 6**, by which point the
cheap early rungs the auto-spawner is actually good at are irrelevant. The funding rule that
makes the change safe is the same rule that makes it too late to matter.

**consequence for the backlog:** there may be no good home for the auto-spawner inside the
current economy at all. It wants early money, and early money is exactly what this bot is
correctly unwilling to spend. Any third attempt needs a reason why the early rungs beat an
investment rung on their own terms — not a better place to take the money from.

## 8. ChargeAwareEverywhere — INCONCLUSIVE (correct, but the paths barely execute)

2026-09-01 · The charge test applied to the three spawn paths iteration 1 deliberately left
out: the wiper in `Decide()`, `FindWiper`, and `DefensiveResponse`'s blocking body.

**measured** — `ladder 400 --both`, seeds 12345 and 777, vs the iteration-1 baseline.
Mirror rung: 48.5 → 49.2, 54.1 → 54.1, 49.7 → 49.2, 55.2 → 55.6. OVERALL within 0.1
everywhere. Earned invests identical to two decimals in all four arms.

**verdict:** INCONCLUSIVE — flag kept in the tree, NOT added to `Accepted`, because nothing
predicted actually moved.

**why nothing moved, which is the useful part.** `bot-checksum --games 24` reports
`WIPE n=0` in every game: **the wiper path essentially never fires** in the measured
configuration. And `DefensiveResponse` only runs under `DefenceOnly`, which the ladder never
exercises — it is the profile `GameHostingService` uses for `defwatch` spectator games. So
this change is correct and it fixes a real silent failure, but it fixes it in code the
benchmark barely executes.

**keep it anyway, as correctness insurance rather than strength.** The `DefensiveResponse`
case in particular is the worst instance of the bug — `_blockCredit` banks up to
`MaxBlockCredit` on refused spawns, so the bot believes it is blocking at the survival law's
rate while delivering one unit id's regen — and it will start mattering the moment anything
raises the wiper's firing rate or `DefenceOnly` returns to the shipped path.

**a placement detail worth not re-deriving:** the charge test is deliberately absent from
`MeasureWipeReach` and sits behind `!ignoreMoney` inside `FindWiper`. Both of those callers
are the "what would the best wiper have been at any price" DIAGNOSTIC, and filtering them
would make the alternative-comparison report a wiper the bot never actually rejected. The
first draft of this change patched `MeasureWipeReach` by mistake; the compiler caught it only
because `settings` was out of scope there.
