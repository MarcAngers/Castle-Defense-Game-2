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

---

## PROMOTION — 2026-09-02

`ChargeAwareFallback` promoted to the default on Marc's instruction. `Accepted` is now a
bare `new HeuristicBotSettings()`, and `PreChargeAware` reproduces the pre-promotion bot.

**`bot-checksum --games 24`: `47EC146D660B0D721B4DC224D8ACB7F9` -> `C9C7E5C0342D07AA43D65113768E5B9A`.**

Everything measured in iterations 1-8 above was measured against the OLD default, which is
now `PreChargeAware`. The A/B commands in those entries still work, but the baseline arm has
to be `--variant PreChargeAware` from here on, not the bare reference.

Carried forward as the top open item: the search-test gap. `RolloutSearchBot` runs
`HeuristicBot` as its prior and its rollout policy for both sides, and this change costs ~35%
throughput, but `search-test` accepts no settings profile so the effect is unmeasured.

## 9. CommitToRung — KEPT, and PROMOTED TO DEFAULT

2026-09-02 · `DeferForInvestment` was bounded to `InvestmentCount < 3`, so from the fourth
rung onward the gadget layer had no awareness of the rung it was saving for and fired purely
on cooldown. Now it also holds a non-urgent cast once money is past `RungCommitFraction`
(0.6) of the next rung.

**measured** — `ladder 400 --both`, seeds 12345 and 777, head-to-head vs the reference:

| arm | h2h | earned invests | castle HP% |
|---|---|---|---|
| nostart s12345 | 44.9 → **49.9** | 6.84 → 6.99 | 34.8 → 39.0 |
| headstart s12345 | 49.5 → **54.6** | 5.02 → 5.12 | 38.5 → 42.4 |
| nostart s777 | 46.3 → **51.9** | 6.96 → 7.09 | 37.9 → 42.1 |
| headstart s777 | 47.4 → **52.5** | 4.88 → 4.99 | 36.3 → 39.7 |

**verdict:** KEPT. +5.0 to +5.6 in every arm, and **earned investments went UP** by 0.11-0.15
— which is the mechanism working rather than a trade, since committing to a rung is what buys
the rung. Promoted to the default on Marc's standing instruction. `bot-checksum --games 24`:
`C9C7E5C0…` → **`26944708B774C609CA6D3E5ECA43815C`**. `PreRungCommit` reproduces the old bot.

## 10. ArmageddonCommit — NOT KEPT

Zeroes the attack budget once `InvestmentCount >= ArmageddonInvestmentCount`, on the grounds
that units bought then are bought instead of winning.

**measured:** h2h 44.9 → 44.5, 49.5 → 47.5, 46.3 → 45.9, 47.4 → 45.8 — consistently
*negative* in all four arms. Earned invests **identical to two decimals** in every arm, while
units/sec fell ~11%. So it fires, cuts production, and converts none of it into rungs.

**verdict:** not kept, and the reason is a design flaw rather than a tuning miss. It zeroes
the attack budget **unconditionally**, including when ARMAGEDDON is unreachable — thirty
seconds left, or the opponent about to break through. Saving $121,221 you will never spend is
strictly worse than buying units with it. Deciding whether the rung is reachable *in time and
ahead of the opponent* is exactly what the economy tracker is for, so this is premature
without it. Retry once the tracker is wired in, gated on the race actually being winnable.

Note also that the ladder under-samples this: earned invests average ~6.9, so games rarely sit
at count 8. Marc's recorded games do. Treat the ladder verdict as weak evidence either way.

## 11. OpponentEconomy + `--economy-tracker-check` — BUILT AND VALIDATED

Marc's design, implemented: simulate the opponent's balance from the known opening position,
accrue income on the engine's schedule, subtract observed spending, credit gadget income, and
assume they invest the moment they can afford to. Reads nothing hidden — no `enemy.Money`,
`enemy.Income`, `InvestmentCount` or `InvestmentPrice`.

**Two corrections made during implementation, both worth keeping:**

1. **Cash income was missing from the spec.** cash_3 fires EIGHT payouts of BaseValue for one
   cost — $12,000 for $7,800 — and cash is White's signature, i.e. the strongest measured
   loadout plays it. Tracking spend without it under-credits a cash opponent by five figures.
2. **The first draft reimplemented the economy curve and had the repair price off by one rung
   within ten minutes.** It now walks a real `PlayerState` via `ApplyInvestmentStep` /
   `ApplyRepairStep` / `ApplyAutoSpawnStep`, which is the single-source-of-truth rule this
   repo already records the time-machine constructor breaking.

**measured** — `--economy-tracker-check --games 40`, income-exact per sample:

| opponent | income exact | investment count under | worst money error |
|---|---|---|---|
| Random | 100.0% | 0.0% | −2 |
| HeuristicBot | **95.6%** | 3.1% | **−121,223** |
| PureInvestor | **95.1%** | 4.9% | 7,575 |
| Tier1Spam | 69.6% | 0.0% | 7,175 |
| DoNothing | 0.0% | 0.0% (100% over) | 7,544 |
| Tier5Spam | 0.0% | 0.0% (100% over) | 39,804 |

**It is accurate where it has to be** — 95%+ against both opponents that actually play an
economy, which are the ones the race is against.

**It over-credits HOARDERS** — an opponent that could invest and chooses not to (DoNothing,
Tier5Spam) is credited with rungs it never bought, and that compounds, because the phantom
income accrues faster and buys more phantom rungs. This is assume-ASAP behaving as specified.
The failure is in the benign direction for the race decision (believe you are further behind,
save harder), which is the trade Marc accepted explicitly.

**THE ONE ERROR TO BE CAREFUL OF.** Worst money error against HeuristicBot is **−121,223**,
i.e. exactly the ARMAGEDDON price. The tracker credits ARMAGEDDON the moment the opponent
*could* afford it, so it can believe they have spent $121,221 and are broke when they are
sitting on it — at precisely the moment the race is decided. **`ArmageddonAssumed` must be
read as "they could have", never as "they did."** Anything that presses because the opponent
looks broke has to use a different signal.

**An assertion I loosened, stated so it can be challenged.** The first version failed on any
negative income bias. That was wrong: the top rungs are 3x apart (252 → 750 → 2500), so
crediting one two seconds late produces a large average bias from a brief self-correcting lag,
and the threshold was measuring the ladder's step size rather than the tracker's accuracy. It
now asserts on the FRACTION of samples where we believe the opponent is on a lower rung than
they are (must be under 10%; actual 3.1% and 4.9%). The residual transition lag itself is
**unexplained** and worth a look — it should not exist under assume-ASAP.

## 12. Replay gauntlet — Marc's acceptance-test idea, built (`--replay-gauntlet`)

2026-09-02 · Plays a bot against Marc's recorded action stream from `9413D9` (White, his
win, 279s). Gadget casts are re-aimed by the bot's own targeting rather than replayed at his
recorded coordinates, per his requirement — v3 files DO store the position, so this
deliberately routes 11/12/13 through `ApplyAction` instead of `ApplyRecorded`.

Fidelity is 98%: his recorded actions almost all still land, so the replay is faithful as an
action stream.

### As designed, the win-rate metric REWARDS RUSHING and must not be used

| bot | win rate | avg length | replay-Marc end HP | bot reaches ARMAGEDDON |
|---|---|---|---|---|
| PreRungCommit (oldest) | **100.0%** | 165s | 0.0% | 0% |
| current default | 90.0% | 193s | 9.6% | 0% |
| PreChargeAware | 85.0% | 202s | 13.3% | 0% |

**The ordering is inverted.** `CommitToRung` gained +5 on the ladder and loses 10 here, and
the arm that wins most is the one that kills a defenceless opponent fastest. A replayed human
cannot react, so any change that trades rush speed for economy looks like a regression. Used
as an acceptance test this would have rejected the single best change of the last two days.

Marc's falsification condition — "if the bot beats the sequence first try, I'm wrong" — did
fire. **It does not license the inference**, because the instrument is measuring a de-fanged
opponent. Note the last column: the bot reaches ARMAGEDDON 0% of the time in *every* arm,
including the one that wins 100%. Win rate here is completely insensitive to the thing he is
actually reporting.

### `--race` is the version that measures what he described

Makes the human's castle invulnerable, removing the rush as a win condition and leaving only
the economy race. Same device as the stall harness's `--protect-attacker`.

**A bug worth recording: setting `IsInvulnerable` alone is a NO-OP.** `ProcessStatuses` clears
it the moment `CurrentTick` passes `InvulnerableUntilTick`, which defaults to 0. The first
version of race mode did exactly that and produced output *byte-identical* to the unprotected
run — which is the only reason it was caught. Both fields must be set.

**Result, 30 games, current default bot:**

| | investments | ARMAGEDDON | end HP |
|---|---|---|---|
| replayed human | 8.00 | **100%** (at 272s) | 100% |
| bot | 7.60 | **0%** | 0% |

**Human first in 30, bot first in 0.** The bot dies to his ARMAGEDDON every single game.

### WHERE THE MONEY GOES — the open question from item 7, answered

Per game, averaged, in race mode:

| | |
|---|---|
| units | **$51,127** |
| gadgets | **$44,082** |
| repairs | **$23,463** |
| investments | $35,586 |
| unspent | $23,639 |
| **total earned** | **~$177,897** |

**ARMAGEDDON costs $121,221. The bot earns $178,000 and spends $118,672 of it on units,
gadgets and repairs.** It is not short of income — it has comfortably more than the rung
costs. It spends the race away. Every one of Marc's three observations is confirmed by a
line in that table.

**Read `--race` as a diagnostic, never as a result.** The human cannot die, so the bot can
never win it, and `killerInstinct` and the disengage system both read enemy castle HP and
therefore never fire. What it isolates is the race, which is exactly the failure Marc
reports: he survives the aggression, then wins on economy.

## 13. AutoSpawnerInsteadOfUnits — WORKS, but it is a trade; cap 8 recommended

2026-09-02 · Marc's instruction: the attack branch should buy the auto-spawner rather than a
constant unit stream, with unit spam demoted to last-resort defence. Reactive spending is
untouched, so the last-resort half is unchanged.

**Two versions were needed.** Dropping the payback test alone still stalled at level 3.2 and
left unit spend at $51,016 against a $51,508 baseline — it changed nothing. The allowance is a
FLOW, so while short of the next level the branch fell through and spent it on a unit and it
never accumulated. **Banking** fixes it, using TechEscalation's existing mechanism and its
reachability deadlock guard.

**The level cap turned out to be the whole story.**

| | ladder OVERALL (4 arms) | ladder mirror h2h | race-mode ARMAGEDDON | gauntlet win |
|---|---|---|---|---|
| default | 90.5 / 90.9 / 90.6 / 90.7 | — | **0%** | 90% |
| cap 8 | **90.7 / 91.1 / 91.0 / 91.0** | **+1.0 to +2.9** | **8%** | 80% |
| cap 16 | 90.1 / 90.5 / 90.5 / 90.5 | −0.7 to −3.2 | 10% | 80% |

Cap 16 banks toward $12,284 rungs and starves the board for too long; cap 8 reaches four free
units/sec for $2,267 cumulative and then resumes normal play. Earned investments rise ~0.06 in
both.

**RECOMMENDED: cap 8** (`AutoSpawnFirst8`). It is the only arm positive on the ladder, which is
the broadest instrument here (6,400 games per arm per mode), and it still takes race-mode
ARMAGEDDON from 0% to 8% — the first time the bot has reached it in any measurement. Cap 16's
race edge over cap 8 is one game in forty, i.e. noise.

**Stated as a judgement call, not a measurement.** Both caps cost 10 points of plain gauntlet
win rate (90% → 80%). I am discounting that because item 12 showed plain gauntlet win rate
rewards rushing a replayed opponent who cannot react — it ranked the oldest bot first at 100%.
But that is my reading of the instrument, not a fact, and it is the one number that says this
change is bad. It deserves scrutiny.

**Open inefficiency worth chasing next: banked money that is never spent.** Unspent at game end
goes $4,952 → $18,114 (cap 8) in normal mode while unit spend is unchanged at ~$26,500. The bot
banks and the game ends first. `TechEscalation` already has `TechTimeAware` for exactly this —
refuse to hold when the wait would eat too much of the remaining game — and the auto-spawner
banking has no equivalent.

## 14. CORRECTION — every A/B this session used the WRONG BASELINE

2026-09-02 · The ladder's reference contender is `new HeuristicBotAdapter(side)`, i.e. bare
`HeuristicBotSettings.Default`. **The deployed singleplayer bot is
`HeuristicBotSettings.EconomyBrakeProfile`** (`Singleplayer:EconomyBrake = true`), which sets
`RepairPriceCheck`, `RepairHpFloorPct`, `RepairMinIntervalSeconds`, `HazardAttackBlackout`,
`KillerInstinctInvestLockoutSeconds` and `KillerInstinctPushLatch`.

So every accept/reject margin recorded in items 1-13 is against **a bot Marc never faces**.

**It is not a small difference.** On the replay gauntlet, `EconomyBrakeProfile` reaches
ARMAGEDDON 22% of the time where the bare default reaches it ~0-2%. The deployed bot is far
better at the race than the thing I was measuring against.

**And it reverses item 13's verdict.** Gauntlet, `9413D9`, n=60, normal mode:

| | win rate | ARMAGEDDON | race: human/bot first | end HP | unspent |
|---|---|---|---|---|---|
| EconomyBrakeProfile | 80% | 22% | 13 / 12 | 65.9 | $21,592 |
| + auto-spawner cap 8 | **90%** | **27%** | **6 / 16** | **78.7** | **$13,907** |

The substitution is **+10 points against the real baseline**, not −10. The banked-money
inefficiency I flagged is also smaller in the change than in the baseline, so that concern was
backwards too. In `--race` the two are indistinguishable (38% ARMAGEDDON each).

**What survives from items 1-13:** the mechanisms. Charges really were failing silently, the
gadget layer really was blind to the rung past count 3, banking really was the missing half of
the substitution, and the money breakdown is a property of the game rather than of a profile.
**What does not survive:** the margins, and any accept/reject decision that turned on a margin.

**Rule going forward:** A/B against the profile that is actually deployed. The ladder needs a
`--reference <profile>` option so its reference contender is not silently the bare default;
until it has one, gauntlet runs must pass `--variant EconomyBrakeProfile` as the control arm.

## 15. Marc's two play-test games (E5CA98, A8452A) — and ArmageddonCommit shipped then reverted

2026-09-02 · First games on `EconomyBrakeAutoSpawnProfile` (cap 8). Recorded as
`heuristic_autospawn8`. New tool `--replay-spend <gameId>` replays BOTH action streams and
reports where each side's money went; both games reconstructed to the recorded outcome exactly
(262s/P1 and 256s/P2), so the numbers are trustworthy.

### `E5CA98` — Marc won. Where the bot's money went:

| | Marc | bot |
|---|---|---|
| units | $2,066 (**1 unit**) | **$24,500 (552 units)** |
| gadgets | $22,472 | $33,992 |
| investments | $50,522 | $50,522 |
| auto-spawner | $4,243 (level 10) | $2,267 (level 8) |
| **unspent** | $12,644 | **$111,908** |

**The bot ended $9,313 short of ARMAGEDDON having spent $24,500 on units.** A third of that
spend wins outright. That $24,500 would have taken the auto-spawner from level 8 to **15**
(5 free units/sec of [5,4,3,2,2] instead of 4/sec of [4,2,1,1]).

**Marc bought ONE unit; the bot bought 552.** He is already playing the strategy he is asking
the bot to play.

### Did the bot know it was losing the race? NO.

`OpponentEconomy` is referenced nowhere in `HeuristicBot` or the web app. It is built and
validated and wired into zero decisions. It changed nothing about how the bot played.

### ArmageddonCommit: shipped on gauntlet evidence, reverted on ladder evidence

Re-measured against the CORRECT baseline it looked strictly better on the replay gauntlet --
win 90.0 -> 91.7, ARMAGEDDON 27% -> 28%, end HP 78.7 -> 80.7, unit spend 28,240 -> 26,476,
unspent 13,907 -> 11,483 -- so it was shipped into the deployed profile.

**The ladder then rejected it, and the ladder is right.** Referenced against
`BrakeAutoSpawn8NoArma` (what Marc actually played):

| | nostart OVERALL | headstart OVERALL | Chipper (headstart) |
|---|---|---|---|
| cap 8, no commit | 92.7 | **92.0** | **98.8** |
| + ArmageddonCommit | 92.7 | **90.9** | **94.8** |

Neutral in nostart, −1.1 in headstart, driven almost entirely by the **Chipper** rung falling
4 points with its unspent money doubling. Headstart games begin at a higher InvestmentCount,
so the bot hits count 8 far more often, the commit zeroes its attack budget, and a lone unit
on its castle goes unanswered. That is exactly the unconditional-zeroing flaw item 10 named,
and exactly what the Chipper rung was built to detect.

**The gauntlet cannot see it** — its opponent is a passive replay that cannot punish an idle
bot. Reverted. Do not retry without making the commit conditional on the rung being reachable
AND on nothing needing an answer.

**Process note, on me:** I shipped this into the deployed profile on gauntlet evidence alone,
before running the ladder. It was caught before Marc played it, but the order was wrong --
ship after both instruments, not between them.

`BrakeAutoSpawn8NoArma` is kept so E5CA98 and A8452A stay reproducible by name.

## 16. RaceAwareSpending — the tracker wired in. A real trade, shipped for play-test.

2026-09-02 · Marc's rule: spend offensively so long as the spend does not put the bot behind in
the economic race; defensive spend stays allowed. `OpponentEconomy` is now instantiated by
`HeuristicBot`, updated every tick, and fed enemy gadget casts via `OnGadgetCast`.

### Correction during implementation: a per-item gate inverts the substitution

The first version asked "does THIS purchase put me behind", comparing an item price against the
race. A $3 tier-1 unit passes where a $102 auto-spawner level fails, so the gate systematically
bought the cheapest thing available: **auto-spawn level collapsed 8.0 → 1.1 while unit spend
ROSE $28,240 → $42,291.** It inverted the very substitution it was meant to protect.

Reframed as a PHASE test — "am I ahead, so is this a spending phase at all" — which leaves the
choice of what to buy to the substitution logic that already prefers the machine.

### The threshold sweep, and why 0

| `RaceSafetySeconds` | gauntlet win | ARMAGEDDON | race h/b | auto lvl | attack decisions |
|---|---|---|---|---|---|
| no gate | **90.0%** | 27% | 6 / 16 | 8.0 | 445 |
| −120 / −60 / −30 | 90.0% | 27% | 6 / 16 | 8.0 | 445 | *(never binds)* |
| **0** | 83.3% | **57%** | **13 / 30** | 7.5 | 227 |
| +10 | 76.7% | **78%** | 18 / 41 | **1.1** | **3** |

At +10 the bot essentially stops attacking. That is the tracker's documented bias doing it: its
income estimate for the opponent is an UPPER bound by construction, so the bot systematically
believes it is behind. **0 is the literal reading of Marc's rule and the only setting that both
binds and leaves the auto-spawner intact.**

### Ladder, referenced against what Marc played

| | nostart OVERALL | headstart OVERALL | mirror h2h | Chipper |
|---|---|---|---|---|
| cap 8, no gate | **92.7** | **92.0** | 54.2 / 55.2 | **100.0 / 98.8** |
| + race gate @0 | 92.3 | 91.6 | 54.0 / **56.3** | 98.0 / 97.0 |

−0.4 OVERALL both modes, mirror head-to-head up in headstart, Chipper down 2. Earned
investments up (7.26 → 7.31).

### THE FLAW TO FIX NEXT, and it is visible in one column

**Idle money explodes.** Against Chipper, unspent at game end goes **$725 → $26,192**; against
HumanClone **$3,790 → $15,440**. The bot stops spending but the saved money never reaches
ARMAGEDDON either — it is saving into a void, while against Chipper its castle drops 66.8 → 50.9
because a lone unit chips it unanswered.

The missing condition is the same one that sank `ArmageddonCommit` in item 15: **hold only if the
rung is actually reachable before the game ends.** `TechEscalation` has `TechTimeAware` for
exactly this shape. Until that is added, the gate is trading real board presence for savings it
often cannot spend.

**Shipped at 0 for play-test** because the race is the failure Marc reports and this is the only
change that has moved it. It is reversible: `Singleplayer:AutoSpawner = false`, or set
`RaceAwareSpending = false` in `EconomyBrakeAutoSpawnProfile`.

## 17. Ladder parallelised — 4.3x, and still bit-for-bit deterministic

2026-09-02 · Marc noticed the ladder was barely using his CPU. It ran games in a serial
`foreach`; they are independent given their spec, since each carries its own EngineSeed and
every bot is constructed inside the loop.

`Parallel.For` over specs, with results written into a pre-sized array and folded **in index
order** so the floating-point accumulations are summed in exactly the serial sequence. A shared
`Record` under a lock would have been right to a few decimals and produced a different CSV every
run, which is the quiet irreproducibility this file's header promises against.

**Verified, not assumed:** same `--seed` twice gives byte-identical gameplay columns. Only
`elapsed_s` and `ticks_per_s` differ, as they must.

On 20 cores: a 400-spec, both-modes, 3-contender run went from minutes to **91 seconds**;
throughput ~695k -> ~3.0M ticks/s. Note the THROUGHPUT column is now parallel wall clock, so it
is still a fair paired comparison between contenders in one run but is no longer a per-core cost.

## 18. ChipEconomics — the mechanism is right, the plumbing does not fire

2026-09-02 · Marc: remove the rule that stops the bot answering a single attacker, now that
there are better economics for trading units against HP.

The economic test itself is clean and needs no unit count and no tuned threshold: a blocker
absorbs roughly its own HP of damage that would otherwise hit the castle, so buy it when
`blockerMaxHealth * DollarsPerHp(me) > blockerCost`. It self-scales — repair 1 buys 10,000 HP
for $20 ($0.002/HP) so tanking is correctly right early, while repair 7 costs $8,837 for 17,800
HP ($0.50/HP) and a $3 body soaking 12 HP is then worth $6.

**First attempt cost 28 points and is recorded as a warning.** OR-ing the result into `inDanger`
took the gauntlet 83.3% -> 55.0% with castle HP falling 74.5 -> 50.5 — worse at the very thing
it was for. `inDanger` is a global MODE flag gating the whole offensive branch, the disengage
system and the reactive scorer at once. Same error shape as the first race gate: a coarse switch
used for a fine decision.

**Re-pointed at the dedicated purchase, it is a no-op.** Ladder referenced against `ShipControl`:
OVERALL identical to three decimals, Chipper identical, unspent unchanged. The
`BlockSingleChipper` credit/detection machinery it rides on is not firing — diagnosing that is
the next step, not more economics.

## 19. THE RACE GATE IS WHAT BROKE CHIPPER DEFENCE — reconsider shipping it

The same run compared `ShipControl` (race gate at 0, currently shipped) against `RaceSm60`
(threshold so low the gate never binds, i.e. the pre-gate bot):

| | OVERALL | Chipper | Chipper unspent | Chipper end HP |
|---|---|---|---|---|
| RaceSm60 (gate off) | **92.2 / 92.2** | **100.0 / 98.6** | **$704 / $1,238** | **66.7 / 69.6** |
| ShipControl (gate on) | 91.7 / 91.8 | 97.8 / 96.8 | **$26,223 / $18,753** | 49.6 / 57.7 |

The gate costs 0.5 OVERALL, 2-3 points of Chipper win rate, **17 points of castle HP against a
chipper**, and leaves **$26,223 idle**. Marc's instinct that the idle money and the chipper
exploit were the same issue is right, but the causality runs the other way from what I assumed:
the lone-chipper blind spot pre-existed, and **the race gate made it much worse** by keeping the
bot from spending while it was being chipped.

The race gate's case rests entirely on the gauntlet ARMAGEDDON column (27% -> 57%). Its cost is
now measured on three columns instead of one. That trade is worse than item 16 reported and the
decision to ship it should be revisited.

## 20. Wiper prices castle HP (Marc's fold-it-into-the-wiper suggestion) — big Tier4Spam win, but it does NOT fix the chipper

2026-09-02 · Marc: fold the lone-chipper case into the wiper rather than writing parallel logic.
He was right that the unit count was never the blocker -- **the budget was**. For a lone $3
chipper `committedEnemyValue * WiperMaxCostVsStackValue` is **$1.05**, so no unit in any roster
qualifies and the wiper silently declines. That is why `bot-checksum` reports `WIPE n=0` in every
game. Pricing the enemy army answers "is this stack worth killing"; a chipper poses a different
question, "what is it about to cost me".

The wiper budget now also counts `UnblockedDps * horizon * DollarsPerHp(me)`, raising the budget
and never lowering it.

**Ladder, referenced against `ShipControl`, n=400 both modes:**

| | Tier4Spam | Chipper | Chipper end HP | mirror h2h | OVERALL |
|---|---|---|---|---|---|
| ShipControl | 87.0 / 90.6 | 97.8 / 96.8 | 49.6 / 57.7 | 51.7 / 55.7 | 91.7 / 91.8 |
| + wiper HP @30s | **91.9 / 93.8** | 98.5 / 95.0 | 52.1 / 57.4 | 48.1 / 54.1 | **92.0** / 91.4 |
| + wiper HP @60s | 91.9 / 93.4 | 99.0 / 95.0 | 55.0 / 58.9 | 46.0 / 52.6 | 91.8 / 91.1 |

**Tier4Spam gains +4.9 / +3.2**, which is the standing weakness recorded in the backlog and
which nothing else this session has moved. But the mirror falls 3.6, headstart OVERALL falls 0.4,
and **the chipper case barely moves** — end HP 49.6 → 52.1, unspent still $25,642.

### Why it does not fix the chipper: `DollarsPerHp` is circular

HP is priced off the repair ladder, and the ladder position depends on how much the bot has
already repaired. Against a Chipper the bot is never `inDanger`, so it never repairs, so
`RepairCount` stays 0, so a repair still buys 10,000 HP for $20 — **$0.002/HP** — and the bleed
never looks expensive, right up until the castle is gone. The metric says HP is cheap *because*
the bot has been tanking, which is the thing it is supposed to price.

**And the helper's comment does not match its code.** `DollarsPerHp`'s doc says it "uses the
HEALING component of the preview, not the whole delta" precisely to avoid pricing HP at almost
nothing when the castle is healthy — but `healed = nextHealth - me.CastleHealth` at full HP is
almost entirely the max-health bump, which is exactly the case the comment claims to exclude.
The `0.2 * nextMax` fallback only fires when that delta is under 1.

So the wiper change is a real win on a different axis (Tier4Spam) and the chipper needs the HP
valuation fixed first. Kept behind `WiperPricesCastleHp`, not shipped, pending that.

## 21. Single-chipper investment clock — Marc's rule, SHIPPED at 30s

2026-09-02 · Marc's rule from live play, replacing the HP-pricing approach rather than refining
it: if the next rung is close, tank the chip and save; if it is far, match the threat now; at
two or more attackers the existing wiper owns the decision.

**It needs no new logic and no HP valuation.** It reuses `timeToInvestSeconds` — money still
needed over income, both facts — and raises the wiper's budget for the single-attacker case.
The tank-then-match behaviour Marc describes falls out rather than being scripted: while the
rung is close the bot holds; the instant the investment lands, money drops to near zero and the
next rung is dearer, so `timeToInvestSeconds` jumps and the rule flips to match.

**It also sidesteps the circularity that sank item 20.** `DollarsPerHp` reads the repair ladder,
and ladder position depends on how much the bot has already repaired — so against a chipper it
never repairs, `RepairCount` stays 0, a repair still buys 10,000 HP for $20, and the bleed never
looks expensive. The metric called HP cheap *because* the bot was tanking.

### The tank window is the parameter that matters, and the PATIENT setting won

| `ChipperTankSeconds` | Chipper (nostart) | Chipper end HP | OVERALL | gauntlet ARMAGEDDON |
|---|---|---|---|---|
| control | 97.8 / 96.8 | 49.6 / 57.7 | 91.7 / 91.7 | **55%** |
| 8 | 99.4 / 95.0 | **59.2 / 62.7** | 91.6 / 91.5 | — |
| 15 | **99.6** / 96.0 | 55.3 / 59.6 | 91.7 / 91.7 | **32%** |
| **30 — shipped** | 99.4 / **97.1** | 51.8 / 58.6 | **91.8 / 91.9** | **55%** |

At 15s the bot matches sooner, spends more, and **buys chipper defence with the win condition** —
gauntlet ARMAGEDDON falls 55% → 32%. At 30s the race is untouched (55%, bot first in 29 of 60,
identical to control) and the Chipper rung still improves in both modes. Tanking while the rung
is close really is right, and matching only once it is far really is enough — the longer window
vindicates Marc's framing over the impatient reading of it.

**Shipped** into `EconomyBrakeAutoSpawnProfile` along with `ChargeAwareEverywhere`, which every
arm measured alongside the clock carried — shipping without it would ship a configuration that
was never measured. Guards pass: `--unit-charge-check`, `--economy-tracker-check`.

### Not shipped: `WiperPricesCastleHp`

The castle-HP wiper budget from item 20 is a real win on a different axis — **Tier4Spam +4.9 /
+3.2**, the standing weakness nothing else has moved — but combined with the clock it costs the
mirror (51.7 → 46.1) and drops gauntlet ARMAGEDDON to 32%. Left behind its flag as a candidate
for the Tier4Spam problem specifically, not as a chipper fix.

## 22. Marc's 0240D8 loss — his diagnosis is half right, and the half that is wrong matters

2026-09-02 · New tools: `--replay-timeline` (when each gadget upgrade landed, with the other
side's progress toward the same one) and unit-buy-by-tier in `--replay-spend`. Both reconstruct
the recorded outcome exactly (259s, winner P1).

### The gadget-upgrade hypothesis is NOT supported

Marc: *"I won the moment I got the Reinforcements_3 upgrade. The bot didn't recognize that it was
1 cast away from achieving that same level."*

The bot **was** exactly 1 cast away — and it made that cast 5 seconds later:

```
  161s  P1 -> reinforcements_3   |  P2 on reinforcements_2, xp 800/900 = 1 cast away, $13,425 in hand
  166s  P2 -> reinforcements_3   |  P1 on reinforcements_3, xp 0/1000 = 10 casts away
```

Every upgrade race that game was neck and neck — reinforcements_2 4s apart, cash_2 2s apart,
nuke_2 12s apart, reinforcements_3 5s apart. **The bot did not lose the upgrade race.** Whatever
lost the game, it was not failing to recognise it was one cast from reinforcements_3.

### The overspending observation IS right, and it is the whole story

| | Marc | bot |
|---|---|---|
| units | **$6,536 (4 units)** | **$35,927 (399 units)** |
| gadgets | $22,712 | $21,872 |
| repairs | **$47** | **$2,575** |
| auto-spawner | $1,204 (lvl 6) | $1,658 (lvl 7) |
| unspent | $7,044 | **$43,664** |

**Unit-buy intent by tier:**

```
  P1: T6(bread $338) x1   T7(eggo $2066) x3
  P2: T1 x92  T2 x59  T3 x67  T4 x92  T5 x62  T6 x16  T7 x11
```

Marc bought **four units, all tier 6-7**. The bot bought **399, of which 310 were tier 1-4 chaff
at $3-18 each** — and still ended at 0% castle HP. The chaff bought nothing.

Note the repair claim is directionally right but small: $2,575 against $35,927 of units. **Units
are 93% of the waste.** Do not chase repairs first.

### Why: the leak moved to the branch both fixes deliberately exempt

`AutoSpawnerInsteadOfUnits` and the race gate are both scoped to `!preferDefense`, because Marc
asked for defensive spend to stay allowed. So the offensive channel is now governed and **the
money is going out the reactive one instead**.

And reactive spending uses `ScoreUnit` cost-efficiency, which CLAUDE.md already records as
"chronically defaults to the cheapest unit that still scores well". `RawPower` is deliberately
restricted to `!preferDefense` because reactive RawPower regressed the ONNX models — **but that
rejection was measured against models, not against a human casting reinforcements_3**, which
puts five FREE tier-7 units on the field per cast and cleaves tier-1-4 chaff without noticing.

**The next hypothesis is therefore: reactive defence needs a power floor against high-tier
threats** — not more spending, and not less, but buying units that survive a swing. That is a
re-allocation, which is the shape that has worked, and it targets 93% of the waste.

## 23. CORRECTION to item 22, and the real mechanism behind Marc's 0240D8 loss

2026-09-02 · Marc pushed back on item 22's "310 chaff units" framing and he was right. **I counted
units instead of dollars.** In dollars:

| tier | cost | count | spend | share |
|---|---|---|---|---|
| T1-T4 | $3-18 | 310 | **$2,771** | **7.7%** |
| T5 alpacco | $81 | 62 | $5,022 | 14.0% |
| T6 bread | $338 | 16 | $5,408 | 15.1% |
| **T7 eggo** | **$2,066** | **11** | **$22,726** | **63.3%** |

The chaff is 7.7% of the money and is doing exactly what it should — stalling. **Eleven tier-7
units are 63% of the spend.** Item 22's conclusion pointed at the wrong tier and its proposed fix
("a power floor on reactive defence") would have made this *worse*.

### The code Marc was thinking of

`WiperMinIntervalSeconds = 4.0` (HeuristicBot.cs:886), gating the wiper block at line 4108: a
wiper needs time to walk in and swing before a second one can be judged necessary.

### Attribution, measured rather than argued

New: unit spend is now tallied by the reason it was bought (`SpendWiper` / `SpendReactive` /
`SpendAttack` / `SpendChipBlock`), plus wiper picks by tier. Gauntlet against 0240D8, n=30:

```
UNIT SPEND BY REASON: wiper 6,216   reactive 9,077   attack 1,622   chip-block 0
wiper picks by tier:  T4 x1.0   T7 x3.0
```

**The wiper does buy the tier 7s** — $6,198 of its $6,216 is three eggos. Marc's hypothesis is
confirmed for the wiper's share. But the wiper is only ~37% of unit spend; **reactive is larger**,
and it is buying tier 7 as well.

### WHY reactive buys tier 7 — and it is an economic asymmetry, not a tuning miss

```csharp
int dominantEnemyTier = enemyUnits.GroupBy(u => u.Tier)
    .OrderByDescending(g => g.Sum(u => (double)u.Damage)).First().Key;
var outclassing = RankPool(dominantEnemyTier + 1);
var tierMatched = RankPool(dominantEnemyTier);
```

The reactive pools are floored at the enemy's damage-weighted dominant tier. **Marc's
`reinforcements_3` puts five FREE tier-7 units on the field per cast** — 5 x 5,180 damage = 25,900,
which dominates everything else instantly. So `dominantEnemyTier` becomes 7 and reactive defence
is *forced* to buy tier 7 or 8: eggo at $2,066 a unit.

**Marc pays $1,440 for a cast that yields $10,330 of tier-7 units. The bot answers with real money
at $2,066 each.** That is the asymmetry that loses the game, and it is invisible to the bot because
`dominantEnemyTier` counts units on the field without asking what they cost the opponent.

**Next hypothesis: tier-match against what the opponent PAID for, not what is on the field.** The
`OpponentEconomy` tracker already distinguishes free spawns from purchases — it has to, to price
their economy — so the information needed is already being computed. Blocking free units is what
chaff is for, and the chaff is already only 7.7% of spend.

## 24. One wiper at a time — SHIPPED; and what actually sends the T5/T6 units

2026-09-02 · Marc's two questions about 0240D8, answered by reading and by measurement.

### Q1: is there logic delaying a repeat T7 until the first dies? NO.

The only gate was `WiperMinIntervalSeconds = 4.0` — a wall clock, not a liveness test, permitting
up to 64 buys in a 259s game. Nothing anywhere tracked whether a previously bought unit was still
on the field. That is how 0240D8 saw **eleven tier-7 eggos for $22,726, 63% of unit spend**.

`OneWiperAtATime` records the wiper's `InstanceId` and refuses a repeat while it lives. Keyed on
instance rather than unit type, because the auto-spawner and reinforcements put units of the same
type on the field for free and those are not the wiper.

**Measured** (gauntlet vs 0240D8, n=40): wiper spend **$6,216 → $4,150**, T7 picks **3.0 → 2.0**,
total unit spend $16,910 → $15,067. **Ladder unmoved** — OVERALL 91.8/91.9 both arms, every rung
within noise, because ladder opponents rarely field a threat tough enough to trigger repeat
expensive wipers (the historical `WIPE n=0`). No measured cost, real saving against a human-shaped
opponent. Shipped.

**A bug caught while writing it:** the first version captured the id from `state.Units.Last()`
*after* the `Act(...)` call. `Act` returns **true when it merely QUEUES** the spawn for a later
tick, so that read could grab an auto-spawner body or an enemy unit. The capture now happens
inside the action where `SpawnUnit` actually succeeded. Same `Act()` false positive as backlog
item 6 — it is worth fixing that generally.

### Q2: what sends the T5 and T6 units? Reactive `SpendOnUnits`, forced by the tier floor.

```
wiper    picks by tier: T4 x1.0   T7 x3.0
reactive picks by tier: T1 x46.0  T2 x10.0  T3 x8.0  T4 x47.5  T5 x8.4  T6 x15.5  T7 x1.0
attack   picks by tier: T1 x14.0  T3 x6.0   T4 x21.0  T5 x10.0  T6 x1.0
```

The wiper picks only T4 and T7. **Reactive T6 bread is 15.5 x $338 = $5,239, 58% of all reactive
spend** — the mid-tier drain Marc identified.

It is not choosing mid-tier units, it is being **denied cheaper ones**:

```csharp
var outclassing = RankPool(dominantEnemyTier + 1);
var tierMatched = RankPool(dominantEnemyTier);
```

Both pools are floored at the enemy's damage-weighted dominant tier, so whatever dominates the
board sets a MINIMUM tier the bot may buy. Dominant 6 → bread $338 is the cheapest legal pick;
dominant 7 → eggo $2,066. And `dominantEnemyTier` counts units on the field without asking what
they cost the opponent, so Marc's FREE `reinforcements_3` squad — 5 x 5,180 damage — sets that
floor at 7 for nothing.

**Next, and it needs Marc's direction:** the floor is a tier-matching heuristic doing the job a
stall stream should do. Chaff is 7.7% of spend and blocks anything, per the survival law. Two
shapes: drop the floor when a stall stream is already holding, or exclude free-spawned units from
`dominantEnemyTier`. The second is narrower; the first is closer to how he says he plays.

## 25. What ACTUALLY sends the T5/T6 units: my own charge-aware fallback

2026-09-02 · Marc pushed back twice, correctly. Item 24 blamed the tier floor, and he pointed out
that cannot be it: if five tier-7 units set `dominantEnemyTier` to 7, the pools are floored at 7
and the bot **cannot legally buy tier 6**. Something else is choosing them.

Instrumented every reactive purchase of tier 5+ with the dominant tier at the time and the route
the pick came through. Gauntlet vs 0240D8, n=30:

```
reactive T5+ buys, by DOMINANT ENEMY TIER: dom1 x2.0  dom2 x6.0  dom5 x2.0  dom7 x14.8
reactive T5+ buys, by ROUTE: matched/outclass 3.0   charge-fallback 21.8   any-affordable 0.0
```

**21.8 of 24.8 mid/high-tier reactive buys — 88% — come from `ChargeAwareFallback`**, the change
shipped as iteration 1. And 14.8 of them happen while the dominant tier IS 7, exactly the case
Marc said the floor should have excluded.

### The mechanism

```csharp
foreach (var def in roster)                 // <- THE WHOLE ROSTER
{
    if (def.Cost <= 0 || def.Cost > spendable) continue;
    if (!me.HasUnitCharge(def.Id)) continue;
    ...
}
```

The fallback scans **the entire roster**. It does not carry the tier floor, the outclass rule, or
any of the discipline the ranked pools encode — by design, since its job was only to avoid a
silently failed purchase. So when the pools correctly pick eggo (T7) against a tier-7 threat and
eggo is out of charges, the fallback re-picks across all eight tiers and lands on **bread ($338)**,
because `ScoreUnit`'s `SurvivabilityMultiplier` crushes everything that gets one-shot by a 5,180
damage attacker and bread is crushed least of the affordable options. Repeatedly: **15.5 breads a
game, $5,239, 58% of reactive spend.**

**Iteration 1 fixed a silent no-op and introduced a silent substitution.** It stopped the bot
buying nothing; it did not stop it buying the wrong thing. The measurements that accepted it —
units/sec up, idle money down, invests unmoved — are all still true, and none of them could have
seen this, because a substituted purchase looks identical to an intended one in every counter.

### The fix shape, for Marc's call

The fallback should not be free to leave the tier band the pools chose. Two options:

1. **Fall back within the same pool** — if the T7 pick is drained, take the next-best *tier 7+*
   with a charge, and if there is none, buy nothing this decision. Minimal, keeps iteration 1's
   benefit, removes the substitution.
2. **Fall back to chaff** — if the intended unit is on cooldown, buy the cheapest body instead.
   Closer to how Marc describes playing: the stall stream holds while the real answer recharges,
   and chaff is 7.7% of spend.

(2) is the more interesting hypothesis and matches the survival law; (1) is the conservative one.

## 26. Charge fallback capped at tier 4 — SHIPPED (Marc's rule)

2026-09-02 · Marc: *"the fallbacks should not go above Tier 4. If there are no charges at Tier 4
and below, don't spawn anything for that decision."*

`ChargeFallbackMaxTier = 4`, applied inside the fallback's roster scan. Nothing at or below the
cap with a charge means the decision buys **nothing** — the pools already decided what this
decision wanted, and a decision that cannot have it should wait rather than substitute a worse
answer at ten times the price.

The reasoning is the survival law: the right stand-in for a recharging answer is a BODY, not a
cheaper copy of the answer. `MoveAndFight` stops a unit attacking the castle whenever anything is
in contact, so chaff buys the same seconds for 1-2% of the price.

**Gauntlet vs 0240D8, n=40:**

| | uncapped | **cap 4** | cap 2 |
|---|---|---|---|
| charge-fallback T5+ buys | 22.8 | **0.0** | 0.0 |
| reactive T6 picks | x16.0 | **x1.0** | x0 |
| reactive spend | $9,295 | **$6,500** | $8,121 |
| wiper spend | $4,150 | **$2,087** | $4,473 |
| total unit spend | $15,067 | **$11,576** | $12,594 |
| win rate | 100% | 100% | 100% |

**The substitution is eliminated outright** and unit spend falls 23%. Cap 2 is worse — mid-tier
buys come back through matched/outclass instead and idle money balloons to $42,836 — so 4 is not
merely Marc's instruction, it is also the better of the two tried.

**Ladder** (referenced against the uncapped bot): nostart OVERALL 91.7 → 91.9 with the mirror
49.5 → 50.6; headstart 92.2 → 91.9 with the mirror 58.0 → 56.4. Roughly neutral, small gain
nostart, small loss headstart.

**CHECKSUM MOVES.** 4 is the DEFAULT for `ChargeFallbackMaxTier`, so this changes bare
`HeuristicBotSettings.Default` too: `bot-checksum --games 24` goes
`26944708B774C609CA6D3E5ECA43815C` -> **`3EFC8EDB17850125BFCB555440AA0C40`**.
`ShipUncappedFallback` reproduces the previous behaviour. Guards pass.

### The pattern worth carrying forward

Items 24, 25 and 26 were one question asked three times, and I got it wrong twice before Marc's
pushback forced instrumentation:

1. **"chaff is the problem"** — wrong, I counted units instead of dollars. Tiers 1-4 were 7.7%.
2. **"the tier floor forces mid-tier buys"** — wrong, and Marc spotted the contradiction: with
   five tier-7 units on the field the floor is 7 and tier 6 is *excluded*.
3. **The fallback bypasses the floor entirely** — right, and only visible once the purchase route
   was instrumented rather than reasoned about.

Both wrong answers were plausible readings of code I had already read. The rule that would have
saved the round trips: **when asked why the bot did something, instrument the decision rather than
re-reading the logic that should have produced it.** The counters cost ten minutes; the two wrong
diagnoses cost more than that and would have shipped a fix that made it worse.

---

## 27. `RacePricePurchases` — the race gate prices the PURCHASE, not just the position

**ACCEPTED** (Marc's call, 2026-09-03: "this is how this gate should have worked from the start").

### The failure it fixes, from `73DBD4`

At **175s**, with an **empty enemy field** — zero enemy units anywhere on the map — the bot
bought a $23,000 tier 8. Instrumented with `--replay-why`, every alternative explanation is
ruled out at the decision point:

| candidate | verdict |
|---|---|
| wiper | FAILS — 0 committed units, budget `0.35 x $0 = $0` |
| rich mode | no — $24,607 against a $69,000 threshold |
| reactive defence | unreachable — nothing on the field |
| **offensive push** | **this one** |

The pick itself is not a malfunction. `MultiplicativeUnitValue` scores `effHP x DPS / cost`,
which for White rises monotonically with tier:

```
T7 eggo  $ 2,066  hp   4,690  dmg  5,180 x 0.66/s   score      7,786
T8 corn  $23,000  hp 105,820  dmg 40,500 x 0.20/s   score     42,857
```

Corn is rank 1 of the whole roster whenever it is affordable, and the survivability multiplier
is not even involved (no enemies ⇒ 1.0 for everything). **Nothing in that formula knows $23,000
is most of an investment rung.**

The race gate was consulted and *approved* it:

```
RACE GATE: bot 69s to ARMAGEDDON, modelled human 81s -> offence ALLOWED
AFTER PAYING $23,000: bot 100s vs human 81s -> BEHIND by 19s (cost 31s of race position)
```

`RaceAllowsOffence` runs in `Decide()` **before anything has been picked**, so it can only ask
"am I ahead", never "am I still ahead after paying".

### Why this is not the per-item gate that already failed

The first race gate (item 12) priced items *before* choosing and **inverted the substitution**:
a rejected $102 auto-spawner level fell through to a $3 unit, auto-spawn level collapsed
8.0 → 1.7 and unit spend ROSE $28,240 → $42,291.

**Position in `SpendOnUnits` is the whole correction.** The new check sits *after* the
auto-spawner substitution block, so the machine keeps first refusal and only the fall-through
unit purchase is priced — the inversion is structurally unreachable. And **a failure BANKS
rather than downgrading**: buying a cheaper body with the same money is precisely the behaviour
that produced the inversion. Reactive defence never reaches the line (`preferDefense`), so
Marc's standing rule that defensive spend stays allowed is preserved.

### Measured

- **Ladder, n=400, 6,400 games/arm:** OVERALL 91.9% → **92.0%**, every rung inside its interval,
  earned investments unchanged to two decimals. Costs nothing.
- **Gauntlet on `B9A8D4`:** fires **21 times a game** and removes the tier-7 purchase from the
  attack mix entirely (T1–T4 only afterwards). Win rate 100% in both arms, so the gauntlet
  **cannot adjudicate it** — same instrument blindness that motivated `ChipperBaseline`.
- **Gauntlet on `73DBD4`, the motivating game: byte-identical, 0 priced holds.** The live bot
  there already wins the race 60/60, so the gate never binds. **The fix is justified by the
  arithmetic at the actual decision point, not by the gauntlet reproducing the loss.** Stated
  plainly because it is the weakest part of the evidence.
- **`bot-checksum --games 24` UNCHANGED** at `3EFC8EDB17850125BFCB555440AA0C40` — the flag
  defaults false, so every existing benchmark and the reference bot are untouched. It is enabled
  only in `EconomyBrakeAutoSpawnProfile` (the shipped singleplayer opponent).
  `HeuristicBotSettings.PreRacePriced` reproduces the previous deployed behaviour.

### What it does NOT fix — cumulative bleed

`B9A8D4` (most recent game, White mirror, Marc won) is the counter-example and it is not
addressed by this change:

```
ARMAGEDDON  bought at   P1 260s   P2 262s      <- the bot lost the race by TWO SECONDS
units       P1 $4,132 (2 units)   P2 $18,120 (144 units)
```

**$16,528 of that $18,120 is eight eggos at $2,066** — 91% of unit spend in 8 of 144 purchases.
At income 2,500 that is **6.6 seconds of income, against a 2-second losing margin**. But no
single $2,066 purchase moves the race clock by more than 0.83s, so a per-purchase test only
catches them once the margin is already thin.

The open problem is therefore **a budget, not a gate**: eight individually-affordable purchases
that are collectively decisive. Next candidate is a cumulative race-spend allowance over the
endgame phase rather than a per-item test.
