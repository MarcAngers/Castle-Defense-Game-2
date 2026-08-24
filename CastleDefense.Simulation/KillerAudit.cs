using CastleDefense.Engine;
using CastleDefense.Engine.Bot;
using CastleDefense.Engine.Models;

namespace CastleDefense.Simulation
{
    /// <summary>
    /// What is a `killerInstinct` activation actually WORTH?
    ///
    /// The flag is a deliberate bypass around the attack flow allowance, the gadget reserve and
    /// the disengage system all at once, on the argument "we are N seconds from winning, stop
    /// saving and close it out". Three dissected losses showed it paying for nothing. That is not
    /// the same as it always being wrong, and removing a mechanism because its failures are
    /// visible is how you delete the thing that was winning the other games.
    ///
    /// So: replay recorded games faithfully, run a SHADOW HeuristicBot on a clone each tick to
    /// see when the flag is up (the replay stores action ids, not reasons), and score each
    /// activation on the two things Marc named as value:
    ///
    ///   1. DAMAGE  -- enemy castle HP removed while the activation runs and shortly after,
    ///                 with the kill as the best case.
    ///   2. RESPONSE-- what the opponent had to spend to answer it. An attack that costs the
    ///                 defender more than it cost to launch is profitable even with no damage.
    ///
    /// Against the cost: money the bot spent on units during the activation.
    ///
    /// SHADOW SETTINGS MUST MATCH THE ARM THE GAME WAS PLAYED ON, or the shadow's internal state
    /// diverges from the bot that actually played and the activations are fiction. Pass --arm.
    /// </summary>
    public static class KillerAudit
    {
        // How long after an activation ends to keep attributing damage and response spend.
        private const double WindowSeconds = 20.0;

        public static void Run(string[] args)
        {
            string dir = Arg(args, "--dir", "CastleDefenseGame2/recordings/singleplayer");
            string list = Arg(args, "--list", null);
            string outPath = Arg(args, "--csv", "killer_audit.csv");

            var games = new List<(string id, string arm)>();
            if (list != null)
                foreach (var line in File.ReadAllLines(list))
                {
                    var p = line.Split(',');
                    if (p.Length >= 4) games.Add((p[0], p[3]));
                }

            using var w = new StreamWriter(outPath);
            w.WriteLine("game,arm,result,start_s,dur_s,cost,dmg,killed,response_spend,"
                      + "own_units,foe_units,foe_hp,foe_hp_pct,foe_maxhp,bot_money,bot_inv,foe_inv");

            int done = 0, acts = 0;
            foreach (var (id, arm) in games)
            {
                string path = Path.Combine(dir, id + ".replay");
                if (!File.Exists(path)) continue;
                if (!arm.StartsWith("heuristic")) continue;   // search-arm games had a different bot
                acts += Audit(path, id, arm, w);
                done++;
            }
            Console.WriteLine($"\naudited {done} games, {acts} killerInstinct activations -> {Path.GetFullPath(outPath)}");
        }

        private static HeuristicBotSettings SettingsFor(string arm) => arm switch
        {
            "heuristic_repairfix"      => HeuristicBotSettings.RepairFixProfile,
            "heuristic_repair_hazard"  => HeuristicBotSettings.RepairFixPlusHazardProfile,
            "heuristic_brake"          => HeuristicBotSettings.EconomyBrakeProfile,
            _                          => null,   // "heuristic" -- the flagship
        };

        private static int Audit(string path, string id, string arm, StreamWriter w)
        {
            var rf = ReplayFile.Read(path);
            var (state, engine) = rf.BuildStart();
            var shadow = new HeuristicBot(2, SettingsFor(arm));

            // One row per contiguous activation.
            bool live = false;
            double startT = 0, startSpend = 0, startFoeHp = 0, startMarcSpend = 0;
            int sOwn = 0, sFoe = 0, sInv = 0, sFoeInv = 0;
            double sMoney = 0, sMaxHp = 0;
            var pending = new List<(double start, double dur, double cost, double hp0,
                                   double marcSpend0, int own, int foe, double maxhp,
                                   double money, int inv, int foeInv)>();

            for (int i = 0; i < rf.TickCount && !state.IsGameOver; i++)
            {
                int tick = (int)state.CurrentTick;
                double sec = tick / 30.0;

                // Shadow BEFORE the recorded actions -- it must see the state the bot decided on.
                var probe = engine.Clone(unchecked(tick));
                shadow.Update(probe);
                bool ki = shadow.LastKillerInstinctRaw;

                if (ki && !live)
                {
                    live = true; startT = sec;
                    startSpend = engine.MoneySpentOnUnits[2];
                    startMarcSpend = engine.MoneySpentOnUnits[1];
                    startFoeHp = state.Player1.CastleHealth;
                    sMaxHp = state.Player1.CastleMaxHealth;
                    sOwn = state.Units.Count(u => u.Side == 2);
                    sFoe = state.Units.Count(u => u.Side == 1);
                    sMoney = state.Player2.Money;
                    sInv = state.Player2.InvestmentCount;
                    sFoeInv = state.Player1.InvestmentCount;
                }
                else if (!ki && live)
                {
                    live = false;
                    pending.Add((startT, sec - startT,
                                 engine.MoneySpentOnUnits[2] - startSpend,
                                 startFoeHp, startMarcSpend, sOwn, sFoe, sMaxHp, sMoney, sInv, sFoeInv));
                }

                byte a1 = rf.A1[i], a2 = rf.A2[i];
                if (a1 != 0) rf.ApplyRecorded(engine, 1, tick, a1);
                if (a2 != 0) rf.ApplyRecorded(engine, 2, tick, a2);
                engine.Tick();
            }
            if (live)
                pending.Add((startT, state.CurrentTick / 30.0 - startT,
                             engine.MoneySpentOnUnits[2] - startSpend,
                             startFoeHp, startMarcSpend, sOwn, sFoe, sMaxHp, sMoney, sInv, sFoeInv));

            // Second pass: replay again to read the outcome window after each activation.
            var (s2, e2) = rf.BuildStart();
            var hp = new List<(double t, double foeHp, double marcSpend, bool over)>();
            for (int i = 0; i < rf.TickCount && !s2.IsGameOver; i++)
            {
                int tick = (int)s2.CurrentTick;
                byte a1 = rf.A1[i], a2 = rf.A2[i];
                if (a1 != 0) rf.ApplyRecorded(e2, 1, tick, a1);
                if (a2 != 0) rf.ApplyRecorded(e2, 2, tick, a2);
                e2.Tick();
                hp.Add((s2.CurrentTick / 30.0, s2.Player1.CastleHealth,
                        e2.MoneySpentOnUnits[1], s2.IsGameOver));
            }
            double EndHp(double t)
            {
                double best = hp[^1].foeHp;
                foreach (var h in hp) if (h.t >= t) { best = h.foeHp; break; }
                return best;
            }
            double EndSpend(double t)
            {
                double best = hp[^1].marcSpend;
                foreach (var h in hp) if (h.t >= t) { best = h.marcSpend; break; }
                return best;
            }

            string result = rf.Winner == 2 ? "BOT" : rf.Winner == 1 ? "MARC" : "draw";
            foreach (var p in pending)
            {
                double endT = p.start + p.dur + WindowSeconds;
                // Castle damage is the DROP only -- a repair in the window must not be scored
                // as the bot healing the enemy.
                double dmg = Math.Max(0, p.hp0 - EndHp(endT));
                bool killed = EndHp(endT) <= 0 && rf.Winner == 2;
                double response = Math.Max(0, EndSpend(endT) - p.marcSpend0);
                w.WriteLine($"{id},{arm},{result},{p.start:F1},{p.dur:F1},{p.cost:F0},{dmg:F0},"
                          + $"{(killed ? 1 : 0)},{response:F0},{p.own},{p.foe},{p.hp0:F0},"
                          + $"{(p.maxhp > 0 ? 100 * p.hp0 / p.maxhp : 0):F0},{p.maxhp:F0},"
                          + $"{p.money:F0},{p.inv},{p.foeInv}");
            }
            Console.WriteLine($"  {id} [{arm}] {result,-4} {pending.Count} activations");
            return pending.Count;
        }

        private static string Arg(string[] a, string n, string f)
        {
            int i = Array.IndexOf(a, n);
            return (i >= 0 && i + 1 < a.Length) ? a[i + 1] : f;
        }
    }
}
