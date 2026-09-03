using System;
using System.Collections.Generic;
using System.Linq;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Definitions;

namespace CastleDefense.Engine.Gadgets
{
    /// <summary>
    /// Reinforcements: pay the gadget's cost, get a squad of FREE units whose combined
    /// roster price is a multiple of that cost.
    ///
    /// REBALANCED 2026-09-03. It used to spawn a flat FIVE units of tier
    /// <c>BaseValue</c> -- so reinforcements_3 handed over five tier-7 units, $10,330 of
    /// White roster value for a $3,000 cast, and reinforcements_2 five tier-5 for $405 at
    /// $180. The payout was untethered from the price, which is why the loadout sweep put
    /// reinforcements 3.6 points clear of every other defence gadget (CLAUDE.md, "RE-MEASURED
    /// 2026-09-02"), and why HeuristicBot's own wipe-pricing needed a special case to stop it
    /// buying the tier 7 its gadget was about to give it for nothing.
    ///
    /// The gadget's <c>BaseValue</c> is now an EFFICIENCY MULTIPLIER, not a tier:
    /// x1.33 at level 1, x1.5 at level 2, x3 at level 3. The budget is
    /// <c>ceil(Cost * BaseValue)</c> and the squad is whatever that budget buys.
    ///
    /// THE CSV IS PART OF THIS CHANGE. <c>BaseLabel</c> reads EFFICIENCY and <c>BaseValue</c>
    /// carries the multiplier, in BOTH copies of master_gadgets.csv -- the engine's
    /// (CastleDefense.Engine/Data) and the client's (wwwroot/assets), which is what the
    /// Collection screen prints. Reading BaseValue as a tier anywhere is now a bug; the one
    /// place that did was <see cref="Bot.OpponentEconomy.ObserveGadgetCast"/>.
    /// </summary>
    public class ReinforcementsEffect : IGadgetEffect
    {
        private readonly GadgetDefinition _def;

        /// <summary>
        /// Ticks between consecutive arrivals: 15, i.e. one unit every half second at 30 Hz.
        ///
        /// The flat-five version ran delays 0/10/20/30/40 and the first cut of the budget
        /// rewrite kept that 10-tick spacing. With squads now running to 28 units it read as
        /// a firehose, so it was slowed to a half second per unit -- which also means a level-3
        /// squad takes about 14 seconds to finish walking out, and the tail of it arrives into
        /// a battle the head has already joined.
        /// </summary>
        public const int SpawnIntervalTicks = 15;

        public ReinforcementsEffect(GadgetDefinition def)
        {
            _def = def;
        }

        /// <summary>
        /// Roster value the cast is allowed to spend, in whole dollars.
        ///
        /// CEILING, like every other price in the game (PlayerState.WholeDollars): level 1 is
        /// $12 x 1.33 = $15.96, and the design calls that $16 rather than $15.
        /// </summary>
        public static int BudgetFor(GadgetDefinition def)
            => (int)Math.Ceiling(def.Cost * (double)def.BaseValue);

        /// <summary>
        /// The squad a cast produces, IN SPAWN ORDER -- lowest price first, biggest unit last.
        ///
        /// COMPOSITION IS GREEDY FROM THE TOP, BUT IT MOVES ON ONE UNIT EARLY. It takes as
        /// many of the most expensive affordable unit as fit WHILE STILL LEAVING ROOM FOR TWO
        /// -- the moment only one more would fit, it drops to the next unit down. So the rule
        /// is `floor(remaining / cost) - 1`, not `floor(remaining / cost)`.
        ///
        /// That one change is what turns the squad from a handful of big units into an ARMY:
        /// $6,000 of White went from 12 units (2 legg, 5 bread, 2 alpacco, 1 squirt, 1 catto,
        /// 1 doggo) to 28 (1 legg, 10 bread, 5 alpacco, 7 ringo, 1 squirt, 2 catto, 2 doggo),
        /// for the same $6,000. Each tier hands its "last affordable copy" of budget down to
        /// the tier below, where it buys several bodies instead of one.
        ///
        /// THE CHEAPEST UNIT IS EXEMPT, because there is nothing below it to move on to: it
        /// buys `floor(remaining / cost)` outright. Doing otherwise would strand up to two
        /// tier-1 prices at the bottom of every cast.
        ///
        /// THE LAST DOLLARS ARE NOT DISCARDED: if anything is left once even the cheapest unit
        /// is out of reach, the budget overspends by one tier-1 rather than evaporating. The
        /// alternative rounds a $15.96 level-1 cast down to nothing on a team whose tier 1
        /// costs $4.
        ///
        /// ORDER IS THE BALANCE LEVER, not presentation. The chumps walk out FIRST, so they
        /// are the ones that meet whatever is already on the field and the expensive units
        /// arrive behind a screen instead of leading it. Reversing it hands the enemy the
        /// tier 7 first, alone.
        /// </summary>
        public static List<UnitDefinition> BuildSquad(GadgetDefinition def, TeamDefinition team)
        {
            var squad = new List<UnitDefinition>();
            if (def == null || team == null || team.Roster == null) return squad;

            // Sorted by PRICE, not by tier. They agree on every roster row today, but price is
            // what the budget is spent in and a rebalance could cross two tiers over.
            var byPrice = team.Roster.Where(u => u != null && u.Cost > 0)
                                     .OrderByDescending(u => u.Cost)
                                     .ThenByDescending(u => u.Tier)
                                     .ToList();
            if (byPrice.Count == 0) return squad;

            long remaining = BudgetFor(def);
            for (int idx = 0; idx < byPrice.Count; idx++)
            {
                var u = byPrice[idx];
                bool cheapest = idx == byPrice.Count - 1;

                // Leave room for two, except at the bottom of the roster where there is
                // nowhere left to hand the remainder down to.
                long n = remaining / u.Cost;
                if (!cheapest) n = Math.Max(0, n - 1);

                for (long i = 0; i < n; i++) squad.Add(u);
                remaining -= n * u.Cost;
            }

            // Whatever could not be spent buys one more of the cheapest thing available. The
            // tier-1 row is the intent; the cheapest row is the fallback if a team ever has
            // no tier 1.
            if (remaining > 0)
                squad.Add(team.Roster.FirstOrDefault(u => u != null && u.Tier == 1 && u.Cost > 0)
                          ?? byPrice[byPrice.Count - 1]);

            // Built most-expensive-first; sent least-expensive-first.
            squad.Reverse();
            return squad;
        }

        public void Execute(GameEngine engine, int side, int position)
        {
            engine.AddGadgetXp(side, "reinforcements", 100);

            var player = side == 1 ? engine._state.Player1 : engine._state.Player2;
            var teamDef = GameDataManager.Teams.Find(team => team.Color == player.Team);

            var squad = BuildSquad(_def, teamDef);

            // One scheduled arrival per unit, on the interval above. Each unit is resolved
            // HERE and carried as an id, so a cloned engine spawns exactly what the original
            // had already committed to (see PendingEffect's class note).
            for (int i = 0; i < squad.Count; i++)
            {
                engine.ScheduleEffect(i * SpawnIntervalTicks, new PendingEffect
                {
                    GadgetId = _def.Id,
                    Phase = PhaseSpawn,
                    Side = side,
                    Position = position,
                    UnitId = squad[i].Id,
                });
            }
        }

        private const int PhaseSpawn = 0;

        public void ExecuteScheduled(GameEngine engine, in PendingEffect e)
        {
            if (e.Phase != PhaseSpawn || e.UnitId == null) return;

            // (since we already payed for the gadget, spawn units for free)
            engine.SpawnUnit(e.Side, e.UnitId, true);
        }
    }
}
