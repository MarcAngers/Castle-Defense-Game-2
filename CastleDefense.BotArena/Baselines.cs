using CastleDefense.Engine;
using CastleDefense.Engine.Bot;
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

    // Mimics a human who read that "investing is good" and saves up for it,
    // spending the remainder on the single best-value affordable unit. No gadgets.
    //
    // FIXED 2026-07-28 -- this bot had never invested once, in any benchmark it has
    // ever appeared in. It checked `Money >= InvestmentPrice` but spent everything it
    // had on units at every one of its 5-tick decisions. Income starts at 2 per 30
    // ticks and the cheapest unit costs 2, so money oscillated 0->2->0 and could never
    // climb to the first InvestmentPrice of 18. That made it behaviourally IDENTICAL
    // to RusherBaseline, BalancedHumanBaseline and Tier1Spam -- all four spawn the same
    // tier-1 unit on the same ticks, so they produced byte-identical results and three
    // of the four ladder rungs were measuring the same thing. Found by the `ladder`
    // mode; see TRAINING_CAMPAIGN_LOG.md.
    //
    // The fix is the saving discipline the bot's own description implies: don't spend
    // while accumulating toward the next investment. That is also exactly the
    // behaviour the RL policy has never learned, which makes this a meaningful rung.
    public class InvestorBaseline : IArenaOpponent
    {
        private readonly int _side;
        private const int IntervalTicks = 5;
        // Economic opening only. The first attempt at this fix used 6 and the bot never
        // got there — it saved for the entire game, bought nothing, and came out
        // byte-identical to DoNothingBaseline. Saving and fighting genuinely compete in
        // this economy, so the saving phase has to be short and it has to yield to a
        // real threat.
        private const int SaveUntilInvestCount = 3;
        private const float ThreatRange = 600f;
        private long _next;

        public InvestorBaseline(int side) => _side = side;

        public void Update(GameEngine engine)
        {
            var state = engine._state;
            if (state.IsGameOver || state.CurrentTick < _next) return;
            _next = state.CurrentTick + IntervalTicks;

            var me = _side == 1 ? state.Player1 : state.Player2;

            // Only give up the turn if the buy actually landed. Invest returns false once
            // ARMAGEDDON has been spent, and InvestmentPrice stays where it was, so
            // `money >= price` remains true forever afterwards -- returning regardless
            // would leave the bot doing nothing at all for the rest of the game.
            if (me.Money >= me.InvestmentPrice && engine.Invest(_side))
            {
                return;
            }

            // Save through the opening — but never while something is walking at the
            // castle, or this degenerates into a bot that dies rich.
            int myCastlePos = _side == 1 ? 200 : GameEngine.MAP_WIDTH - 200;
            bool threatened = state.Units.Any(u => u.Side != _side &&
                                                   System.Math.Abs(u.Position - myCastlePos) < ThreatRange);
            if (!threatened && me.InvestmentCount < SaveUntilInvestCount) return;

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

    // The one ladder rung that is not a spam bot, an Investor, or HeuristicBot in a hat.
    // Plays a behaviour clone fitted to Marc's 141 recorded games — see
    // CastleDefense.Engine.Bot.HumanCloneBot and PolicyTableExport for what the table can
    // and cannot represent. Deliberately not strong; the point is that it spends money
    // where a person spends it, so a change that helps here is not just better
    // HeuristicBot-exploitation.
    public class HumanCloneBaseline : IArenaOpponent
    {
        private readonly HumanCloneBot _bot;
        public HumanCloneBaseline(int side) => _bot = new HumanCloneBot(side);
        public void Update(GameEngine engine) => _bot.Update(engine);
        public long[] ActionCounts => _bot.ActionCounts;
    }

    /// <summary>
    /// <summary>
    /// Plain stock HeuristicBot as an ARENA OPPONENT, so the dashboard can sweep the
    /// mirror.
    ///
    /// WHY THIS WAS MISSING AND WHY IT MATTERS. Until 2026-08-23 the dashboard's only mirror
    /// arm was `SearchMirror`, which is off by default AND uses RolloutSearchBot -- so a
    /// `--bot heuristic` sweep had no mirror at all. Every one of its 23 opponents was a spam
    /// tier or an ONNX checkpoint, i.e. an opponent HeuristicBot beats 93-99% of the time, and
    /// against that spread a genuine team or gadget edge is swamped by the opponent simply
    /// being bad. The mirror is the one arm where an edge shows up as an edge.
    ///
    /// The dashboard alternates which physical seat the protagonist takes, which this arm
    /// needs more than any other: a near-mirror is decided by the seat rather than the play
    /// (see the seat-bias entry in CLAUDE.md), so an unbalanced mirror measurement is worthless.
    /// The opponent still receives a RANDOM loadout via AssignRandomLoadout, so this is
    /// "this kit against a competent bot playing anything", not a loadout-vs-loadout cell --
    /// counter-matrix is the tool for that.
    /// </summary>
    public class HeuristicBaseline : IArenaOpponent
    {
        private readonly HeuristicBot _bot;
        public HeuristicBaseline(int side) => _bot = new HeuristicBot(side);
        public void Update(GameEngine engine) => _bot.Update(engine);
    }

    /// HeuristicBot plus the scripted save-invest commitment (see SavingHeuristicBot).
    ///
    /// A ladder CONTENDER rather than a rung. Probe A only means anything if this policy is
    /// genuinely stronger than HeuristicBot — a "stronger rollout policy" that is not
    /// actually stronger tests nothing. The ladder is the instrument that answers that:
    /// paired setups, side-swapped, with a confidence interval.
    /// </summary>
    public class SavingHeuristicBaseline : IArenaOpponent
    {
        private readonly SavingHeuristicBot _bot;
        public SavingHeuristicBaseline(int side, double commitFraction = 0.5)
            => _bot = new SavingHeuristicBot(side, commitFraction);
        public void Update(GameEngine engine) => _bot.Update(engine);
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

            // FIXED 2026-07-28 -- the old gate also required `Money > InvestmentPrice * 3`,
            // which was unreachable for the same reason InvestorBaseline never invested
            // (spends every 2 gold at each 5-tick decision, so money never accumulates).
            // This bot is described as "the toughest baseline" but was in practice just
            // another tier-1 spammer, identical to Rusher and Tier1Spam. Now it saves
            // toward the investment when it is safe to do so, which is what "invests
            // opportunistically when flush and safe" was always supposed to mean.
            bool safeToInvest = !danger && castleHpPct > 0.9f;
            // See the note on the other Invest callsite: bail out only on a successful buy,
            // or a post-ARMAGEDDON bot stalls permanently.
            if (safeToInvest && me.Money >= me.InvestmentPrice && engine.Invest(_side))
            {
                return;
            }
            // Hold the purse while saving, but only through a brief opening and only
            // while genuinely out of trouble. The first version of this used
            // `InvestmentCount < 4` and made the bot markedly WORSE (HeuristicBot went
            // from beating it 85% to 100%) — it spent the game holding money it never
            // managed to convert. Two investments is enough to make it economically
            // distinct from the pure spammers without gutting its defence.
            if (safeToInvest && me.InvestmentCount < 2) return;

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
