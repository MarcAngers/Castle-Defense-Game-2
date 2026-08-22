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
before any of them lands. At `cd 0.0s` the tally claims to have destroyed **1.28× everything the
opponent ever bought**, which is impossible. The figures are still valid as a BEFORE/AFTER on the
selection rule, since both sides are measured the same way — they are not valid as economics.

The honest measurement counts each distinct enemy unit once and asks whether it is still alive:

| cooldown | their army | we destroyed | our spend | real ratio | wins |
|---|---|---|---|---|---|
| 1.0s | $42,823 | $29,479 (69%) | $7,925 | **3.72×** | 53.8% |
| 8.0s | $34,888 | $23,354 (67%) | $2,604 | **8.97×** | 47.5% |

So the defence as a whole is trading extremely well, and **the better trade ratio loses more
games**. Spending more to kill more wins, even at worse efficiency — efficiency is not the
objective, and optimising it directly would have taken the bot the wrong way.

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
- **The bot gains no economic advantage.** In the pinned mirror both players finish on income
  $750/s having earned exactly 7 investments. The premise — defend cheaply, invest faster, reach
  Armageddon first — is not happening. See `mirror_anatomy.html`.

## Reproducing

```
CastleDefense.BotArena.exe bot-checksum --games 160 --p1-defence-only
CastleDefense.BotArena.exe bot-checksum --games 160 --p1-defence-only --wiper-cd 8.0
CastleDefense.BotArena.exe bot-checksum --games 24                      # flag-off guard
CastleDefense.BotArena.exe bot-checksum --games 1 --p1-defence-only \
    --loadout White,nuke,reinforcements --dump mirror_dump_new.csv
```
