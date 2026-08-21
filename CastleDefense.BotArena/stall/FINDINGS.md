# Chump-blocking: measured findings

Everything here comes from `CastleDefense.BotArena.exe stall-test` (source: `../StallTest.cs`).
Raw CSVs sit next to this file. Measured 2026-08-21. Three experiments: a single
attacker, then attacking forces with escorts, then the defender's anchor answer.

> **CORRECTION, applied 2026-08-21.** The harness stopped the defender buying once the
> *high-tier* units died. With an escort a lethal swarm is still on the field, so the defender
> stood idle while it walked in — no bot would play that way. The defence is now gated on ANY
> enemy being present. **Unescorted results are unaffected** (verified: 0 of 48 cells differ);
> **every escorted number below was re-measured** and the "the escort breaks the tactic"
> conclusion is withdrawn. See experiment 4.

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

**The escort is a TAX on the stall, not a counter to it.** Cheapest chump rate that holds all
8 teams, unescorted vs escorted:

| force | T5 | T6 | T7 | T8 |
|---|---|---|---|---|
| ×1 | 0.5 → 6/s (12×) | 1.5 → 10/s (7×) | 2 → 30/s (15×) | 0.25 → 30/s (120×) |
| ×3 | 1 → 6/s (6×) | 6 → 15/s (2×) | 15 → 30/s (2×) | 0.67 → 30/s (45×) |
| ×5 | 2 → 6/s (3×) | 10 → 15/s (2×) | 30 → 30/s (1×) | 2 → 30/s (15×) |

Median tax ~7×. An escorted tier-5 force is still held for 6 chumps/s and an escorted tier 6 for
10–15/s; only tier 7 in numbers genuinely needs the engine's ceiling.

**Two earlier claims are withdrawn.** The first draft said no rate up to 30/s holds an escorted
T5–T7 force; the second said 30/s holds it but nothing below. Both came from the idle-defender
bug. The escort raises the price by roughly an order of magnitude and does not break the tactic
except against tier 7 in numbers.

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

---

## Experiment 3 — the defender's answer: a T5 anchor woven into the chump wave

`anchor_isolated.csv` (3,840 runs), `anchor_tier.csv`, `anchor_gap_*.csv`. The defender keeps
its tier-1 chump stream and additionally spawns one **tier-5** unit every **5 seconds**
(`--anchors 5 --anchor-gap 150`). Anchors are charged normally, unlike the attacker's force,
and they stop with the chumps when the threat is gone.

### It helps, but it is a matchup tool, not a general upgrade

**Re-measured after the correction.** Cheapest total defender spend that holds all 8 teams
against an escorted force:

| force | T5 | T6 | T7 | T8 |
|---|---|---|---|---|
| ×1 | 7.5 vs 13.0 — chumps | 20.0 vs **17.3** — anchor | 60.0 vs **32.5** — anchor | 60.0 vs **32.5** — anchor |
| ×3 | 12.0 vs 12.7 — chumps | 30.0 vs **26.6** — anchor | 60.0 vs 72.7 — chumps | 60.0 vs **42.4** — anchor |
| ×5 | 12.0 vs 13.3 — chumps | 30.0 vs 33.3 — chumps | 60.0 vs 72.6 — chumps | 60.0 vs 72.4 — chumps |

(chumps-only $/s vs chumps+anchor $/s.) The anchor is the cheaper buy in **5 of 12** escorted
cells and pure chumps in the other 7. Where it wins it wins clearly — a single escorted tier 7
or tier 8 costs $60/s to hold with chumps and $32.5/s with an anchor. Where it loses it loses by
adding ~$12/s that bought nothing.

The original framing — "the anchor turns impossible into possible" — was an artefact of the
idle-defender bug and is withdrawn.

### Against an UNESCORTED force it is usually a waste of money

Cheapest total defender spend that holds all 8 teams:

| force | chumps alone | chumps + T5 anchor | verdict |
|---|---|---|---|
| T5 ×1 | 1/s = **$2.0/s** | anchor only = $14.7/s | chumps win |
| T6 ×3 | 3.75/s = **$7.5/s** | 1/s = $15.8/s | chumps win |
| T6 ×5 | 10/s = $20.0/s | 1/s = **$15.8/s** | anchor wins |
| T7 ×3 | 15/s = $30.0/s | 6/s = **$24.9/s** | anchor wins |
| T7 ×5 | 30/s = $60.0/s | 10/s = **$32.8/s** | anchor wins |
| T8 ×1 | 1/s = **$2.0/s** | anchor only = $12.3/s | chumps win |

The anchor's floor is ~$12–15/s, so it only pays where the chump-only requirement had already
gone extreme. Against tier 7 in numbers it roughly **halves** the bill.

### The anchor is also just a blocker, and that is half of why it works

A tier-5 every 5 seconds with **zero chumps** already holds a single tier 8 in 8/8 teams — 5s is
exactly the tier-8 swing period, so the anchor alone satisfies the experiment-1 threshold. It
likewise holds unescorted T5 (any size) and T6 ×1 on its own. So "anchor + chumps" is not purely
"killer + blockers"; the anchor is doing both jobs, and attribution between them is not clean.

### Anchor tier and cadence both matter, and 5s was not optimal

×3 force + T4 escort, teams held of 8:

| anchor | 3.75/s | 6/s | 10/s | 15/s | 30/s | total $/s at 30 chumps/s |
|---|---|---|---|---|---|---|
| T6 attacker — none | · | · | · | · | 5 | 54.9 |
| T4 anchor | · | · | · | 1 | 8 | 58.4 |
| T5 anchor | 1 | 3 | 6 | 5 | 8 | 61.9 |
| **T6 anchor** | **8** | 6 | 8 | 8 | 8 | 110.7 |
| T7 attacker — none | · | · | · | 1 | 7 | 59.3 |
| T5 anchor | · | · | · | 3 | 8 | 71.9 |
| T6 anchor | · | 3 | 4 | 6 | 8 | 113.2 |

Cadence is roughly linear in cost and monotone in effect (T5 anchor, T6 ×3 escorted): 2.0s holds
8/8 from 6 chumps/s; 5.0s needs 30/s; 20.0s never gets there.

**Cheapest total defence that holds all 8 teams**, searched over anchor tier × cadence × chump
rate:

| attacker | best configuration | total |
|---|---|---|
| T6 ×3 + escort | T5 anchor every **2.0s** + only **10** chumps/s | **$44.4/s** |
| T7 ×3 + escort | T4 anchor every 5.0s + 30 chumps/s | **$63.4/s** |

Against tier 6 a faster anchor buys a *cheaper* defence overall, because it lets the chump rate
drop from 30/s to 10/s. Against tier 7 nothing escapes needing the maximum chump rate.

### Caveats specific to this experiment

- Anchor tier was swept only at ×3 force against tiers 6 and 7; cadence only for the T5 anchor
  in that same cell. The optimum is likely to move with force size and attacker tier.
- 30 chumps/s is one spawn per tick — the engine ceiling — so any row that only holds at 30/s
  is not reachable by a human.
- The anchor still cannot beat an escorted tier-7 force below 15 chumps/s in any configuration
  tested. That hole is real and remains open.

---

## Experiment 4 — the survival law: what a dollar of defence actually buys

`curve_full.csv` (7,680 runs: 19 chump rates × tiers 5–8 × forces 1/3/5 × escort 0/T4 ×
anchor 0/T5). This is the experiment that makes the rest usable by a bot, because it drops the
binary "does it hold" question and measures **seconds survived against defensive spend rate**.

### The closed form

Each body in contact absorbs one enemy swing. With the enemy delivering **S** swings/sec and
bodies arriving at **r**/sec, castle-bound swings leak at (S − r), and the castle needs **K** of
them:

```
t(r) = T_walk + K / (S - r)          r < S
r    = S - K / (T - T_walk)          the rate needed to survive T seconds
```

Fitted on unescorted chumps-only runs via the linearisation `1/(t − T_walk) = S/K − r/K`:

- **median R² = 0.972** across 93 (team, tier, force) cells
- recovered **S within 4%** and **K within 6%** of the roster-derived values
- predicting survival directly: **median error −4.5%**, 85% of runs within 20%

**Out-of-sample, never fitted:** treating a T5-every-5s anchor as nothing but a body arriving at
0.2/s moves the prediction error from −17.5% to **−3.7%**. The law had never seen an anchor.

### What the bot reads off the board

- **S** = sum of attack rates of every enemy unit on the field. Directly observable, and it
  absorbs escorts and mixed forces with no special case.
- **K** = castle HP ÷ damage per swing (siege doubles; the one-shot floor makes a tier 8 exactly
  2 swings at 23,000 HP).
- **T_walk** = remaining distance ÷ move speed.

### Two consequences

**Returns accelerate, so half-measures are the worst buy.** dt/dr = K/(S−r)², which *grows* as r
approaches S. Spending a little is nearly worthless; spending enough to sit just under the
enemy's swing rate captures almost all the value. This argues for a **threshold policy, not a
proportional one** — match the swing rate or save the money.

**Chumps dominate anything bigger, per unit of blocking.** The law says a body's blocking
contribution is one absorbed swing whatever the body is, so only price matters:

| body | price | cost per unit of blocking |
|---|---|---|
| tier 1 | $2 | 1× |
| tier 4 | $20 | 10× |
| tier 5 | $61 | 30× |
| tier 6 | $331 | 166× |

This predicts experiment 3's result from first principles: buy a bigger body **only** for what
the blocking law does not capture — killing an escort that is eating the line.

### The practical headline

The useful range is a few dollars per second. Three tier-7s take an undefended castle in 20s;
**$3/s** of chumps stretches that to 43s, **$7.50/s** to 165s, and **$10/s** makes it never —
against a force that cost the attacker $4,578.

### Limits of the law

- It models **blocking only**. Chumps also out-damage low tiers, so against tier 5 the law is
  conservative — less defence is needed than it says.
- It predicts the survival time well but the **critical rate less well**: observed r_crit / S is
  0.11–0.14 for tier 5 (chumps kill it long before saturation) and 1.9–2.6 for tier 7 in numbers.
- S is time-varying when escorts accumulate; the law assumes it constant over the window.

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
  asked for. The `force 0` arm suggests tier and cadence matter a great deal, and experiment 3
  confirmed exactly that for the defender's side of the same knob.
- **No mixed defensive compositions beyond chumps + one anchor tier.** Two anchor tiers at once,
  or an anchor that changes tier as the attack develops, are untested.

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

# experiment 3 -- the defender anchor
stall-test --teams all --seat 1 --tiers 5,6,7,8 --forces 1,3,5 --escorts 0,4 \
    --anchors 0,5 --anchor-gap 150 --intervals 1,2,3,5,8,10,15,20,30 \
    --protect-attacker true --csv anchor_isolated.csv
stall-test --teams all --seat 1 --tiers 6,7 --forces 3 --escorts 4 --anchors 0,3,4,5,6 \
    --intervals 1,2,3,5,8 --csv anchor_tier.csv

# experiment 4 -- the survival curve behind the law
stall-test --teams all --seat 1 --tiers 5,6,7,8 --forces 1,3,5 --escorts 0,4 \
    --anchors 0,5 --anchor-gap 150 \
    --intervals 240,180,120,90,60,45,30,24,20,15,12,10,8,6,5,4,3,2,1 \
    --protect-attacker true --csv curve_full.csv
```

`gen_report.py` in this folder rebuilds the published HTML report straight from the CSVs, so
no figure in it is transcribed by hand.
