using CastleDefense.Engine;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Models;

namespace CastleDefense.Simulation
{
    /// <summary>
    /// SIDE-BY-SIDE SPENDING LEDGER for a recorded game: what each player actually bought,
    /// broken down per action and per unit tier, with the unit names and prices for the team
    /// each side was playing.
    ///
    /// Money deltas are taken from the reconstruction at the tick each recorded action fires,
    /// so this is what the ENGINE charged, not what a price table says it should have cost --
    /// which matters because gadget prices change as tiers upgrade mid-game, and reading the
    /// end-of-game tier back onto early casts is exactly the mistake that produced a wrong
    /// analysis of B0589C.
    ///
    /// TWO THINGS THIS DOES NOT CAPTURE, both worth remembering before drawing conclusions:
    ///   * DELAYED PAYOUTS. The cash gadget debits its cost here and credits its payout later
    ///     through a scheduled effect, so it shows as pure spend. Its net is better than the
    ///     column suggests.
    ///   * FREE UNITS. Reinforcements spawns its units without charging for them, so the
    ///     gadget line carries the whole cost and the unit lines carry none of it.
    ///
    /// Usage: --spend-breakdown &lt;replay&gt;
    /// </summary>
    public static class SpendBreakdown
    {
        public static void Run(string path, string[] args)
        {
            var rf = ReplayFile.Read(path);
            var (state, engine) = rf.BuildStart();
            if (!rf.HasV3)
            {
                state.Player1.SetLoadout(new[] { B(rf.P1Off), B(rf.P1Def), B(rf.P1Sig) });
                state.Player2.SetLoadout(new[] { B(rf.P2Off), B(rf.P2Def), B(rf.P2Sig) });
            }

            var spend = new[] { new Dictionary<int, (double t, int n)>(),
                                new Dictionary<int, (double t, int n)>(),
                                new Dictionary<int, (double t, int n)>() };

            for (int i = 0; i < rf.TickCount; i++)
            {
                for (int side = 1; side <= 2; side++)
                {
                    byte a = side == 1 ? rf.A1[i] : rf.A2[i];
                    if (a == 0) continue;
                    var p = side == 1 ? state.Player1 : state.Player2;
                    double before = p.Money;
                    rf.ApplyRecorded(engine, side, i, a);
                    double cost = before - p.Money;
                    if (cost <= 0.001) continue;
                    var cur = spend[side].TryGetValue(a, out var v) ? v : (0.0, 0);
                    spend[side][a] = (cur.Item1 + cost, cur.Item2 + 1);
                }
                if (state.IsGameOver) break;
                engine.Tick();
            }

            var p1 = state.Player1;
            var p2 = state.Player2;
            Console.WriteLine();
            Console.WriteLine("=== SPENDING BREAKDOWN: " + rf.GameId + " ===");
            Console.WriteLine("  P1 " + p1.Team + " " + rf.P1Off + "/" + rf.P1Def + "/" + rf.P1Sig
                            + "   vs   P2 " + p2.Team + " " + rf.P2Off + "/" + rf.P2Def + "/" + rf.P2Sig);
            Console.WriteLine("  final: P1 income " + p1.Income.ToString("F1") + " invest " + p1.InvestmentCount
                            + " repairs " + p1.RepairCount
                            + "   |   P2 income " + p2.Income.ToString("F1") + " invest " + p2.InvestmentCount
                            + " repairs " + p2.RepairCount
                            + "   |   winner P" + rf.Winner);
            Console.WriteLine();
            Console.WriteLine("                                   P1 (human)              P2 (bot)");
            Console.WriteLine("    action                        n        $   share       n        $   share");
            Console.WriteLine("    " + new string('-', 76));

            double tot1 = spend[1].Values.Sum(v => v.t);
            double tot2 = spend[2].Values.Sum(v => v.t);

            foreach (int a in new[] { 9, 10, 1, 2, 3, 4, 5, 6, 7, 8, 11, 12, 13 })
            {
                bool any = spend[1].ContainsKey(a) || spend[2].ContainsKey(a);
                if (!any) continue;
                Console.WriteLine("    " + Label(a, p1, p2).PadRight(26) + Cell(spend[1], a, tot1) + Cell(spend[2], a, tot2));
            }
            Console.WriteLine("    " + new string('-', 76));
            Console.WriteLine("    " + "TOTAL SPENT".PadRight(26)
                            + ("     " + tot1.ToString("F0")).PadLeft(14) + "".PadLeft(8)
                            + ("     " + tot2.ToString("F0")).PadLeft(14));

            // The split that actually decides these games.
            double eco1 = Get(spend[1], 9) + Get(spend[1], 10);
            double eco2 = Get(spend[2], 9) + Get(spend[2], 10);
            Console.WriteLine();
            Console.WriteLine("  ECONOMY (invest + repair) vs EVERYTHING ELSE:");
            Console.WriteLine("    P1  economy $" + eco1.ToString("F0") + " (" + Pct(eco1, tot1)
                            + ")   military $" + (tot1 - eco1).ToString("F0") + " (" + Pct(tot1 - eco1, tot1) + ")");
            Console.WriteLine("    P2  economy $" + eco2.ToString("F0") + " (" + Pct(eco2, tot2)
                            + ")   military $" + (tot2 - eco2).ToString("F0") + " (" + Pct(tot2 - eco2, tot2) + ")");
        }

        private static double Get(Dictionary<int, (double t, int n)> d, int a)
            => d.TryGetValue(a, out var v) ? v.t : 0;

        private static string Pct(double part, double whole)
            => whole <= 0 ? "0%" : (100.0 * part / whole).ToString("F0") + "%";

        private static string Cell(Dictionary<int, (double t, int n)> d, int a, double tot)
        {
            if (!d.TryGetValue(a, out var v)) return "       -        -       -";
            return v.n.ToString().PadLeft(8) + ("$" + v.t.ToString("F0")).PadLeft(9)
                 + Pct(v.t, tot).PadLeft(8);
        }

        private static string Label(int a, PlayerState p1, PlayerState p2)
        {
            switch (a)
            {
                case 9: return "INVEST";
                case 10: return "repair";
                case 11: return "offence gadget";
                case 12: return "defence gadget";
                case 13: return "signature gadget";
                default:
                    // Unit names differ per team, so show both when the teams differ.
                    string n1 = UnitName(p1, a), n2 = UnitName(p2, a);
                    string nm = n1 == n2 ? n1 : n1 + "/" + n2;
                    return "T" + a + " " + nm;
            }
        }

        private static string UnitName(PlayerState p, int tier)
        {
            var roster = GameDataManager.Teams.FirstOrDefault(t => t.Color == p.Team)?.Roster;
            var d = roster?.FirstOrDefault(u => u.Tier == tier);
            return d == null ? "?" : d.Name + "($" + d.Cost + ")";
        }

        private static string B(string id) => string.IsNullOrEmpty(id) ? id : id.Split('_')[0];
    }
}
