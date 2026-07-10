using CastleDefense.Engine;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Definitions;
using CastleDefense.Engine.Models;
using System.Linq;

namespace CastleDefense.BotArena
{
    // Never spends a dime. Pure sanity check that the real bot can actually
    // close games out against zero resistance.
    public class DoNothingBaseline : IArenaOpponent
    {
        public void Update(GameEngine engine) { }
    }

    // Mimics an impatient human mashing the cheapest unit button as fast as
    // possible. Never invests, never repairs, never touches gadgets.
    public class RusherBaseline : IArenaOpponent
    {
        private readonly int _side;
        private const int IntervalTicks = 5;
        private long _next;

        public RusherBaseline(int side) => _side = side;

        public void Update(GameEngine engine)
        {
            var state = engine._state;
            if (state.IsGameOver || state.CurrentTick < _next) return;
            _next = state.CurrentTick + IntervalTicks;

            var me = _side == 1 ? state.Player1 : state.Player2;
            var teamDef = GameDataManager.Teams.FirstOrDefault(t => t.Color == me.Team);
            if (teamDef == null) return;

            var affordable = teamDef.Roster.Where(u => u.Cost <= me.Money).OrderBy(u => u.Cost).FirstOrDefault();
            if (affordable != null) engine.SpawnUnit(_side, affordable.Id);
        }
    }

    // "Spam bot": mashes ONE specific tier's spawn button all game, exactly as
    // fast as the decision cadence allows, and does nothing else -- no investing,
    // no repairing, no gadgets, no adapting. Skips the buy entirely (does not
    // fall back to a cheaper unit) whenever that tier isn't affordable yet.
    public class TierSpamBaseline : IArenaOpponent
    {
        private readonly int _side;
        private readonly int _tier;
        private const int IntervalTicks = 5;
        private long _next;

        public TierSpamBaseline(int side, int tier)
        {
            _side = side;
            _tier = tier;
        }

        public void Update(GameEngine engine)
        {
            var state = engine._state;
            if (state.IsGameOver || state.CurrentTick < _next) return;
            _next = state.CurrentTick + IntervalTicks;

            var me = _side == 1 ? state.Player1 : state.Player2;
            var teamDef = GameDataManager.Teams.FirstOrDefault(t => t.Color == me.Team);
            var unit = teamDef?.Roster.FirstOrDefault(u => u.Tier == _tier);
            if (unit == null || unit.Cost > me.Money) return;

            engine.SpawnUnit(_side, unit.Id);
        }
    }

    // Mimics a human who read that "investing is good" and does it every time
    // they can afford it, with no regard for the income dip it causes. Spends
    // whatever's left on the single best-value affordable unit. No gadgets.
    public class InvestorBaseline : IArenaOpponent
    {
        private readonly int _side;
        private const int IntervalTicks = 5;
        private long _next;

        public InvestorBaseline(int side) => _side = side;

        public void Update(GameEngine engine)
        {
            var state = engine._state;
            if (state.IsGameOver || state.CurrentTick < _next) return;
            _next = state.CurrentTick + IntervalTicks;

            var me = _side == 1 ? state.Player1 : state.Player2;

            if (me.Money >= me.InvestmentPrice)
            {
                engine.Invest(_side);
                return;
            }

            var teamDef = GameDataManager.Teams.FirstOrDefault(t => t.Color == me.Team);
            if (teamDef == null) return;

            UnitDefinition best = null;
            double bestScore = double.MinValue;
            foreach (var def in teamDef.Roster)
            {
                if (def.Cost > me.Money) continue;
                double dps = def.Damage * (def.AttackSpeed > 0 ? def.AttackSpeed : 0.3f);
                double score = dps / System.Math.Max(1, def.Cost);
                if (score > bestScore) { bestScore = score; best = def; }
            }
            if (best != null) engine.SpawnUnit(_side, best.Id);
        }
    }

    // A reasonably competent scripted human: buys good-value units, reacts to
    // incoming threats, invests opportunistically when flush and safe, and
    // fires gadgets on cooldown at sensible-ish targets. Meant to be the
    // toughest baseline -- a real test of whether the heuristic bot is strong.
    public class BalancedHumanBaseline : IArenaOpponent
    {
        private readonly int _side;
        private const int IntervalTicks = 5;
        private long _next;

        public BalancedHumanBaseline(int side) => _side = side;

        public void Update(GameEngine engine)
        {
            var state = engine._state;
            if (state.IsGameOver || state.CurrentTick < _next) return;
            _next = state.CurrentTick + IntervalTicks;

            var me = _side == 1 ? state.Player1 : state.Player2;
            var teamDef = GameDataManager.Teams.FirstOrDefault(t => t.Color == me.Team);
            if (teamDef == null || me.OffensiveGadget == null) return;

            int myCastlePos = _side == 1 ? 200 : GameEngine.MAP_WIDTH - 200;
            var myUnits = state.Units.Where(u => u.Side == _side).ToList();
            var enemyUnits = state.Units.Where(u => u.Side != _side).ToList();

            bool danger = enemyUnits.Any(u => System.Math.Abs(u.Position - myCastlePos) < 500) && myUnits.Count < enemyUnits.Count;

            // Naive gadget usage: fire whenever off cooldown and affordable, no real targeting smarts.
            FireIfReady(engine, me.OffensiveGadget, enemyUnits.Count > 0 ? (int)enemyUnits[0].Position : myCastlePos);
            FireIfReady(engine, me.DefensiveGadget, myCastlePos);
            FireIfReady(engine, me.SignatureGadget, myCastlePos);

            float castleHpPct = me.CastleMaxHealth > 0 ? (float)me.CastleHealth / me.CastleMaxHealth : 1f;
            if (!danger && castleHpPct > 0.9f && me.Money >= me.InvestmentPrice && me.Money > me.InvestmentPrice * 3)
            {
                engine.Invest(_side);
                return;
            }

            if (castleHpPct < 0.6f && !danger && me.Money >= me.RepairPrice)
            {
                engine.Repair(_side);
                return;
            }

            UnitDefinition best = null;
            double bestScore = double.MinValue;
            foreach (var def in teamDef.Roster)
            {
                if (def.Cost > me.Money) continue;
                double dps = def.Damage * (def.AttackSpeed > 0 ? def.AttackSpeed : 0.3f);
                double score = danger ? (dps + def.MaxHealth) / System.Math.Max(1, def.Cost) : dps / System.Math.Max(1, def.Cost);
                if (score > bestScore) { bestScore = score; best = def; }
            }
            if (best != null) engine.SpawnUnit(_side, best.Id);
        }

        private void FireIfReady(GameEngine engine, GadgetDefinition def, int position)
        {
            var state = engine._state;
            var me = _side == 1 ? state.Player1 : state.Player2;
            if (def == null) return;
            if (me.GadgetCooldowns.TryGetValue(def.Id, out var cd) && cd > 0) return;
            if (me.Money < def.Cost) return;
            engine.UseGadget(_side, def.Id, position);
        }
    }
}
