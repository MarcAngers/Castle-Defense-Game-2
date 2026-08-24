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
    /// saving and close it out". Dissected losses showed it paying for nothing. That is not the
    /// same as it always being wrong, and deleting a mechanism because its failures are visible
    /// is how you delete the thing that was winning the other games.
    ///
    /// Replays recorded games faithfully, runs a SHADOW HeuristicBot on a clone each tick to see
    /// when the flag is up (the replay stores action ids, not reasons), and scores each attack.
    ///
    /// WHEN IS AN ATTACK OVER? Marc's definition, 2026-08-24: from the first attacking unit
    /// spawned until the last one dies. NOT a fixed window -- an earlier version used 20 seconds
    /// flat, which is arbitrary and truncates exactly the long grinding pushes that matter.
    ///
    /// With one addition he asked for: **Blue's Wave + Freeze can stall a push essentially
    /// forever without killing it**, so a unit shoved back past the bot's OWN castle wall counts
    /// as out of the fight whether it is alive or not. Without that clause a stalled attack never
    /// closes, the window runs to the end of the game, and every later purchase and repair gets
    /// attributed to it.
    ///
    /// VALUE, in dollars:
    ///   DAMAGE   -- priced at what it costs the DEFENDER to undo, using their repair count at
    ///               the moment the bot committed. That price rises ~3,230x from repair 0 to 7,
    ///               so identical HP is worth wildly different money depending on who it lands on.
    ///   OUTFLOW  -- everything they put back into the game over the window: income earned less
    ///               what stayed banked, less any investment (a choice, not a forced response).
    ///               Unit spend alone captured about a third of this.
    /// COST: what the bot spent on units while the flag was up.
    ///
    /// SHADOW SETTINGS MUST MATCH THE ARM THE GAME WAS PLAYED ON, or the shadow's state diverges
    /// from the bot that actually played and the activations are fiction.
    /// </summary>
    public static class KillerAudit
    {
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
            w.WriteLine("game,arm,result,start_s,dur_s,cost,dmg,killed,foe_unit_spend,"
                      + "own_units,foe_units,foe_hp,foe_hp_pct,foe_maxhp,bot_money,bot_inv,foe_rc,"
                      + "push_dps,kill_secs,foe_repairs,foe_repair_spend,foe_outflow,dmg_value,"
                      + "attack_units,ended_by");

            int done = 0, acts = 0;
            foreach (var (id, arm) in games)
            {
                string path = Path.Combine(dir, id + ".replay");
                if (!File.Exists(path)) continue;
                if (!arm.StartsWith("heuristic")) continue;   // search-arm games had a different bot
                acts += Audit(path, id, arm, w);
                done++;
            }
            Console.WriteLine($"\naudited {done} games, {acts} attacks -> {Path.GetFullPath(outPath)}");
        }

        private static HeuristicBotSettings SettingsFor(string arm) => arm switch
        {
            "heuristic_repairfix"     => HeuristicBotSettings.RepairFixProfile,
            "heuristic_repair_hazard" => HeuristicBotSettings.RepairFixPlusHazardProfile,
            "heuristic_brake"         => HeuristicBotSettings.EconomyBrakeProfile,
            _                         => null,   // "heuristic" -- the flagship
        };

        private static double RepairPrice(int c)
        {
            double p = Math.Exp(0.0109 * c * c * c + 0.0011 * c * c + 0.4351 * c + 0.5268)
                     * (c * 5 + 5);
            return c >= 8 ? p * 2 : p;
        }

        /// <summary>HP one repair buys -- PreviewRepairStep: max rises 11,000, heals 20 points of the new max.</summary>
        private static double RepairHp(int c, double pct) => pct * 11000 + 0.2 * (1000 + 11000 * (c + 1));

        /// <summary>
        /// A bot unit is still in the fight if it is alive AND has not been shoved back past its
        /// own castle wall. The second clause is what closes a Wave-stalled push.
        ///
        /// THE SPAWN POINT IS ALREADY BEHIND THE WALL. GameEngine spawns side 2 at
        /// MAP_WIDTH - 100 = 1900, and P2_CASTLE_WALL is 1800, so a naive `Position >= wall`
        /// test fires on the tick the unit appears and every attack closes instantly (measured:
        /// median duration 0.2s, which is what caught it). A unit therefore has to COMMIT first
        /// -- advance clear of the spawn zone -- before being pushed back means anything.
        /// </summary>
        private const float CommitLine = 1700f;

        private static bool StillAttacking(Unit u, HashSet<Guid> committed)
        {
            if (u.CurrentHealth <= 0) return false;
            if (u.Position < CommitLine) { committed.Add(u.InstanceId); return true; }
            // Not yet advanced: still forming up, not pushed back.
            if (!committed.Contains(u.InstanceId)) return true;
            return u.Position < GameEngine.P2_CASTLE_WALL;
        }

        private static int Audit(string path, string id, string arm, StreamWriter w)
        {
            var rf = ReplayFile.Read(path);
            var (state, engine) = rf.BuildStart();
            var shadow = new HeuristicBot(2, SettingsFor(arm));

            var committed = new HashSet<Guid>();
            var open = new List<Attack>();
            var closed = new List<Attack>();
            bool live = false;
            Attack cur = null;

            for (int i = 0; i < rf.TickCount && !state.IsGameOver; i++)
            {
                int tick = (int)state.CurrentTick;
                double sec = tick / 30.0;

                var probe = engine.Clone(unchecked(tick));
                shadow.Update(probe);
                bool ki = shadow.LastKillerInstinctRaw;

                if (ki && !live)
                {
                    live = true;
                    cur = new Attack
                    {
                        Start = sec,
                        End = sec,
                        Spend0 = engine.MoneySpentOnUnits[2],
                        FoeUnitSpend0 = engine.MoneySpentOnUnits[1],
                        FoeHp0 = state.Player1.CastleHealth,
                        FoeMax0 = state.Player1.CastleMaxHealth,
                        FoeMoney0 = state.Player1.Money,
                        FoeInv0 = state.Player1.InvestmentCount,
                        FoeIncome = state.Player1.Income,
                        Own = state.Units.Count(u => u.Side == 2),
                        Foe = state.Units.Count(u => u.Side == 1),
                        Money = state.Player2.Money,
                        Inv = state.Player2.InvestmentCount,
                        Dps = shadow.LastOwnPushDps,
                        KSec = shadow.LastKillerSeconds,
                        FoeRc0 = (int)Math.Round((state.Player1.CastleMaxHealth - 1000) / 11000.0),
                        LastFoeHp = state.Player1.CastleHealth,
                        LastFoeMax = state.Player1.CastleMaxHealth,
                    };
                    open.Add(cur);
                }
                else if (!ki && live)
                {
                    live = false;
                    if (cur != null) cur.Committing = false;
                    cur = null;
                }

                var before = new HashSet<Guid>(
                    state.Units.Where(u => u.Side == 2).Select(u => u.InstanceId));

                byte a1 = rf.A1[i], a2 = rf.A2[i];
                if (a1 != 0) rf.ApplyRecorded(engine, 1, tick, a1);
                if (a2 != 0) rf.ApplyRecorded(engine, 2, tick, a2);

                // Units bought while the flag is up ARE the attack.
                if (live && cur != null)
                {
                    cur.Spend = engine.MoneySpentOnUnits[2] - cur.Spend0;
                    foreach (var u in state.Units)
                        if (u.Side == 2 && !before.Contains(u.InstanceId))
                            cur.Units.Add(u.InstanceId);
                }

                engine.Tick();

                foreach (var atk in open)
                {
                    double hpNow = state.Player1.CastleHealth;
                    double maxNow = state.Player1.CastleMaxHealth;
                    if (maxNow > atk.LastFoeMax)
                    {
                        atk.FoeRepairs++;
                        atk.FoeRepairSpend += RepairPrice(
                            (int)Math.Round((atk.LastFoeMax - 1000) / 11000.0));
                    }
                    else if (hpNow < atk.LastFoeHp) atk.Damage += atk.LastFoeHp - hpNow;
                    atk.LastFoeHp = hpNow;
                    atk.LastFoeMax = maxNow;
                    atk.End = state.CurrentTick / 30.0;
                    atk.FoeMoney1 = state.Player1.Money;
                    atk.FoeInv1 = state.Player1.InvestmentCount;
                    atk.FoeUnitSpend = engine.MoneySpentOnUnits[1] - atk.FoeUnitSpend0;
                }

                foreach (var atk in open.ToList())
                {
                    if (atk.Committing || atk.Units.Count == 0) continue;
                    int alive = 0, pushed = 0;
                    foreach (var u in state.Units)
                        if (u.Side == 2 && atk.Units.Contains(u.InstanceId))
                        {
                            if (StillAttacking(u, committed)) alive++;
                            else if (u.CurrentHealth > 0) pushed++;
                        }
                    if (alive == 0)
                    {
                        atk.EndedBy = pushed > 0 ? "pushed-back" : "all-dead";
                        closed.Add(atk);
                        open.Remove(atk);
                    }
                }
            }
            foreach (var atk in open) { atk.EndedBy = "game-over"; closed.Add(atk); }

            string result = rf.Winner == 2 ? "BOT" : rf.Winner == 1 ? "MARC" : "draw";
            int n = 0;
            var durs = new List<double>();
            foreach (var p in closed)
            {
                if (p.Spend <= 0) continue;
                double dur = p.End - p.Start;
                double invested = 0;
                for (int c = p.FoeInv0; c < p.FoeInv1; c++) invested += p.FoeIncome * (c * 4 + 8);
                double outflow = Math.Max(0,
                    p.FoeMoney0 + p.FoeIncome * dur - p.FoeMoney1 - invested);
                double pct = p.FoeMax0 > 0 ? p.FoeHp0 / p.FoeMax0 : 1;
                double dmgValue = p.Damage * RepairPrice(p.FoeRc0)
                                / Math.Max(1, RepairHp(p.FoeRc0, pct));
                bool killed = p.LastFoeHp <= 0 && rf.Winner == 2;
                durs.Add(dur);
                w.WriteLine($"{id},{arm},{result},{p.Start:F1},{dur:F1},{p.Spend:F0},{p.Damage:F0},"
                          + $"{(killed ? 1 : 0)},{p.FoeUnitSpend:F0},{p.Own},{p.Foe},{p.FoeHp0:F0},"
                          + $"{100 * pct:F0},{p.FoeMax0:F0},{p.Money:F0},{p.Inv},{p.FoeRc0},"
                          + $"{p.Dps:F0},{(float.IsPositiveInfinity(p.KSec) ? 9999 : p.KSec):F1},"
                          + $"{p.FoeRepairs},{p.FoeRepairSpend:F0},{outflow:F0},{dmgValue:F0},"
                          + $"{p.Units.Count},{p.EndedBy}");
                n++;
            }
            durs.Sort();
            Console.WriteLine($"  {id} [{arm}] {result,-4} {n} attacks"
                            + (n > 0 ? $"   median {durs[n / 2]:F0}s" : ""));
            return n;
        }

        private sealed class Attack
        {
            public double Start, End, Spend0, Spend, FoeHp0, FoeMax0, FoeMoney0, FoeMoney1;
            public double FoeIncome, Damage, FoeRepairSpend, LastFoeHp, LastFoeMax;
            public double FoeUnitSpend0, FoeUnitSpend, Money;
            public int Own, Foe, Inv, FoeInv0, FoeInv1, FoeRc0, FoeRepairs;
            public float Dps, KSec;
            public bool Committing = true;
            public string EndedBy = "?";
            public HashSet<Guid> Units = new();
        }

        private static string Arg(string[] a, string n, string f)
        {
            int i = Array.IndexOf(a, n);
            return (i >= 0 && i + 1 < a.Length) ? a[i + 1] : f;
        }
    }
}
