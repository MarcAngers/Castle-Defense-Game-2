# BOT_MECHANICS.md

**What this file is for.** Deriving this map cost ~3,000 lines of engine reading. It is
written down so that no future session has to pay that again. Read this instead of
re-reading the engine; go to the source only to confirm a specific line before changing it.

Companion files: `BOT_BACKLOG.md` (what to try next, and why), `BOT_ITERATION_LOG.md`
(what has been tried and what happened). `CLAUDE.md` remains the authority on
measurement discipline and project history — this file does not repeat it.

Derived 2026-09-01 against branch `bot-iteration-loop`. Every claim below was read out of
the source, not inferred from documentation. Where a fact contradicts an older document,
this file is the newer reading, but **verify before acting on it** — that is the whole
lesson of `CLEANUP_BACKLOG.md`.

---

## 1. The board

| constant | value | where |
|---|---|---|
| `MAP_WIDTH` | 2000 | `GameEngine` |
| P1 castle wall | x = 200 | `P1_CASTLE_WALL` |
| P2 castle wall | x = 1800 | `P2_CASTLE_WALL` |
| tick rate | 30 Hz | `TICKS_PER_SECOND` |
| game length cap | 18,000 ticks = 600 s | `MAX_TICKS` |
| income cadence | every 30 ticks (1 s) | `INCOME_FREQUENCY` |

`Unit.Position` is the sprite's **LEFT edge**, not its centre. A side-1 unit therefore
leads with `Position + Width`; a side-2 unit leads with `Position` alone. Getting this
backwards is a real historical bug (P2 units used to open fire a full unit-width early) —
see `GetDistanceToEnemyCastle`'s doc comment.

Both sides' leading edges start 1700 px from the enemy wall. P1 spawns at `100 - Width`,
P2 at `1900`, which is what makes that symmetric.

---

## 2. Tick order (`GameEngine.Tick`)

1. `ProcessActions` — drains the queued input actions
2. Scheduled `PendingEffect`s fire (iterated **downward**, so anything they schedule runs on a *later* tick)
3. `CurrentTick++`
4. `SpawnOpeningSquad`
5. `TickAutoSpawn`
6. Income, if `CurrentTick % 30 == 0`
7. `TickCooldowns` (both players) — unit charge regen and gadget cooldowns
8. `ProcessHazards`
9. `ProcessMapHealPulse`
10. `ProcessStatuses` — **only if `CurrentTick % 5 == 0`**, so all DoT ticks at 6 Hz
11. `MoveAndFight`
12. Time-limit check

**`MoveAndFight` is fully deferred.** Damage, castle damage and movement are all collected
into lists and applied *after* the loop over units. This is the root-cause fix for
order-dependent seat bias: no unit's action this tick can be affected by another unit's
action in the same tick. Anything added to this loop must preserve that.

---

## 3. Combat — the single most important rule

```
if (enemies in range)        -> CurrentSpeed = 0, attack EVERY enemy in range
else if (castle in range)    -> CurrentSpeed = 0, attack the castle
else                         -> move
```

**The castle is only ever attacked in the `else if`.** One enemy body in contact means
*zero* castle damage that tick. This is the mechanism behind the whole chump-blocking
result in `CastleDefense.BotArena/stall/FINDINGS.md`: blocking is a hard stop, not a
damage race.

A swing hits **all** targets in range simultaneously — `FindTargetsFast` returns a list and
every member takes full damage. Melee cleave is the default, not a special case. That is
why a cheap swarm can be deleted by one swing, and why `SurvivabilityMultiplier` exists.

### Targeting (`FindTargetsFast`)

Edge-to-edge distance against `attackerDef.Range`. Scans forward and backward from the
attacker's index in the position-sorted list. The forward bound is exact; the backward
bound must pad by `_maxUnitWidth` because widths are not sorted.

**`Range` is 0 for every unit in the game.** `master_roster.csv` has no `Range` column, so
`GameDataManager` falls back to 0. Combat is pure contact.

**The Flying/Ranged exclusion is unreachable dead code.** No roster row defines
`ArmorType`, so it falls back to `isAce ? Shield : None` and nothing is ever `Flying`.
Do not build a counter-play around air units; there are none.

### Stats are DERIVED, not read

`master_roster.csv` columns are: `Team, Tier, ID, Name, Price, Health, Damage,
AttackSpeed, DPS, Speed, Width, Height, Description`. There is **no** `Range`,
`AttackType`, `ArmorType`, `Weight` or `Shield` column. Consequences:

| field | actual value |
|---|---|
| `Range` | 0, always |
| `Weight` | falls back to `Health` |
| `isAce` | `Tier == 8` |
| `AttackType` | `Siege` for tier 8, `Melee` for everything else |
| `ArmorType` | `Shield` for tier 8, `None` for everything else |
| `MaxShield` | 0 for everyone (only `monky` gets one, hardcoded) |
| `PushForce` | `Weight * Speed * Damage` |
| `EffectiveWeight` | `Tier² * Weight / Speed` |

**`AttackSpeed` is recomputed and the CSV column is ignored:**

```
tier < 6   ->  tier   * 2 * Speed / Damage
tier < 7   ->  tier^2     * Speed / Damage
tier >= 7  ->  tier^3     * Speed / Damage
clamped to [0.33, 5.0]      # MIN/MAX_ATTACK_SPEED in GameDataManager
```

This clamp is load-bearing for the survival law. **Every tier-8 unit clamps at the BOTTOM;
tier-4/5 units clamp at the TOP (5.0/s).** That is why a tier-4 stream razes an undefended
castle far faster per dollar than a tier 8, and why a steady chump feed holds any tier 8
indefinitely.

**The floor was raised 0.20 -> 0.33 on 2026-09-03** (one swing per 3.03 s, was per 5.00 s).
It is a floor only the eight aces ever touch — every other unit's raw value is already above
0.33 — so it is a **65% DPS buff to every tier 8 and a no-op everywhere else**. It also moves
the chump-blocking arithmetic: the holding threshold goes 5.00 s -> ~3.03 s and the spread
between the two clamps narrows from 25x to 15x. Numbers measured before that date are stale
for tier 8; see the note in CLAUDE.md's chump-blocking section.

### Siege (tier 8 only)

- **double damage to the castle** (`castleDamage *= 2`)
- **double damage to shields** (in `ApplyDamage`)
- **double push force**

### Knockback

`knockbackDist = impactForce / max(1, EffectiveWeight)`, capped at 3000, then:

- `AttacksWithoutKnockback >= 50` and tier < 8 → 25 px
- `AttacksWithoutKnockback >= 250` and tier == 8 → 10 px
- tier 8 is always capped at 10 px
- only applied at all if `> 10 px`; otherwise the counter increments
- re-knockback immunity: 2 s (`LastKnockbackTick`), deliberately **not** scaled by map
- walls are immovable — the impulse is discarded, not banked

Applied in `MoveAndFight` step 6, **after** the anti-stunlock clamps, which is where the
map's knockback multiplier lands.

### `ClampToContact`

Trims a move so a unit stops exactly at the wall or at the nearest enemy, instead of
overshooting by a fraction of its stride. Without it a mixed-tier pileup settled into
several ranks a few pixels apart and a swing hit only the front rank ("phantom hits").

**It skips FRIENDLY units.** A force does not queue behind its own front member — the
whole force overlaps at one contact point and all of it swings. This is why force size
costs a defender superlinearly (×1 → 1.0 chumps, ×5 → 15.0).

---

## 4. Castle

Starts 2000/2000. `DamageCastle` order:

1. `IsInvulnerable` → return
2. `CastleShield` absorbs first, bleeds remainder through; fully-absorbed hits return early
3. **One-shot prevention**: at *exactly* full health, `damage >= CastleMaxHealth` floors at 1 HP
4. Subtract; ≤ 0 ends the game

Double-KO: if the game is already over and *this* hit is against the side about to be
recorded as winner, it becomes a draw (`WinnerSide = 0`). Overkill on the already-dead
loser is the common case and must not be mistaken for it.

**Only `Repair` heals a castle.** The heal and goo gadgets are units-only. This makes
castle damage effectively permanent, which is the reason ignoring a lone chipping unit is
expensive (see `BOT_BACKLOG.md` item 3).

---

## 5. Economy

One curve prices everything:

```
EconomyCurve(x) = e^(0.0109x^3 + 0.0011x^2 + 0.4351x + 0.5268)
```

All prices go through `WholeDollars` (ceiling).

### Invest — 8 rungs then ARMAGEDDON

`Income = curve(n)`, `InvestmentPrice = ceil(Income * (n*4 + 8))`, with hand overrides at
n=7 and n=8.

| n | income after | price of NEXT rung |
|---|---|---|
| start | 2.00 | 18 |
| 1 | 2.65 | 32 |
| 2 | 4.43 | 71 |
| 3 | 8.47 | 170 |
| 4 | 19.74 | 474 |
| 5 | 59.88 | 1,677 |
| 6 | 252.50 | 8,080 |
| 7 | 750.00 | 40,000 |
| 8 | 2,500.00 | 121,221 (= ARMAGEDDON) |

**The structurally important fact:** because `price = Income * (4n + 8)`, the time to
afford the next rung from an empty wallet is exactly **(4n + 8) seconds regardless of
income** — 8 s, then 12 s, 16 s, 20 s... This is why a search horizon shorter than that
can never see the payoff of saving, and why `RolloutSearchBot` needed horizon 1600.

At `InvestmentCount == 8` the invest button becomes ARMAGEDDON: one-time, and it does
**not** run `ApplyInvestmentStep`, so income and price stay put.

### Repair

`RepairPrice = ceil(curve(n) * (n*5 + 5))`, doubled again at n ≥ 8.
`nextMax = 1000 + 11000 * (n+1)`, and it heals 20 % of max on top.

Schedule: **20, 26, 66, 169, 493, 1796, 8837, 126390**

Super-exponential price for a flat +11,000 HP. Repair 7 is ~443x worse value per HP than
repair 1, and repair 8 costs more than ARMAGEDDON.

### Auto-spawner — 19 rungs, and the bot cannot buy it

Same curve at a different scale: `x = 2.5 + 0.25*(L-1)`, multiplier `(x*4 + 7)`.
Levels 17/18/19 are hardcoded (43,614 / 100,000 / 100,000).

| L | price | cumulative | free units/s | tier cycle |
|---|---|---|---|---|
| 1 | 102 | 102 | 1 | [1] |
| 2 | 128 | 230 | 2 | [1,1] |
| 3 | 161 | 391 | 2 | [2,1] |
| 4 | 205 | 596 | 2 | [2,2] |
| 5 | 264 | 860 | 3 | [3,2,1] |
| 6 | 344 | 1,204 | 3 | [3,2,2] |
| 7 | 454 | 1,658 | 3 | [3,3,2] |
| 8 | 609 | 2,267 | 4 | [4,2,1,1] |
| 9 | 829 | 3,096 | 4 | [4,3,2,1] |
| 10 | 1,147 | 4,243 | 4 | [4,4,1,1] |
| 11 | 1,617 | 5,860 | 4 | [4,4,3,2] |
| 12 | 2,324 | 8,184 | 5 | [4,4,3,3,2] |
| 13 | 3,409 | 11,593 | 5 | [4,4,4,3,2] |
| 14 | 5,108 | 16,701 | 5 | [5,3,3,2,1] |
| 15 | 7,828 | 24,529 | 5 | [5,4,3,2,2] |
| 16 | 12,284 | 36,813 | 6 | [5,4,4,2,2,2] |
| 17 | 43,614 | 80,427 | 6 | [6,5,4,3,2,1] |
| 18 | 100,000 | 180,427 | 6 | [7,6,6,5,5,4] |
| 19 | 100,000 | 280,427 | 6 | [8,7,7,6,6,5] |

**Units per second IS the cycle length** — deliberately not a separate column.
The accumulator is an integer counting ticks, never a float.

**Action 14 is not in `GetActionMask` (which is 14 wide, 0–13), and
`RolloutSearchBot` builds candidates from `a = 8..1` plus `{9,10,11,12,13}`.** No bot in
the project can buy this.

---

## 6. Unit charges (added 2026-09-01)

| | |
|---|---|
| `UnitMaxCharges` | 5 |
| `UnitChargeRegenMs` | 1000 (one charge per second, per unit id) |

**Absent means full.** `PlayerState.UnitCharges` only holds units that have been used;
`GetUnitCharges` treats a missing key as full. This is what makes every construction path
(live game, timeSkip, rollout clone, bare `new GameState()`) correct with no init step.

Enforced in **three places that must agree**: `SpawnUnit` (refuses and spends),
`TickCooldowns` (regenerates), `GetActionMask` (gates the policy). Guarded by
`--unit-charge-check`, which passes as of 2026-09-01.

`UnitDefinition.MaxCharges` / `CooldownMs` still carry an older price-scaled formula.
**Those fields are dead** — the live rule is the flat one above.

The charge check sits **inside** `SpawnUnit`'s `!ignoreCost` branch.

---

## 7. `ignoreCost` — the free-spawn paths

An `ignoreCost` spawn costs no money, **consumes no charge**, is not recorded as an action,
and does not touch `UnitsPurchased` / `MoneySpentOnUnits`. There are four:

1. **Opening squad** — 5 free tier-1 per side, on ticks 1 / 31 / 61 / 91 / 121. Keyed on
   absolute `CurrentTick`, so league games (which start at `30*30*timeSkip`) get none.
2. **Auto-spawner**
3. **Reinforcements gadget** — a squad worth `ceil(Cost * BaseValue)` of roster value,
   scheduled 15 ticks apart, **lowest tier first**. `BaseValue` is an EFFICIENCY MULTIPLIER
   (x1.33 / x1.5 / x3), not a tier, since the 2026-09-03 rebalance, and each tier stops
   buying while one more copy would still fit so the remainder falls to cheaper units.
   3-7 units at level 1, 11-17 at level 2, 23-32 at level 3, depending on the price curve.
4. **Wall gadget**

Because these bypass charges, they are the **only** ways to exceed the per-unit-id rate
cap. That coupling is the single most important strategic consequence of the charges
change and no bot currently exploits it.

---

## 8. Gadgets

Three slots: Offense (action 11), Defense (12), Signature (13). Cooldown in ticks is
`CooldownMs / (1000/30)`.

**XP is a flat 100 per cast, for every gadget, regardless of effect, damage or value.**
`AddGadgetXp` upgrades when `GadgetXp[family] >= currentDef.UpgradeCost`, then resets XP to
0 and puts the upgraded gadget on cooldown. So **upgrades are bought with casts, not with
value**, and the number of casts needed is `UpgradeCost / 100`:

| family | L1 cost | casts to L2 | casts L2→L3 | what the upgrade buys |
|---|---|---|---|---|
| wall | $50 | 3 | 7 | 400 HP → 6,000 HP → 48,000 HP |
| reinforcements | $12 | 6 | 9 | $16 → $270 → $6,000 of free units |
| nuke | $20 | 5 | 7 | 200 → 3,000 → 24,000 dmg |
| freeze | $15 | 7 | 15 | 90 → 150 → 180 tick freeze |
| firebomb | $18 | 9 | 14 | 10 → 70 → 400 DPS |
| snipe | $30 | 4 | 9 | 1,500 → 22,500 → 55,000 dmg |
| heal | $25 | 7 | 15 | 15 → 90 → 500 HPS |
| speed | $30 | 7 | 15 | ×1.5 → ×2 → ×10 speed |
| cash | $75 | 7 | 11 | $100 → $1,500 → 8x $1,500 |
| rage | $30 | 5 | 11 | ×2 → ×4 → ×10 damage |
| divine | $50 | 4 | 11 | 100 → 2,500 → 10,000 shield (L3 = invulnerability) |
| meteor | $50 | 4 | 7 | 500 → 2,500 dmg |
| goo | $70 | 4 | 7 | 20 → 300 → 2,400 HPS, radius 200 → 1000 |
| poison | $65 | 4 | 7 | 22 → 165 → 1,320 DPS |
| wave | $40 | 5 | 9 | 500 → 1,000 → 3,000 knockback, cap 50 → 100 → 1,000 units |
| blackhole | $80 | 5 | 14 | 16 → 240 → 1,920 DPS |

`wall` → `wall_2` is **3 casts × $50 = $150 to turn a 400 HP wall into a 6,000 HP one.**
`reinforcements` → `reinforcements_3` is 15 casts total and yields **$6,000 of free roster
value per cast on a 10 s cooldown, charge-free** — for a $2,000 cast, so x3.

Casting with `position == -1` routes through `GadgetTargeting.AutoTarget`, which may return
`null` — that **refuses** the cast: no money spent, no cooldown started, `ApplyAction`
returns false.

**The nuke damages BOTH castles** for `BaseValue / 2` (100 / 1,500 / 12,000).

---

## 9. Map effects (the map is a gameplay input)

| map | effect |
|---|---|
| White | +10 % HP |
| Purple | +10 % move speed |
| Blue | −25 % fire damage |
| Green | −10 % move speed |
| Yellow | −10 % damage |
| Orange | +10 % fire damage |
| Red | every 10–30 s, heal every unit 10–50 % of max HP |
| Black | knockback ×1.5 and **double** flight time |
| *shadow* | +10 % damage, multiplied on top of the underlying map |

Applied at exactly four choke points: `SpawnUnit` (HP/damage/speed, after the weirdo roll),
`ProcessStatuses` (fire), `MoveAndFight` (knockback), `ProcessMapHealPulse`.

HP and damage round; **speed does not**. `MapEffects.ScaleStat` keeps 0 at 0 (walls have
`Damage = 0` and a blanket floor of 1 would arm them). A multiplier of exactly 1 returns
the input untouched, so a no-effect map is byte-identical to pre-feature by construction.

**The Red heal pulse invalidates the chump and wipe arithmetic** — blockers get healed
10–50 % on a 10–30 s cycle. Nothing in any bot accounts for this.

---

## 10. Special units

**`weirdo`** (Black tier 4). Rolls HP, damage and speed independently in [0.5, 2.0] from the
engine's seeded `Rng` on every spawn. `VisualScale` is the mean of the three.

**SIZE IS APPEARANCE ONLY.** The first version scaled real `Width`/`Height` and broke
combat, because `ClampToContact` used the instance width while `FindTargetsFast` measured
from the definition. The invariant to protect is **`unit.Width == def.Width` for every
spawned unit**. Per-instance stats live on `Unit.Damage` and `Unit.BaseSpeed`
(`CurrentSpeed` is the live value and cannot hold a stat).

**`monky`** (Purple tier 7). Health halved, then given a shield equal to the new health.

---

## 11. Action space

| id | action |
|---|---|
| 0 | wait |
| 1–8 | spawn roster tier N (`Roster[N-1]`) |
| 9 | invest (→ ARMAGEDDON at count 8) |
| 10 | repair |
| 11 / 12 / 13 | offense / defense / signature gadget |
| 14 | **auto-spawner — reachable via `ApplyAction`, NOT in the mask** |

`GetActionMask` returns 14 slots (0–13). Unit slots are gated on money **and** charges.
Slot 9 also closes once `ArmageddonUsed`.

Action 14 exists only so recordings that contain an auto-spawner purchase can be replayed.

### Decision cadence — differs by harness

| context | interval |
|---|---|
| `HeuristicBot.DecisionIntervalTicks` | **5 ticks** (6/s) |
| `AIModelOpponent`, BotArena modes | 3 ticks (10/s) |
| Python training (`Simulation/Program.cs`) | 9 ticks |

---

## 12. `HeuristicBot` — structure of `Decide()`

Settings live in `HeuristicBotSettings`: **95 `{ get; init; }` knobs** plus named presets.
The shipped singleplayer profile is chosen in `GameHostingService` from three static bools
in `Program.cs`; all default `false`, so the shipped bot is `HeuristicBotSettings.Default`
unless one is set.

`Act(Func<bool>)` performs one action per decision and **queues** the rest.
**It returns `true` when it merely queues**, so a caller that checks the return value gets
a false positive for an action that has not happened and may still fail.

Evaluation order (all early-`return`s yield the whole decision):

1. loadout / roster guards
2. observed-enemy bookkeeping, `threatScore`, `defenseScore`, `enemyIsClose`
3. `ThreatModel.Build` (if `DefenceOnly` or `RepairPriceCheck`)
4. time-to-death — observed (HP window) vs geometric, sentinel-discarding max
5. `investmentRunwayIsSafe`, `runwayDeficitSeconds`, **`reactiveSpendBudget`**
6. wave-wipe opportunity (needs `WaveWipeMinUnits` = **3**)
7. `survivalEmergency` (TTD ≤ `SurvivalEmergencySeconds` = 12 s) → uncaps the budget
8. `inDanger = (enemyIsClose && !investmentRunwayIsSafe) || survivalEmergency`
9. **nuke emergency repair** → `return`
10. **repair** (`RepairTtdSeconds` = 5.5, or `RepairHpFloorPct`) → `return`
11. **invest claim** (if runway safe) → `return`
12. offense / defense / signature gadgets
13. threat relief netting
14. **wiper** purchase (one unit that one-shots the toughest committed enemy)
15. **reactive `SpendOnUnits(preferDefense: true)`** — only if `inDanger`
16. fallback invest → `return`
17. **attack `SpendOnUnits(preferDefense: false)`** — gated on `!inDanger &&
    InvestmentCount >= AttackGateMinInvestment (6) && (Income >= 50 || hasIncomeAdvantage)
    && !attackDisengaged && !hazardBlackout`

### Key default thresholds

| setting | default |
|---|---|
| `EnemyIsCloseDistance` | 700 |
| `RepairTtdSeconds` | 5.5 |
| `SurvivalEmergencySeconds` | 12 |
| `ReactiveSpendEVMultiplier` | 1.5 |
| `WaveWipeRadius` | 500 |
| `WaveWipeMinUnits` | 3 |
| `AttackGateMinInvestment` | 6 |
| `KillerInstinctHpThreshold` | 2676 |
| `GadgetUpgradeSpam` | **false** |
| `DefenceOnly` | false |

### `SpendOnUnits` — the purchase pipeline

`RankPool(minTier)` ranks by `ScoreUnit` (cost-efficiency) or `RawPower` (rich mode), then:
`outclassPick` / `matchedPick` / `anyAffordable` fallback → `PowerPickAffordable` →
`TechEscalation` → **one** `Act(() => engine.SpawnUnit(_side, pick.def.Id))`.

**Every filter in that pipeline tests `def.Cost <= spendable` and nothing else.** There is
no charge test anywhere, and no fallback if the single spawn attempt fails.

`DefensiveResponse` (the `DefenceOnly` survival-law path) buys the single `cheapest` unit
and decrements `_blockCredit` **only inside** `if (Act(...))`, so a failed spawn banks
credit up to `MaxBlockCredit` that can never be spent.

---

## 13. Things that are silently invisible

Assembled because each one has already cost a wrong conclusion or is positioned to:

- **A failed purchase is logged nowhere.** `LastUnitsPurchased`, `ActionCounts[]`,
  `UnitsPurchased[]` and `MoneySpentOnUnits[]` all increment on success only.
- **Nothing may write a non-finite value into serialisable state.** Two instances so far:
  `wall_3` → `Unit.AttackCooldown`, and `AutoSpawnPriceFor(20)` → `AutoSpawnPrice`. Both
  crashed live games through `System.Text.Json`.
- **`GameEngine.Clone` is shallow.** Any mutable reference field added to `GameEngine` is
  shared with every search rollout. This is why auto-spawner state lives on `PlayerState`.
- **The browser wire format is an allowlist.** A new field on `GameState` / `PlayerState` /
  `Unit` is invisible to the client until added to `GameStateWire` *and* `UNIT_FIELDS` in
  `game-connection.js`, in the same positional order.
- **Gadget target positions are not recorded** in `.replay`, only the action id. Every
  reconstruction re-aims with the auto-targeter.
- **A win by default is not a win** — exclude `end_reason` `disconnect` / `abandoned`.
- **An agent's own games must not enter `recordings/`** — pass `--RecordingsDir`.
