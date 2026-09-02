# BOT_BACKLOG.md

Ranked hypothesis queue for `HeuristicBot`. Seeded 2026-09-01 from a full engine read
(see `BOT_MECHANICS.md`).

**Every entry must carry a MECHANISM and a PREDICTED SIGNATURE before it is run.** The
signature says which measurements should move *and which must not*. Win rate falsifies a
hypothesis; it never selects one. A change that wins with no stated mechanism is rejected,
because that is precisely how `RolloutSearchBot` came to be +292 Elo against HeuristicBot
while feeling *worse* to play against — it was fitted to an opponent model rather than to
the game. The template to imitate is the incoming-nuke fix: *"every other rung came back
byte-identical; the effect appeared exactly where the mechanism predicted and nowhere
else."*

Conventions:
- Every change ships behind a new `HeuristicBotSettings` flag defaulting to `false`, so the
  base arm stays reproducible and one run yields a paired comparison.
- `HeuristicBot` calls `engine.Invest / Repair / SpawnUnit / UseGadget` **directly**, not
  through `ApplyAction`. So it can also call `engine.UpgradeAutoSpawn` directly —
  **no mask change, no observation-vector change, no ONNX invalidation.**
- Results go to `BOT_ITERATION_LOG.md`, one entry per run, win or lose.

---

## 1. Charge-aware purchase fallback — `ChargeAwareFallback`

**Status:** queued (first)

**Mechanism.** Every filter in `SpendOnUnits`' pick pipeline (`RankPool`, `outclassPick`,
`matchedPick`, `anyAffordable`, `PowerPickAffordable`, `TechEscalation`) tests
`def.Cost <= spendable` and nothing else. It then makes **one** attempt:
`Act(() => engine.SpawnUnit(_side, pick.def.Id))`, with no fallback. Since 2026-09-01
`SpawnUnit` refuses without a charge. The bot converges on one unit id, drains 5 charges in
under a second at 6 decisions/s, and then re-picks the same uncharged id for most of the
rest of the game while a fully-charged second choice sits unused.

**Evidence.** `bot-checksum --games 4`: 375 / 241 / 472 / 204 units over 312 / 236 / 335 /
217 s = **0.94–1.41 units/s, mean 1.14**, against a 6 decisions/s cadence. That is exactly
`UnitChargeRegenMs = 1000`, i.e. the refill rate of a *single* unit id. End-of-game idle
money: $42,765 / $39,199 / $48,608 / $54,854. `CLAUDE.md` independently records purchases
falling 101.9 → 35.4 per 1000 ticks (−67 %) when charges shipped.

**Change.** Rank as now, then take the best-scoring pick that is both affordable *and* has
a charge. Prefer a genuine fallthrough over a pre-filter so the existing scoring, the
outclass rule and TechEscalation are untouched when the top pick is available.

**Predicted signature.**
- units/s rises from ~1.14 toward 3+ — *must move*
- end-of-game idle money falls substantially — *must move*
- earned investments roughly unchanged — **must NOT move much**; if invests shift a lot the
  change is doing something other than what is claimed
- win rate up against tier-spam rungs, which the bot out-produces
- `--unit-charge-check` still passes

**Risk.** Buying more units means spending more money, which is the exact failure mode the
four rejected `SpendOnUnits` experiments produced. The allowance/budget machinery should
contain it because the *spend* caps are unchanged — only the *choice* is. Watch invests.

---

## 2. Buy the auto-spawner — `AutoSpawnerLadder`

**Status:** queued

**Mechanism.** No bot in the project can buy it. Action 14 is absent from `GetActionMask`,
and `RolloutSearchBot` builds candidates from `a = 8..1` plus `{9,10,11,12,13}`. But
`HeuristicBot` bypasses the action space entirely, so `engine.UpgradeAutoSpawn(_side)` is
directly callable with nothing else invalidated.

It buys **bodies per second**, which after the charges change is a separate resource that
money alone can no longer buy — auto-spawner units are `ignoreCost` and therefore consume
no charges and are not subject to the one-purchase-per-decision pacing.

**Value.** Level 1 costs **$102** for 1 free body/s forever. The survival law says one body
per enemy *swing* holds anything, and every tier-8 clamps at 0.20 swings/s — so level 1 is
5x the rate needed to neutralise a lone tier 8, permanently, for a one-time $102. Level 5
is $860 cumulative for 3 bodies/s.

**Change.** A rung-buying rule in the same slot as invest: buy the next auto-spawner level
when it is cheap relative to the next investment rung and the runway is safe. Start
deliberately conservative — cap at a low level (say ≤ 5, $860 cumulative) so this tests
"cheap early bodies" rather than "spend the whole economy on the machine".

**Predicted signature.**
- units on field rises without unit *purchases* rising (they are `ignoreCost`)
- `MoneySpentOnUnits` roughly unchanged — **must NOT move**; if it moves, the rule is
  displacing ordinary spending rather than adding free bodies
- strongest effect against tier-spam rungs and in long games
- earned invests may fall slightly; if they fall a lot, the cap is too high

**Risk.** Competes with invest for the same early dollars, which is where every previous
"spend earlier" experiment died. The level cap is the control.

---

## 3. Block a lone chipping unit — `BlockSingleChipper`

**Status:** queued (Marc's own finding, 2026-09-01)

**Mechanism.** A single enemy unit parked on the castle is refused by all four defence
paths simultaneously:
- TTD against one unit is 100+ s → `investmentRunwayIsSafe` true → `inDanger` false →
  `SpendOnUnits(preferDefense: true)` is never called
- even if it were: `runwayDeficitSeconds = max(0, timeToInvest − timeToDeath) = 0` →
  `reactiveSpendBudget = $0` → `spendable = 0` → nothing affordable → return
- `waveWipeOpportunity` requires `WaveWipeMinUnits = 3`
- `survivalEmergency` requires TTD ≤ 12 s

This is a band-aid that outlived its purpose: `reactiveSpendBudget` was added after playtest
D3596E, where the bot spent hundreds of dollars on a barely-threatening single unit. The cap
was correct; its *price basis* is not. It prices the threat against **time-to-death** when
the right comparison is a $1–4 chump against the **cumulative HP** the unit will remove —
and since only `Repair` heals a castle, that damage is permanent. Same rate-vs-stock error
as the incoming-nuke blind spot, inverted.

Marc reports `RolloutSearchBot` exploiting exactly this, which is what makes it live rather
than theoretical.

**Change.** An absolute floor independent of the EV budget: if an enemy unit is in contact
with our castle and the cheapest roster unit is affordable, buy one. Rate-limit it to the
survival law's requirement (one body per enemy swing period) so it cannot degenerate into
the permanent-reactive-spend pathology that this file's history documents repeatedly.

**Predicted signature.**
- castle HP at end of game rises — *must move*
- damage taken from small forces falls
- money spent on units rises only slightly (chumps cost $1–4)
- earned invests **must NOT fall** — if they do, the rate limit is too loose
- effect concentrated in *long* games and against low-tier spam; near-zero against tier 7/8
  rungs, where a single unit is never the threat

---

## 4. Farm cheap gadget upgrades — `GadgetXpFarming`

**Status:** queued

**Mechanism.** `AddGadgetXp` grants a flat **100 XP per cast**, for every gadget,
regardless of effect, damage dealt or value destroyed. Upgrades trigger at
`UpgradeCost` (300–1500), i.e. **3–15 casts**. So upgrades are bought with casts, and the
cheapest gadgets buy the steepest upgrades:

- `wall` → `wall_2`: 3 casts × $50 = **$150 turns a 400 HP wall into a 6,000 HP one**
- `reinforcements` → `_2` → `_3`: 15 casts total, ending at **5 free tier-7 units per cast
  on a 10 s cooldown, charge-free**

`GadgetUpgradeSpam` already exists but defaults to **false**, and when on it is gated behind
`!inDanger`, an income test (`Income >= Cost / (cooldown × k)`, k = 0.30), an
investment-commit deferral and a stagger — so it only farms when already rich, which is
after the upgrades have stopped mattering.

**Change.** Allow farming *early* specifically for gadgets whose next upgrade is cheap in
absolute dollars (casts × cost), rather than gating on income. Keep every self-harm guard —
the first version of this path lost 73 % of games to a do-nothing opponent by farming XP
with the nuke, which damages both castles.

**Predicted signature.**
- gadget tier reached by mid-game rises — *must move*
- early money spent on gadgets rises
- earned invests fall slightly (this is a real trade)
- **must NOT** regress against the do-nothing / Investor rungs — that is the self-harm canary

---

## 5. Map awareness — `MapAwarePlay`

**Status:** queued (lowest confidence, listed for completeness)

**Mechanism.** `HeuristicBot` reads `GameState` directly and could be map-aware with no
retraining, but nothing in `CastleDefense.Engine/Bot/` references `MapEffects`, `state.Map`
or `ShadowMap`. Every map is played identically. The two effects with real decision content:

- **Red** heals every unit 10–50 % every 10–30 s, which invalidates the chump-block and
  wave-wipe arithmetic outright — a wiper that "one-shots the toughest" may not, after a pulse
- **Blue** cuts fire damage 25 %, which mis-prices firebomb and meteor; **Orange** raises it 10 %

**Change.** Narrow and testable: fold the map's fire multiplier into the firebomb/meteor
value estimate, and require the wiper's one-shot test to clear the toughest unit's *max*
health on Red rather than its current health.

**Predicted signature.**
- effect appears **only** on the relevant maps — Blue/Orange for the fire change, Red for
  the wiper change — and every other map must come back **byte-identical**. That
  one-sidedness is the entire acceptance test.

---

## 6. `Act()` reports success for a queued action — `ActQueueHonesty`

**Status:** queued (correctness, not strength)

**Mechanism.** `Act` returns `true` when it merely enqueues, so callers that check the
return value record a purchase that has not happened and may still fail. `boughtWiper`,
`_lastWiperTick`, `LastUnitsPurchased`, `ActionCounts[]` and the allowance debits are all
set on that false positive.

**Predicted signature.** Diagnostic counters change; **behaviour should not**. If win rate
moves at all, something is depending on the bug and that dependency needs finding before
this lands.

---

## Rejected / do not retry without a new angle

Carried forward from `CLAUDE.md` and the in-file history so no session burns a run on them:

- **Lowering `AttackGateMinInvestment` / earlier army pivot.** Three independently designed
  variants, all net-negative. A fourth needs the pivot to *persist* for the rest of the game
  (variant 2) while carrying variant 3's tier escalation and opponent-read gate — untried.
- **Snipe tuning.** 2 for 2 rejected.
- **`SpendOnUnits` reactive constants.** 4 confirmed dead ends.
- **Distilling search into the rollout policy.** Probe A: search is insensitive to rollout
  strength (non-monotone; override rate pinned at 8.2 %). The headroom is the option space.
- **Proactive early repair.** Net loss every ordering tried.
