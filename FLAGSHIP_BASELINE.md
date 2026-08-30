# FLAGSHIP BASELINE — the bot as it stands on 2026-08-11

Frozen at Marc's request while pushing toward the acceptance-test goal. **This is the
configuration he judges "fun, challenging, and interactive", and it is the restore point.**
If a later change makes the bot stronger but worse to play against, this file is how the
current feel gets back — the goal statement of 2026-08-10 drops playability as a
*constraint*, it does not say the current setup is disposable.

Baseline commit: **9cf4b452**. Every line reference below is against that commit.

Nothing in this file is a recommendation. It is a record of what the shipped bot IS.

---

## 1. What the singleplayer / acceptance-test opponent actually is

`RolloutSearchBot` — a **flat one-ply** search over the real engine, with `HeuristicBot`
as its policy prior. It is not a learned model; no ONNX file is involved. Construction
site: `CastleDefenseGame2/Services/GameHostingService.cs:275` (`SetupSearchOpponent`),
which both `sp` and the new `accept` mode call.

### Explicitly passed at the construction site

| parameter | value | note |
|---|---|---|
| `side` | 2 | bot is always P2 |
| `decisionInterval` | 15 ticks | 500 ms of real time between decisions |
| `horizon` | 300 ticks | **SUPERSEDED 2026-08-19 → 1600; see section 5.** The "cliff" (250 → 30%, 200 → 1.5%) is real but is a FLOOR at rung 0 (270 ticks), not evidence against long horizons |
| `rolloutsPerAction` | 1 | 1 → 3 changed nothing at all; variance is not the bottleneck |
| `seed` | `Environment.TickCount ^ gameId.GetHashCode()` | per-game, so games differ but one game is reproducible |
| `usePrior` | true | HeuristicBot plays unless a candidate beats it by the margin |
| `overrideMargin` | **0.10** | the single most important tunable after horizon |
| `useMacro` | true | save-invest macro |
| `usePressMacro` | true | press-advantage (blitz) macro |
| `maxDecisionMs` | 250 | with async this bounds STALENESS, not freeze |
| `maxParallelism` | `ProcessorCount - 2` | 18 on Marc's box |
| `asyncDecisions` | **true** | thinks on a background thread; acts on slightly stale state |

### Inherited from constructor defaults — equally load-bearing

`GameHostingService` passes none of these, so `RolloutSearchBot`'s defaults apply
(`CastleDefense.Engine/Bot/RolloutSearchBot.cs:354`):

| parameter | effective value | how it resolves |
|---|---|---|
| `macroMargin` | **0.10** | `NaN → overrideMargin` (line 396) |
| `armageddonMargin` | **0.10** | `NaN → _macroMargin` (line 398) |
| `pressPeakMargin` | **0.10** | `NaN → _macroMargin` (line 404) |
| `pressOffPeakMargin` | **0.10** | `NaN → _macroMargin` (line 405) |
| `useArmageddonMacro` | true | |
| `pressWaveUnits` / `pressMinTier` | 6 / 6 | |
| `pressPeakMinInvest` / `MaxInvest` | 6 / 7 | |
| `pressWaveCommit` | false | |
| `saveCommitFraction` | 0.5 | only used by `RolloutPolicyKind.Saving`, which is off |
| `ownRolloutPolicy` / `oppRolloutPolicy` | Heuristic / Heuristic | |
| `macroRandomRate` | 0.0 | Stage 0 instrumentation, off |
| `deepMacroEval` | false | Stage 1b, off — measured p=0.58 and not shippable at 1629 ms |

> ### ⚠ THE PRESS-MACRO INVESTMENT WINDOW IS INERT IN THE SHIPPED BOT
>
> `MarginFor` (line 165) picks `_pressPeakMargin` inside investments 6–7 and
> `_pressOffPeakMargin` outside it. **Both are 0.10 here**, so the branch returns the
> same number either way and the window suppresses nothing. The long comment at
> lines 169–182 describes a mechanism that only exists when the margins are passed
> explicitly — which `search-test` also does not do by default (`pressPeak`/`pressOff`
> start at `NaN` in `SearchTest.Run`, so benchmark and live agree, both inert).
>
> `RolloutSearchOpponent`'s own signature advertises 0.02 / 0.30, but `SearchTest` always
> passes positional values over them, so those numbers have never been what ships.
>
> This is the **third** instance of the "a config knob that reads as set but changes
> nothing" bug class in this project — see the strategy-matrix notes, where both press
> margins were 0.0 and a gated run came back byte-identical to an ungated one. Recorded
> here rather than fixed: changing it changes the bot, and that is a measurement, not a
> tidy-up.

### Evaluator

Deployed `GameState.EvaluateBoard()` — a 6-feature logistic over `LogitWeight*`.
`UseLinearEval` and `UseRefitEval` are both **false** (nothing in the web game assigns
them). Money's share of the logistic vector is `2.96 / 19.23 = 0.154`, which is the
one-line check for "am I looking at the deployed evaluator".

### Prior

`new HeuristicBot(side)` with **default settings** — so HeuristicBot tuning
automatically propagates to the search bot with no update here. The Armageddon macro
additionally holds a persistent defence-only prior
(`HeuristicBotSettings { AttackGateMinInvestment = 99 }`).

---

## 2. Measured behaviour of this exact configuration

n=600 paired, seed 4242, no headstart, vs HeuristicBot:

| quantity | value |
|---|---|
| win rate vs HeuristicBot | **74.8%** [71.2, 78.1] |
| overrides the prior on | 8.2% of decisions |
| save-macro chosen | 4.5% of decisions |
| press-macro chosen | ~0.1–0.3% |
| arma-macro chosen | 0.5% |
| earned invests (own / opp) | 6.93 / 6.12 |
| units bought / game | 224.8 |
| ms per decision | ~7 typical (benchmark, 1 core/game ≈ 37 ms) |

**Roughly 92% of the bot's moves are HeuristicBot's.** That is not a defect of the
configuration — it is what every lever tested so far says is optimal. The project-wide
invariant is *intervening rarely is good, intervening often is bad, whichever kind of
move it intervenes with*. Every degraded evaluator, in either direction, shows up as an
override rate of 12–13% instead of 8%.

**This is also the likely source of the "fun" property.** A bot that plays HeuristicBot's
readable, streaming game and corrects it 8% of the time reads as a human-paced opponent.
The two configurations known to be stronger-or-equal but worse to play against both work
by making the bot go quiet and bank: `--arma-margin 0.0` (fires 7.3%, much of the game in
defence-only banking) and macro-every-decision (buys literally zero units, loses 600/600).
If a future change costs the feel, look first at how much of the game the bot spends not
buying anything.

---

## 3. Against Marc

From `CastleDefenseGame2/recordings/game_records.db`, excluding the 11 quarantined
abandoned rerolls:

| matchup | record | rate | Elo gap |
|---|---|---|---|
| Marc vs HeuristicBot | 58W / 5L | 92.1% | +426 |
| Marc vs SearchBot | 43W / 8L | 84.3% | **+292** |
| SearchBot vs HeuristicBot | — | 74.8% | +191 |

**Correction to CLAUDE.md, which records +241 for the middle row.** `400·log₁₀(43/8)` is
+292; +241 corresponds to 80.0%, not 84.3%. The transitivity claim degrades with it:
424 vs 292+191 = 483 is a **59 Elo** discrepancy, not 8. Still consistent enough to call
one strength axis, but the ladder is not as tight as recorded.

---

## 4. The acceptance test (added 2026-08-11)

Main-menu **Acceptance Test** button (formerly Training League) → `gameMode: "accept"` →
`GameHub.JoinGame`'s `accept` branch → `SetupSearchOpponent`, i.e. **this exact config**.

- Server assigns random team + random offensive/defensive gadget to **both** sides;
  signature gadget follows team. Neither player chooses.
- Human is P1, no headstart (`timeSkip` 0), no selection screens, nothing to reroll.
- Recorded to `recordings/singleplayer/` with `game_mode="accept"`,
  `opponent_type="search"` — separable from the 51 existing `sp` games against the same
  bot, and automatically included as human games by `ReplayFile.IsHumanPlayed`, so
  `--divergence` and `--export-policy-table` pick them up with no change.

**Pass condition: Marc wins 0 or 1 of 10.**

Sizing that bar honestly, since it is stricter than "large majority":

| Marc's true win rate | P(he wins ≤ 1 of 10) |
|---|---|
| 20% | 37.6% |
| 15% | 54.4% |
| 10% | 73.6% |
| 5% | 91.4% |

A bot that genuinely wins 80% passes this test **only 38% of the time**. Reliable passage
needs Marc at ~10%, i.e. the bot ~+382 Elo above him. From today's −292 that is a swing of
roughly **674 Elo**; the entire search programme to date bought +191. Worth knowing before
reading a single 10-game run as a verdict in either direction.

Training League's watch mode (`SetupTrainingLeagueWatchMatch`, v4 vs HeuristicBot) is
still present server-side and still reachable by posting `gameMode: "league"`; it just no
longer has a menu entry.

---

# 5. RESTORE POINT — the config as it stood before the 2026-08-19 horizon change

**Everything above this line describes the bot as shipped up to 2026-08-19.** On that date
`horizon` changed from **300 to 1600**. This section is the revert instruction and the
evidence that prompted the change.

## To revert

One file, one line. `CastleDefenseGame2/Services/GameHostingService.cs`, in
`SetupSearchOpponent`: set `horizon: 300`. Nothing else changed — interval stays 15,
margin stays 0.10, every macro setting is untouched. `GameHostingService.cs` was clean at
commit `e2d91e77` when the change was made, so `git diff e2d91e77 -- CastleDefenseGame2/Services/GameHostingService.cs`
shows exactly the departure and nothing else.

The pre-change measured behaviour to restore to is section 2 above: **74.8–76.0% vs
HeuristicBot, 7.8–8.2% overrides, 4.4–4.5% save-macro, ~6.9 earned invests.**

## Why it changed: THE 300-TICK HORIZON WAS NEVER A MEASURED OPTIMUM

The tuning record at the construction site claimed `horizon 900 -> 300` moved the win rate
`47.0% -> 68.5%` and called it "the single largest factor". **That does not reproduce.**

Re-measured 2026-08-19, `search-test`, seed 4242, paired setups, interval 15, **margin 0.10
(the shipped margin)**, no time cap, n=200 per arm:

| horizon | win rate | paired Δ vs 300 | b/c | McNemar p | best−worst gap | flat | overrides |
|---|---|---|---|---|---|---|---|
| 300 | 77.0% | — | — | — | 0.203 | 3.5% | 8.1% |
| 450 | 80.5% | +3.5 | 23/16 | 0.34 | 0.251 | 4.4% | 10.3% |
| 600 | 81.5% | +4.5 | 25/16 | 0.21 | 0.273 | 4.8% | 12.0% |
| **900** | **78.0%** | +1.0 | 20/18 | 0.87 | 0.298 | 7.1% | 10.1% |
| 1200 | 77.5% | +0.5 | 13/12 | 1.00 | 0.341 | 8.2% | 11.0% |

**Horizon 900 measures 78.0% [71.8, 83.2], not 47.0%.** The interval excludes the recorded
figure by a wide margin. There is no cliff above 300 and no penalty for long rollouts.

Confirmed at n=800 paired, 300 vs 600:

| | horizon 300 | horizon 600 |
|---|---|---|
| win rate | 76.0% [72.9, 78.8] | **80.0% [77.1, 82.6]** |
| overrides | 7.8% | 11.1% |
| decided on HP at tick cap | 10 of 608 | 5 of 640 |
| ms/decision (1 core) | 38.0 | 72.8 |

**Paired delta +4.00, b=94/c=62, McNemar exact p = 0.0128, 95% CI [+0.95, +7.05].**
Control reproduces the recorded shipped figure (76.0% vs 74.8/75.0% on record), and the
discordant fraction was stable with n (20.5% → 19.5%), so the earlier n=200 estimate was
under-powered, not wrong.

## The confound: horizon was tuned BEFORE margin, and never retuned

The 2026-08-05 sweep was coordinate descent. Horizon was chosen at margin ~0.03, then
margin was raised to 0.10, and horizon was never revisited. Margin 0.03 is the
high-intervention regime — re-measured in this build, horizon 300 at margin 0.03 scores
73.0% with **17.1% overrides**, against margin 0.10's 7.8%. A longer horizon produces a
wider score spread, which does maximum damage where search overrides constantly and is
filtered out where it overrides rarely. The horizon × margin interaction was never measured.

## ATTRIBUTION CONFIRMED: it is the margin, and the interaction is large

The original comparison was re-run **in this build at margin 0.03**, n=200 paired, seed 4242.
The full 2x2:

| | horizon 300 | horizon 900 | paired Δ | p |
|---|---|---|---|---|
| **margin 0.03** | 73.0% | **59.5%** | **−13.50** | **0.00036** |
| **margin 0.10** | 77.0% | 78.0% | +1.00 | 0.871 |
| paired Δ (0.03→0.10) | +4.00 | **+18.50** | | |
| p | 0.200 | **<0.00001** | | |

**The horizon penalty is real at margin 0.03 and absent at margin 0.10.** The original
sweep was not wrong about what it measured — it was wrong to treat horizon as separable
from margin. Interaction ≈ **14.5 points**.

Equivalently, and this is the part that matters going forward: **the value of the margin
grows with the horizon.** Raising margin 0.03 → 0.10 is worth +4.0 at horizon 300 and
+18.5 at horizon 900. Margin is denominated in the evaluator's own output units and the
score spread grows with horizon (0.203 at 300 → 0.341 at 1200), so a margin tuned at one
horizon does not transfer to another. **Margin 0.10 at horizon 1600 is an untested
assumption, not a carried-over result**, and is likely the largest single open variable in
the shipped config.

(The recorded penalty was −21.5 points, 47.0% vs 68.5%; this build reproduces −13.5. Same
sign, same order of magnitude, smaller — the build has since gained the Armageddon macro
and other changes, so an exact reproduction was not expected.)

## The stated mechanism is FALSIFIED

The comment claimed long rollouts "converge to the same self-play continuation and the
search is scoring noise". Convergence predicts the best−worst spread collapses toward zero.
**It grows monotonically, 0.203 → 0.341.** What actually happens is a bifurcation: flat
decisions also rise (3.5% → 8.2%) because more rollouts reach a terminal state and tie
exactly. More ties AND wider spreads at once — bimodal, not convergent. That is presumably
why gains stop above ~600, but it does not cost 31 points.

## The override-rate invariant has a counterexample

Horizon 600 runs at 11.1% overrides — the band this document associates with degraded
configurations — and wins by 4.0 points at p=0.013. **The invariant "assume anything raising
the override rate is harmful" appears to describe bad evaluators specifically, not
intervention rate as such.** Section 2's framing should be read with that caveat.

## Why 1600 and not 600, which measured best

Marc's call, made explicitly on principle rather than on the measured argmax, to avoid
optimising into a local optimum that no longer has a mechanism behind it.

Time to afford the next investment from an empty wallet, in ticks — **note the hand-tuned
top rung**, which the general `4·count + 8` seconds formula does not cover
(`PlayerState.ApplyInvestmentStep`, and the constructor's price 18 / income 2 for count 0):

| count | 0 | 1 | 2 | 3 | 4 | 5 | 6 | **7** |
|---|---|---|---|---|---|---|---|---|
| ticks | **270** | 360 | 480 | 600 | 720 | 840 | 960 | **1600** |

The documented cliff (250 → 30%, 200 → 1.5%) sits exactly below rung 0 at 270 ticks, so
the shipped 300 was the smallest number that buys the FIRST investment and nothing more.
1600 is the smallest horizon that can see the LAST one. 1200 — the original intent — covers
only rungs 0–6.

**1600 IS UNMEASURED AT THE TIME OF THE CHANGE.** The sweep topped out at 1200, where the
paired delta was +0.5 (p=1.00), i.e. indistinguishable from 300. The expected cost relative
to horizon 600 is roughly the 4 points 600 was beating 300 by. This is a deliberate trade of
measured win rate for a mechanism that can be reasoned about, taken as the base for
evaluator work — not a claim that 1600 is stronger.

## Playability warning

Section 2 argues the "fun" property comes from ~92% of moves being HeuristicBot's. Longer
horizons reduce that: overrides 7.8% → 11.1% at 600, and press-macro firing quadruples
(0.4% → 1.5%), i.e. a bot that banks and then hits harder. That may be better or worse to
play against; win rate vs HeuristicBot says nothing about it. **If the feel degrades, this
section is the way back.**

## Caveat on all of the above

Every number here is measured against HeuristicBot, which is also the rollout policy driving
both sides inside the search. The self-referential caveat in CLAUDE.md applies at full
strength. None of this is evidence about play against Marc.
