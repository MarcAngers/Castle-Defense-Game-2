using CastleDefense.Engine;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Models;

namespace CastleDefense.Simulation
{
    /// <summary>
    /// WOULD A VALUE GATE HAVE REFUSED THESE REPAIRS? A diagnostic run BEFORE building one,
    /// because every attempt so far to make the bot repair less has measured neutral or
    /// negative, and the case that repair is a leak rests on a single game.
    ///
    /// At each recorded repair it prices both sides of the trade in seconds, which is the
    /// framing the proposed gate would use:
    ///
    ///   BOUGHT  = hpGained / threatDps            -- extra seconds of life
    ///   COST    = min(RepairPrice / Income, ttd)  -- seconds of income surrendered, capped
    ///             at remaining life because a rung you do not live to collect is worth zero
    ///   verdict = BOUGHT >= COST * margin
    ///
    /// TWO ESTIMATOR CAVEATS, stated because the verdict depends on which one is used:
    ///   * threatDps and projTtd mirror HeuristicBot.EstimateProjectedThreatDps -- only units
    ///     already in CONTACT with the castle count, which is the same contact test the
    ///     engine uses before it damages a castle (MoveAndFight).
    ///   * geoTtd is GameState.TimeToCastleDeathSeconds, the oracle-checked geometric
    ///     estimator that also prices units still walking in. It is NOT what the bot's own
    ///     timeToDeathSeconds uses (that is max(observed-drain, projected)), so treat these
    ///     as bracketing the bot's real number rather than reproducing it.
    ///
    /// Usage: --repair-audit &lt;replay&gt; [--side 2] [--margin X]
    /// </summary>
    public static class RepairAudit
    {
        public static void Run(string path, string[] args)
        {
            int side = 2;
            double margin = 1.0;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--side" && i + 1 < args.Length) side = int.Parse(args[++i]);
                if (args[i] == "--margin" && i + 1 < args.Length) margin = double.Parse(args[++i]);
            }

            var rf = ReplayFile.Read(path);
            var (state, engine) = rf.BuildStart();
            if (!rf.HasV3)
            {
                state.Player1.SetLoadout(new[] { B(rf.P1Off), B(rf.P1Def), B(rf.P1Sig) });
                state.Player2.SetLoadout(new[] { B(rf.P2Off), B(rf.P2Def), B(rf.P2Sig) });
            }
            var me = side == 1 ? state.Player1 : state.Player2;
            var acts = side == 1 ? rf.A1 : rf.A2;

            Console.WriteLine();
            Console.WriteLine("=== REPAIR AUDIT: " + rf.GameId + ", P" + side + "  (margin " + margin + ") ===");
            Console.WriteLine();
            Console.WriteLine("   #  tick   sec   price  cost_s  hpGain  enemyDps  projTtd  geoTtd  bought_s   verdict");
            Console.WriteLine("   " + new string('-', 92));

            int n = 0;
            int refused = 0, accepted = 0;
            for (int i = 0; i < rf.TickCount; i++)
            {
                if (acts[i] == 10)
                {
                    n++;
                    var (nextHealth, _) = me.PreviewRepairStep();
                    double hpGain = nextHealth - me.CastleHealth;
                    double dps = ThreatDps(engine, state, side);
                    double projTtd = dps > 0.01 ? me.CastleHealth / dps : double.PositiveInfinity;
                    double geoTtd = state.TimeToCastleDeathSeconds(side);
                    double bought = dps > 0.01 ? hpGain / dps : double.PositiveInfinity;
                    double rawCost = me.Income > 0.01 ? me.RepairPrice / me.Income : double.PositiveInfinity;
                    double ttdForCap = Math.Min(projTtd, geoTtd);
                    double cost = Math.Min(rawCost, ttdForCap);
                    bool ok = bought >= cost * margin;
                    if (ok) accepted++; else refused++;

                    Console.WriteLine("   " + n.ToString().PadLeft(2)
                        + i.ToString().PadLeft(6)
                        + (i / 30).ToString().PadLeft(6)
                        + me.RepairPrice.ToString("F0").PadLeft(8)
                        + Fmt(rawCost).PadLeft(8)
                        + hpGain.ToString("F0").PadLeft(8)
                        + dps.ToString("F0").PadLeft(10)
                        + Fmt(projTtd).PadLeft(9)
                        + Fmt(geoTtd).PadLeft(8)
                        + Fmt(bought).PadLeft(10)
                        + ("   " + (ok ? "ACCEPT" : "REFUSE") + "  (cost capped to " + Fmt(cost) + ")"));
                }
                for (int s2 = 1; s2 <= 2; s2++)
                {
                    byte a = s2 == 1 ? rf.A1[i] : rf.A2[i];
                    if (a != 0) rf.ApplyRecorded(engine, s2, i, a);
                }
                if (state.IsGameOver) break;
                engine.Tick();
            }

            Console.WriteLine();
            Console.WriteLine("   " + n + " repairs: the proposed gate would ACCEPT " + accepted
                            + ", REFUSE " + refused + " at margin " + margin);
            if (refused == 0)
                Console.WriteLine("   -> No repair is refused. A value gate would change nothing here and repair is"
                                + " NOT the leak; drop this thread.");
        }

        /// <summary>Mirrors HeuristicBot.EstimateProjectedThreatDps: units in CONTACT only.</summary>
        private static double ThreatDps(GameEngine engine, GameState state, int side)
        {
            var enemyState = side == 1 ? state.Player2 : state.Player1;
            var roster = GameDataManager.Teams.FirstOrDefault(t => t.Color == enemyState.Team)?.Roster;
            if (roster == null) return 0;
            double dps = 0;
            foreach (var u in state.Units)
            {
                if (u.Side == side || u.CurrentHealth <= 0) continue;
                var def = roster.FirstOrDefault(d => d.Id == u.DefinitionId);
                if (def == null) continue;
                if (engine.GetDistanceToEnemyCastle(u) <= def.Range)
                    dps += (double)def.Damage * def.AttackSpeed;
            }
            return dps;
        }

        private static string Fmt(double v)
            => double.IsInfinity(v) || v > 9999 ? "inf" : v.ToString("F1");

        private static string B(string id) => string.IsNullOrEmpty(id) ? id : id.Split('_')[0];
    }
}
