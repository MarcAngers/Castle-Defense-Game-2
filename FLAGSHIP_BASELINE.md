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
| `horizon` | 300 ticks | rollout length. **A cliff, not a curve** — 250 → 30%, 200 → 1.5% |
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
