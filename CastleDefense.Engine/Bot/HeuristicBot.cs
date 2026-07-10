using CastleDefense.Engine.Data;
using CastleDefense.Engine.Definitions;
using CastleDefense.Engine.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CastleDefense.Engine.Bot
{
    // Rule-based opponent. Drives a side entirely through GameEngine's public API
    // (SpawnUnit / Invest / Repair / UseGadget) -- the same surface a human player
    // uses via the SignalR hub -- so it plays by the exact same rules a human does.
    public class HeuristicBot
    {
        private readonly int _side;

        // ~6 decisions/sec at 30 TPS. Fast enough to never leave money idle,
        // slow enough that it doesn't look like it's cheating with instant reactions.
        private const int DecisionIntervalTicks = 5;
        private long _nextDecisionTick;

        public HeuristicBot(int side)
        {
            _side = side;
        }

        public void Update(GameEngine engine)
        {
            var state = engine._state;
            if (state.IsGameOver) return;
            if (state.CurrentTick < _nextDecisionTick) return;
            _nextDecisionTick = state.CurrentTick + DecisionIntervalTicks;

            Decide(engine);
        }

        private void Decide(GameEngine engine)
        {
            var state = engine._state;
            var me = _side == 1 ? state.Player1 : state.Player2;

            // Loadout not assigned yet (shouldn't happen once the game has started).
            if (me.OffensiveGadget == null || me.DefensiveGadget == null || me.SignatureGadget == null) return;

            var teamDef = GameDataManager.Teams.FirstOrDefault(t => t.Color == me.Team);
            if (teamDef == null || teamDef.Roster.Count == 0) return;

            int myCastlePos = _side == 1 ? 200 : GameEngine.MAP_WIDTH - 200;

            var myUnits = state.Units.Where(u => u.Side == _side).ToList();
            var enemyUnits = state.Units.Where(u => u.Side != _side).ToList();

            // --- THREAT ASSESSMENT ---
            // Weight enemy strength by how close it is to our castle so a distant
            // skirmish doesn't trigger the same panic response as a unit at the gate.
            float threatScore = 0f;
            foreach (var u in enemyUnits)
            {
                float distToMyCastle = Math.Abs(u.Position - myCastlePos);
                float proximityWeight = Math.Max(0.15f, 1200f / (distToMyCastle + 250f));
                threatScore += Power(u) * proximityWeight;
            }
            float defenseScore = myUnits.Sum(Power);

            bool enemyIsClose = enemyUnits.Count > 0 && enemyUnits.Min(u => Math.Abs(u.Position - myCastlePos)) < 700;
            float castleHpPct = me.CastleMaxHealth > 0 ? (float)me.CastleHealth / me.CastleMaxHealth : 1f;

            // A lone leaker slipping past the main fight and reaching the castle doesn't
            // move the aggregate threat-vs-defense comparison at all (our army is still
            // huge and winning on paper), but it's still steady chip damage every time it
            // happens -- and new spawns land right next to the castle, so reacting to it
            // directly is cheap. Only counts as a genuine leak if our own army has already
            // moved on toward the enemy though -- otherwise this just fires constantly
            // during ordinary early-game combat, which naturally happens close to home
            // since spawn sits right next to the castle.
            bool armyHasMovedOn = myUnits.Count == 0 || myUnits.Average(u => Math.Abs(u.Position - myCastlePos)) > 500;
            bool anyoneAtTheGate = armyHasMovedOn && enemyUnits.Count > 0 && enemyUnits.Min(u => Math.Abs(u.Position - myCastlePos)) < 300;

            bool inDanger = anyoneAtTheGate || (enemyIsClose && threatScore > defenseScore * 0.9f) || (castleHpPct < 0.4f && enemyUnits.Count > 0);

            // --- GADGETS: cheap relative to overall spend, high impact, own cooldowns ---
            TryUseOffenseGadget(engine, me, myUnits, enemyUnits, myCastlePos);
            TryUseDefenseGadget(engine, me, myUnits, myCastlePos);
            TryUseSignatureGadget(engine, me, myUnits, enemyUnits, myCastlePos, inDanger, castleHpPct);

            // --- MILITARY / ECONOMY ---
            // A single decision tick can spend far more than one unit's worth of money
            // (income keeps flowing between ticks), so unit purchases loop instead of
            // buying once -- otherwise money piles up uselessly between decisions.
            if (inDanger)
            {
                SpendOnUnits(engine, me, teamDef.Roster, preferDefense: true, enemyUnits);
                return;
            }

            // Repair first -- unlike investing, it has no downside (it doesn't touch
            // income), so it's the default sink for money that unit purchases can't
            // usefully absorb. Crucially, Repair() also permanently raises
            // CastleMaxHealth (1000 -> 12000 -> 23000 -> ...) even when called at full
            // health -- multiple enemy units can attack the castle in the very same
            // tick with no per-tick damage cap, so a bigger HP pool is real insurance
            // against a burst that a 1000 HP castle just doesn't survive. Repair
            // opportunistically whenever safe and flush, not only when already hurt.
            bool needsHealing = castleHpPct < 0.95f && me.Money >= me.RepairPrice * 1.25;
            bool canAffordGrowthRepair = me.Money >= me.RepairPrice * 3;
            if (needsHealing || canAffordGrowthRepair)
            {
                engine.Repair(_side);
            }

            // Investing has essentially no downside in THIS economy: the hardcoded
            // starting Income (2) is already below the investment formula's very first
            // step (~2.65), so every investment -- starting with the very first one -- is
            // a strict, permanent income increase. (That's not true of every economy this
            // bot might run under: if the starting income is ever tuned back up above
            // where the formula naturally would be, the first investment can crater income
            // for a while before recovering -- worth re-checking this assumption if the
            // starting Income constant in PlayerState() ever changes again.) So treat it
            // like repair: take it whenever safe and affordable, no elaborate gating.
            if (me.Money >= me.InvestmentPrice)
            {
                engine.Invest(_side);
            }

            // Spend whatever's left keeping steady pressure on the enemy castle.
            SpendOnUnits(engine, me, teamDef.Roster, preferDefense: false, enemyUnits);
        }

        private static float Power(Unit u)
        {
            float aps = u.AttackSpeed > 0 ? u.AttackSpeed : 0.3f;
            return u.Damage * aps + u.CurrentHealth * 0.04f + u.CurrentShield * 0.04f;
        }

        // Repeatedly buys the best-value affordable unit until money runs out (or the
        // roster runs out of affordable options). One decision tick's worth of income
        // is easily enough to buy several cheap units, so a single purchase per tick
        // would leave most of the income unspent.
        // Cap how large our own army is allowed to get: combat is O(units^2) per tick,
        // and a battlefield with hundreds of units per side isn't more effective anyway
        // (they just queue up waiting for a turn to attack).
        private const int MaxOwnUnitsOnField = 120;
        private const int MaxPurchasesPerDecision = 40;

        private void SpendOnUnits(GameEngine engine, PlayerState me, List<UnitDefinition> roster, bool preferDefense, List<Unit> enemyUnits)
        {
            int ownUnitCount = engine._state.Units.Count(u => u.Side == _side);

            // Once the cost-efficient swarm is already at full size and the economy is
            // still piling up money beyond what it needs, cost-per-value stops mattering
            // -- more cheap units just stalemates against an equally cheap trickle from
            // the other side. Switch to buying pure raw power instead, and make room for it.
            double topCost = roster.Where(u => u.Cost > 0).Select(u => (double)u.Cost).DefaultIfEmpty(1).Max();
            bool richMode = ownUnitCount >= MaxOwnUnitsOnField && me.Money >= topCost * 3;
            int cap = richMode ? MaxOwnUnitsOnField * 2 : MaxOwnUnitsOnField;

            if (ownUnitCount >= cap) return;

            // A cheap unit's HP means nothing if the enemy's biggest hitter one-shots it --
            // and since every unit in this game is melee (no roster defines a Range, so
            // ArmorType/AttackType always fall back to melee) an attack cleaves ALL
            // defenders in contact simultaneously, so a whole cheap cluster can die to a
            // single swing. Size against the worst hit currently on the field, not the
            // average, since the average is what gets a fragile swarm wiped out.
            float enemyHitDamage = enemyUnits.Count > 0 ? enemyUnits.Max(u => (float)u.Damage) : 0f;

            // Cost-efficiency ratios alone chronically default to the cheapest unit that
            // still scores well, which is exactly the fodder that gets cleaved by a
            // sustained mid/high-tier push. So identify what tier the enemy is actually
            // fielding (weighted by damage contributed, not raw count). Matching that tier
            // exactly still tends to converge on a near-mirror trade though, since the
            // matching tier is usually also the best-value option within "tier >=
            // dominant" -- a fair fight, not a won one. Prefer OUTCLASSING it by one tier
            // when affordable, only settling for an even trade or pure cost-efficiency
            // fallback if we can't afford the tech edge yet.
            int dominantEnemyTier = enemyUnits.Count > 0
                ? enemyUnits.GroupBy(u => u.Tier).OrderByDescending(g => g.Sum(u => (double)u.Damage)).First().Key
                : 0;

            List<(UnitDefinition def, double score)> RankPool(int minTier)
            {
                IEnumerable<UnitDefinition> pool = roster;
                if (minTier > 0)
                    pool = pool.Where(u => u.Tier >= minTier);

                return pool
                    .Select(def => (def, score: richMode ? RawPower(def, enemyHitDamage) : ScoreUnit(def, preferDefense, enemyHitDamage)))
                    .OrderByDescending(x => x.score)
                    .ToList();
            }

            var outclassing = RankPool(dominantEnemyTier + 1);
            var tierMatched = RankPool(dominantEnemyTier);
            var anyAffordable = RankPool(0);

            // In a low-income economy, units are cheap enough to always win the "who's
            // affordable first" race against gadgets and investing -- both are checked
            // every decision too (see TryUse*Gadget and the invest check above), but never
            // accumulate the money needed to actually fire if units keep draining the pool
            // back to near-zero first. A reserve that's just a FRACTION of instantaneous
            // surplus doesn't fix this: it's recomputed from scratch every decision, so it
            // never persists long enough across ticks to actually reach a bigger target --
            // it needs to hold the SAME dollars back release after release until the goal
            // is hit. Once we have a minimal army out, reserve the full remaining gap
            // toward whichever comes first: our next investment, or the cheapest gadget
            // that's off cooldown but not yet affordable.
            bool haveMinimalArmy = ownUnitCount >= 10;
            double investGap = me.Money < me.InvestmentPrice ? me.InvestmentPrice - me.Money : double.MaxValue;
            double gadgetGap = double.MaxValue;
            foreach (var g in new[] { me.OffensiveGadget, me.DefensiveGadget, me.SignatureGadget })
            {
                if (g == null) continue;
                bool onCooldown = me.GadgetCooldowns.TryGetValue(g.Id, out var cd) && cd > 0;
                if (onCooldown) continue;
                if (g.Cost > me.Money) gadgetGap = Math.Min(gadgetGap, g.Cost - me.Money);
            }
            double smallestGap = Math.Min(investGap, gadgetGap);
            // Cap at 70% of current money even while actively saving -- replacements for
            // combat losses still need to keep flowing, or the "10 unit minimum" gate above
            // becomes a one-time check we immediately fall below as the army trades.
            double reserve = haveMinimalArmy && smallestGap != double.MaxValue ? Math.Min(smallestGap, me.Money * 0.7) : 0;

            int budget = Math.Min(MaxPurchasesPerDecision, cap - ownUnitCount);
            for (int i = 0; i < budget; i++)
            {
                double spendable = me.Money - reserve;
                var matchedPick = tierMatched.FirstOrDefault(x => x.def.Cost > 0 && x.def.Cost <= spendable);
                var outclassPick = outclassing.FirstOrDefault(x => x.def.Cost > 0 && x.def.Cost <= spendable);

                // Only take the tech edge if it's ALSO a competitive value, not just a
                // higher tier -- a naive "always outclass" rule ends up paying a real
                // premium (e.g. 33% more per unit) for a WORSE cost-efficiency pick every
                // single purchase, which starves total army size for no real benefit once
                // the cost-matched option already fields comfortably (see
                // SurvivabilityMultiplier). That compounds into a much smaller army over a
                // whole game, which is exactly what happened testing this against a cheap
                // tier-1 spam bot: it kept passing up 3-cost units for a 4-cost one that
                // scored *worse* per dollar, and lost the production race outright.
                var pick = matchedPick.def != null && outclassPick.def != null && outclassPick.score < matchedPick.score * 0.9
                    ? matchedPick
                    : (outclassPick.def != null ? outclassPick : matchedPick);
                if (pick.def == null) pick = anyAffordable.FirstOrDefault(x => x.def.Cost > 0 && x.def.Cost <= spendable);
                if (pick.def == null) break;

                engine.SpawnUnit(_side, pick.def.Id);
            }
        }

        // Below ~1.5x the enemy's average hit, a unit is one-or-two-shot fodder that
        // never gets to swing back enough to matter -- crush its score rather than
        // excluding it outright (so it's still a fallback if literally nothing survives).
        private static double SurvivabilityMultiplier(UnitDefinition def, float enemyHitDamage)
        {
            if (enemyHitDamage <= 0) return 1.0;
            double effectiveHp = def.MaxHealth + def.MaxShield;
            double hitsSurvived = effectiveHp / enemyHitDamage;

            // Dies to a single hit -- pure cleave fodder, worth almost nothing.
            if (hitsSurvived < 1.0) return 0.05;
            // Survives one hit but dies to essentially any second one (including a
            // stray hit from a DIFFERENT enemy in the same cleave). "Just barely enough
            // HP to pass" is not the same as "actually holds up" -- needs real headroom.
            if (hitsSurvived < 2.5) return 0.2;
            return 1.0;
        }

        private static double ScoreUnit(UnitDefinition def, bool preferDefense, float enemyHitDamage)
        {
            double cost = Math.Max(1, def.Cost);
            double dps = def.Damage * (def.AttackSpeed > 0 ? def.AttackSpeed : 0.3f) * RangeMultiplier(def);
            // Defense leans harder into durability (blocking/trading); offense still wants
            // real HP too -- a pure glass cannon dies before it ever reaches the castle,
            // and a fragile army stops replacing its losses the moment money gets tight.
            double baseScore = preferDefense
                ? (dps * 1.5 + def.MaxHealth + def.MaxShield) / cost
                : (dps * 1.8 + def.MaxHealth * 0.8 + def.MaxShield * 0.8) / cost;
            return baseScore * SurvivabilityMultiplier(def, enemyHitDamage);
        }

        private static double RawPower(UnitDefinition def, float enemyHitDamage)
        {
            double dps = def.Damage * (def.AttackSpeed > 0 ? def.AttackSpeed : 0.3f) * RangeMultiplier(def);
            return (dps + def.MaxHealth + def.MaxShield) * SurvivabilityMultiplier(def, enemyHitDamage);
        }

        // Melee combat oscillates (hit, knock the target back, chase, re-engage), which
        // wastes a lot of a melee unit's uptime. Ranged/Siege/Magic units suffer less from
        // this (they also take half knockback impact themselves) and Ranged can hit flyers,
        // so weight them a bit higher rather than always defaulting to the cheapest melee grunt.
        private static double RangeMultiplier(UnitDefinition def) => def.AttackType switch
        {
            AttackType.Ranged => 1.3,
            AttackType.Magic => 1.25,
            AttackType.Siege => 1.15,
            _ => 1.0,
        };

        private bool IsReady(PlayerState me, GadgetDefinition def)
        {
            if (def == null) return false;
            if (me.GadgetCooldowns.TryGetValue(def.Id, out var cd) && cd > 0) return false;
            return me.Money >= def.Cost;
        }

        // The offense slot can be any of "nuke" / "firebomb" / "snipe" / "freeze" --
        // the loadout isn't fixed, so all four need real usage logic, not just whichever
        // one happened to be equipped when this was written.
        private void TryUseOffenseGadget(GameEngine engine, PlayerState me, List<Unit> myUnits, List<Unit> enemyUnits, int myCastlePos)
        {
            var def = me.OffensiveGadget;
            if (!IsReady(me, def)) return;
            if (enemyUnits.Count == 0) return; // nothing to hit -- don't burn the cooldown/cost for free

            string family = def.Id.Split('_')[0].ToLowerInvariant();
            int radius = Math.Max(150, def.Radius);

            switch (family)
            {
                case "snipe":
                    // No splash, no friendly fire, no self-castle-damage. SnipeEffect
                    // targets whichever enemy is nearest the given position, so aiming at
                    // our own castle makes it snipe whichever enemy is closest to reaching
                    // it -- directly preventing the exact chip damage that ends games,
                    // rather than chasing whoever hits hardest somewhere else on the field.
                    engine.UseGadget(_side, def.Id, myCastlePos);
                    break;

                case "freeze":
                    // Hits and freezes EVERY enemy unit on the field regardless of
                    // position -- no friendly fire. Frozen units skip their whole
                    // attack/move step, so they take free hits while stunned. This is what
                    // actually breaks a chokepoint stalemate: a small steady trickle of
                    // defenders can otherwise permanently pin a much bigger army just by
                    // always having *something* in contact range.
                    engine.UseGadget(_side, def.Id, 0);
                    break;

                case "nuke":
                {
                    // Always damages BOTH castles by BaseValue/2 and hits ALL units (any
                    // side) in the blast radius -- a real cost, not a free chip. Only
                    // worth it against an actual cluster, and only where none of our own
                    // units would eat the same blast.
                    int? target = FindBestAoeTarget(enemyUnits, radius);
                    if (target.HasValue && enemyUnits.Count >= 2 && !myUnits.Any(u => Math.Abs(u.Position - target.Value) <= radius))
                        engine.UseGadget(_side, def.Id, target.Value);
                    break;
                }

                case "firebomb":
                {
                    // Leaves a damage-over-time zone that burns ANYONE standing in it,
                    // ally or enemy (FireHazard doesn't filter by side). Prefer the densest
                    // enemy cluster, but if that overlaps our own units, retarget to
                    // whichever enemy is farthest from our army instead of skipping the
                    // cast outright -- still a valid burn, and this gadget needs real usage
                    // to ever earn enough XP to upgrade past its weak base tier.
                    int? target = FindBestAoeTarget(enemyUnits, radius);
                    if (target.HasValue && myUnits.Any(u => Math.Abs(u.Position - target.Value) <= radius))
                    {
                        float myAvgPos = myUnits.Count > 0 ? myUnits.Average(u => u.Position) : myCastlePos;
                        target = (int)enemyUnits.OrderByDescending(u => Math.Abs(u.Position - myAvgPos)).First().Position;
                    }
                    if (target.HasValue)
                        engine.UseGadget(_side, def.Id, target.Value);
                    break;
                }
            }
        }

        // The defense slot can be any of "heal" / "reinforcements" / "speed" / "wall".
        private void TryUseDefenseGadget(GameEngine engine, PlayerState me, List<Unit> myUnits, int myCastlePos)
        {
            var def = me.DefensiveGadget;
            if (!IsReady(me, def)) return;

            string family = def.Id.Split('_')[0].ToLowerInvariant();

            switch (family)
            {
                case "heal":
                {
                    if (myUnits.Count == 0) return; // nothing to heal
                    float avgHpPct = myUnits.Average(u => u.MaxHealth > 0 ? (float)u.CurrentHealth / u.MaxHealth : 1f);
                    if (avgHpPct < 0.85f)
                        engine.UseGadget(_side, def.Id, myCastlePos);
                    break;
                }

                case "reinforcements":
                    // Spawns free units (bypasses cost entirely) regardless of position --
                    // pure value with no downside, so use it on cooldown.
                    engine.UseGadget(_side, def.Id, myCastlePos);
                    break;

                case "speed":
                    if (myUnits.Count > 0)
                        engine.UseGadget(_side, def.Id, myCastlePos);
                    break;

                case "wall":
                {
                    // Only ONE wall (any level) is ever allowed on the field at a time --
                    // casting again while one is already up just refunds the cost and
                    // grants NO xp, which stalls its upgrade path if we keep trying anyway.
                    // Wait for the existing one to die before recasting.
                    bool alreadyHaveWall = engine._state.Units.Any(u => u.Side == _side && u.DefinitionId.StartsWith("wall"));
                    if (alreadyHaveWall) break;

                    // WallEffect ignores the position for level 1 (fixed spawn point) but
                    // uses it directly for level 2/3, so place it in our own front line
                    // where it can actually tank alongside the rest of the army.
                    int target = myUnits.Count > 0 ? (int)myUnits.Average(u => u.Position) : myCastlePos;
                    engine.UseGadget(_side, def.Id, target);
                    break;
                }
            }
        }

        private void TryUseSignatureGadget(GameEngine engine, PlayerState me, List<Unit> myUnits, List<Unit> enemyUnits,
            int myCastlePos, bool inDanger, float castleHpPct)
        {
            var def = me.SignatureGadget;
            if (!IsReady(me, def)) return;

            string family = def.Id.Split('_')[0].ToLowerInvariant();

            switch (family)
            {
                case "cash":
                    // Pure economy, no downside to the team's own board state -- always take it.
                    engine.UseGadget(_side, def.Id, myCastlePos);
                    break;

                case "rage":
                    if (myUnits.Count > 0 && (inDanger || myUnits.Any(u => enemyUnits.Any(e => Math.Abs(e.Position - u.Position) < 250))))
                        engine.UseGadget(_side, def.Id, myCastlePos);
                    break;

                case "divine":
                    if (castleHpPct < 0.3f || (inDanger && enemyUnits.Count >= 3))
                        engine.UseGadget(_side, def.Id, myCastlePos);
                    break;

                case "wave":
                    // Always fires from our own edge regardless of target position.
                    engine.UseGadget(_side, def.Id, myCastlePos);
                    break;

                case "goo":
                    {
                        int target = myUnits.Count > 0 ? (int)myUnits.Average(u => u.Position) : myCastlePos;
                        engine.UseGadget(_side, def.Id, target);
                        break;
                    }

                case "meteor":
                case "poison":
                    {
                        // Both of these only ever affect enemy units -- no friendly fire risk.
                        int? target = FindBestAoeTarget(enemyUnits, Math.Max(150, def.Radius));
                        if (target.HasValue)
                            engine.UseGadget(_side, def.Id, target.Value);
                        break;
                    }

                case "blackhole":
                    {
                        // Unlike meteor/poison, a black hole pulls in and damages BOTH sides
                        // in its radius (and can instantly kill non-tier-8 units at its core),
                        // so only fire it where none of our own units would get caught in it.
                        int radius = Math.Max(150, def.Radius);
                        int? target = FindBestAoeTarget(enemyUnits, radius);
                        if (target.HasValue && !myUnits.Any(u => Math.Abs(u.Position - target.Value) <= radius))
                            engine.UseGadget(_side, def.Id, target.Value);
                        break;
                    }
            }
        }

        // Finds the enemy unit position with the most enemy "power" clustered within radius.
        private int? FindBestAoeTarget(List<Unit> targets, int radius)
        {
            if (targets.Count == 0) return null;

            int bestPos = 0;
            float bestScore = -1f;
            foreach (var candidate in targets)
            {
                float score = 0f;
                foreach (var other in targets)
                {
                    if (Math.Abs(other.Position - candidate.Position) <= radius)
                        score += Power(other);
                }
                if (score > bestScore)
                {
                    bestScore = score;
                    bestPos = (int)candidate.Position;
                }
            }
            return bestPos;
        }
    }
}
