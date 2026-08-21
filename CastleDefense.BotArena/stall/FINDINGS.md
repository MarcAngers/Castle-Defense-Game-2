# Chump-blocking: measured findings

Everything here comes from `CastleDefense.BotArena.exe stall-test` (source: `../StallTest.cs`).
Raw CSVs sit next to this file. Measured 2026-08-21.

**Kept out of `bin/` deliberately.** `CLAUDE.md` records a 2026-07-14 data loss caused by a
`bin/` cleanup; the arena writes there by default, so anything worth keeping is copied here.

---

## The setup, and what it is not

Both castles pinned at **23,000 HP**, both incomes at **$5,000/s** so nothing is ever
money-limited. The attacker gets a fixed force and never reinforces it beyond the schedule.
The defender either does nothing (the control) or spawns one **tier-1** unit every N ticks
and nothing else. No gadgets, no investing, no repairs on either side.

Three properties of the harness that shape how the numbers may be read:

- **Deterministic, and seeds do not matter.** The engine RNG only drives unit *y*-position,
  which nothing in combat reads. Verified: seeds 999 / 4242 / 12345 give 0 differing cells
  out of 80. **The only real sample dimension is TEAM, so n = 8 per cell**, not the run count.
- **Seats are identical here.** 0/480 cells differ between P1-attacks and P2-attacks in
  experiment 1, and 0/80 with forces and escorts added. The seat bias `CLAUDE.md` warns about
  does not reach this scenario — measured, not assumed. Later sweeps therefore run seat 1 only.
- **Force members are spawned with `ignoreCost`.** Five tier 8s is a median **$91,960** and
  could not be banked in 4 seconds at $5,000/s. The force schedule is a *stress test*, not an
  affordable opening. List prices are still accounted so the cost comparisons hold.

Two arms, always. The chump stream *blocks*, and it also walks on and *razes the attacker's
castle* once the force is dead. `--protect-attacker true` shields the attacker's castle and
isolates the first from the second; `false` asks who wins the game. Conflating them reads the
counter-attack as if it were stalling.

---

## Experiment 1 — one attacker, no support

`stall_isolated.csv` (960 runs), `t8_threshold.csv`, `stall_realistic.csv`.

### The threshold is the attacker's ATTACK PERIOD

Not its HP, not its damage, not its tier. `MoveAndFight` sets `CurrentSpeed = 0` and attacks
the *unit* whenever any enemy is in range; reaching a castle needs `FindTargetsFast` to come
back empty. **A body in contact is a hard stop, not a damage race.**

Every tier-8 unit in the game has its attack speed clamped to 0.20/s — one swing per 5.00s —
because `GameDataManager` recomputes attack speed from damage and move speed and clips at 0.2.
The boundary lands in exactly the same place for all eight teams:

| one chump every | 4.00s | 4.33s | 4.67s | 4.83s | **5.00s** | **5.17s** | 5.33s | 5.67s | 6.00s |
|---|---|---|---|---|---|---|---|---|---|
| all 8 teams | hold | hold | hold | hold | **hold** | **259–378s** | 134–198s | 74–108s | 54–90s |

### Price

**$2.00 per second of delay** (pooled median over every cell where the castle still fell) —
0.04% of the test income. Per tier: T5 $2.50/s, T6 $2.11/s, T7 $2.14/s, T8 $0.84/s.

### Kill vs neutralise — the tier decides which

Every blocker clamps to the same *x*, so the whole pile swings at once and its damage scales
with pile size. Killing the attacker outright costs:

| tier | cheapest rate that kills 8/8 | bodies | peak stack | time |
|---|---|---|---|---|
| T5 | 0.5/s | 46 | 2 | 91s |
| T6 | 1.5/s | 146 | 6 | 97s |
| T7 | 2/s | 434 | 12 | 217s |
| T8 | 6/s | 2,825 | 58 | 337s |

The real game ends at 600s, so **against tier 8 the tactic neutralises but does not kill;
against tier 5/6 it genuinely kills.**

### Unshielded, the stream wins the game

At ≥6 bodies/s a defender doing nothing but spamming the cheapest unit in the game beats the
attacker outright in 8/8 teams at every tier, median 376s against a tier 8.

---

## Experiment 2 — attacking forces, and escorts

`force_isolated.csv` (4,576 runs), `force_realistic.csv` (1,536), `t8_force_threshold.csv`,
`t8_fine2.csv`. Force members spawn **1 second apart**; the escort is one **tier-4** unit
per second, which stops when the last high-tier unit dies (it exists to escort them).

### Seconds to kill an UNDEFENDED castle (median of 8 teams)

| tier | ×1 | ×2 | ×3 | ×4 | ×5 | | ×1 +escort | ×5 +escort |
|---|---|---|---|---|---|---|---|---|
| T5 | 198 | 102 | 70 | 54 | 45 | | 28 | 23 |
| T6 | 93 | 51 | 37 | 30 | 26 | | 26 | 17 |
| T7 | 29 | 22 | 20 | 19 | 18 | | 19 | 17 |
| T8 | 33 | 29 | 29 | 29 | 29 | | 27 | 27 |

T7/T8 saturate because their time is travel-dominated, not damage-dominated. **A tier-4 stream
with no high-tier units at all razes the castle in 30s** — see the balance note below.

### Force size raises the required chump rate SUPERLINEARLY

`ClampToContact` skips friendly units, so a force does **not** queue up behind its own front
member: they overlap at the same contact point and all swing at the line. Tier 8 gives the
cleanest measurement, because its swing period is exactly 150 ticks for all eight teams.

Two statistics, because the naive one is not trustworthy alone. **Naive** = the slowest
spawn period that holds all 8 teams. **Robust** = the slowest period such that it *and every
faster period* also hold. Quote the robust one.

| force | ×1 | ×2 | ×3 | ×4 | ×5 |
|---|---|---|---|---|---|
| naive threshold | 150 ticks | 60 | 45 | 24 | 15 |
| **robust threshold** | **150 ticks** | **60** | **30** | **24** | **10** |
| **chumps per enemy swing** (robust) | **1.00** | **2.50** | **5.00** | **6.25** | **15.00** |
| ratio to force size | 1.00× | 1.25× | 1.67× | 1.56× | 3.00× |

Doubling the force costs the defender rather more than double the click rate; five attackers
need fifteen bodies per swing, not five.

**Phase caveat, and it is a real one.** The two rows differ because near the boundary the
outcome depends on how the spawn period lines up with the 150-tick swing period — the response
is *not monotone* in rate. At ×5 the line holds 8/8 at 15 ticks, 6/8 at 12, and 8/8 again at 10.
Single cells within a step or so of a boundary are not trustworthy; only the "all 8 teams"
columns are, and only the robust threshold should be quoted. A human clicking is not periodic,
so this is an artefact of the harness rather than a property of the game.

### What it costs to stop each force (no escort)

Cheapest rate holding all 8 teams; "def $" is spend at the moment the force died.

| tier | force | rate | attacker $ | defender $ | def/atk |
|---|---|---|---|---|---|
| T5 | ×1 | 0.5/s | 61 | 77 | 1.35× |
| T5 | ×5 | 2/s | 305 | 149 | 0.54× |
| T6 | ×1 | 1.5/s | 331 | 192 | 0.91× |
| T6 | ×5 | 10/s | 1,655 | 686 | 0.65× |
| T7 | ×1 | 2/s | 1,526 | 996 | 0.56× |
| T7 | ×5 | 30/s | 7,630 | 4,294 | 0.45× |
| T8 | ×1 | 0.25/s | 18,392 | **600** | **0.04×** |
| T8 | ×5 | 2/s | 91,960 | **4,800** | **0.07×** |

**Defence gets cheaper, in relative terms, the bigger and more expensive the force gets.**
Against tier 8 the defender pays 4–7 cents on the attacker's dollar. Tier 7 is the hardest
tier to hold — it is fast enough to close and cheap enough to mass.

### The tier-4 escort BREAKS the tactic

| chumps/s | 30 | 15 | 10 | 6 | 3 | ≤2 |
|---|---|---|---|---|---|---|
| escorted T5/T6/T7 forces, teams held (of 8) | 5–7 | 0–1 | 0–1 | 0 | 0 | 0 |
| escorted T8 forces | 8 | 4–6 | 0–1 | 0–2 | 0 | 0 |

**No chump rate up to 30/s holds an escorted tier-5, -6 or -7 force.** Only tier-8 forces can
be held, only at 30/s, and it costs ~$36,000 and ~20,000 bodies — at ×1 that is **1.26× the
attacker's own spend**, the only configuration measured where blocking is a losing trade.

### The escort really does escort — this is not just "a T4 stream is scary"

The `force 0` reference arm runs the identical escort stream with no high-tier units at all.
At 15 chumps/s:

| force | escort only | ×1 | ×2 | ×3 | ×4 | ×5 |
|---|---|---|---|---|---|---|
| T5 teams held | **8/8** | 1/8 | 1/8 | 1/8 | 1/8 | 1/8 |
| T6 teams held | **8/8** | 1/8 | 0/8 | 0/8 | 0/8 | 0/8 |
| T7 teams held | **8/8** | 1/8 | 1/8 | 1/8 | 1/8 | 0/8 |

The chump line stops the escort stream on its own. Adding **one** high-tier unit collapses it.
The escort breaks the line and the big unit walks through the hole — exactly the mechanism the
tactic is named for.

### Unshielded: does the defender still win?

| | 30/s | 15/s | 10/s | 6/s | 3/s |
|---|---|---|---|---|---|
| no escort, T8 ×1 | 8/8 | 8/8 | 8/8 | 8/8 | 7/8 |
| no escort, T8 ×5 | 8/8 | 7/8 | 5/8 | 1/8 | 0/8 |
| **with escort, any force** | 5–7/8 | 0–1/8 | 0–1/8 | 0 | 0 |

---

## Balance note that fell out of this: tier-4 spam dominates tier 8

Against an undefended castle, a $20/s tier-4 stream does the job in comparable time to a
single tier 8 for a median **28× less money**:

| | Black | Blue | Green | Orange | Purple | Red | White | Yellow |
|---|---|---|---|---|---|---|---|---|
| 1× T8 cost | 14,040 | 19,416 | 17,880 | 19,928 | 12,760 | 18,904 | 23,000 | 13,272 |
| 1× T8 kill | 33.4s | 19.2s | 16.4s | 23.9s | 33.4s | 33.4s | 61.7s | 33.4s |
| T4 stream cost | 650 | 667 | 672 | 270 | 925 | 403 | 558 | 527 |
| T4 stream kill | 25.4s | 28.6s | 31.3s | 17.8s | 36.5s | 30.0s | 30.5s | 30.0s |
| ratio | 21.6× | 29.1× | 26.6× | 73.8× | 13.8× | 46.9× | 41.2× | 25.2× |

Mechanism: tier-4 units move at 7–23 px/tick against a tier 8's 1–5, so they arrive 15–50s
earlier, and their attack speed is clamped at the *top* (5.0/s) where a tier 8's is clamped at
the *bottom* (0.2/s). This is a separate finding from the chump-block and has not been checked
against a *defended* castle in a real game.

---

## What is still unmeasured

- **No gadgets on either side.** A firebomb or nuke over the pile resets the whole clock.
  This is the single biggest gap between these numbers and a real game.
- **Mirror blockers only** — the blocker is always the attacker's own team's tier 1, and
  tier-1 cost/HP vary 1–4× across teams. `--blocker all` would settle the best blocker.
- **$5,000/s hides the real constraint.** Nothing is money-limited, so what binds here is
  click rate. The reachable region in a real game is roughly 0.25–2/s.
- **Escort tier and cadence were not swept** — only tier 4 at 1/s, because that is what was
  asked for. The `force 0` arm suggests tier and cadence matter a great deal.

---

## Reproducing

```
# experiment 1
stall-test --teams all --seat both --tiers 5,6,7,8 \
    --intervals 1,2,3,5,10,15,20,30,45,60,90,120,180,240 --protect-attacker true
stall-test --teams all --tiers 8 --intervals 120,130,140,145,150,155,160,170,180

# experiment 2
stall-test --teams all --seat 1 --tiers 5,6,7,8 --forces 0,1,2,3,4,5 --escorts 0,4 \
    --intervals 1,2,3,5,10,15,20,30,45,60,90,120 --protect-attacker true
stall-test --teams all --seat 1 --tiers 8 --forces 1,2,3,4,5 \
    --intervals 10,12,15,16,18,20,21,22,24,25,26,28,30,32,35,38,40
```

`gen_report.py` in this folder rebuilds the published HTML report straight from the CSVs, so
no figure in it is transcribed by hand.
