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
