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
