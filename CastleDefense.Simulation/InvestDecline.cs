using CastleDefense.Engine;
using CastleDefense.Engine.Models;

namespace CastleDefense.Simulation
{
    /// <summary>
    /// HOW OFTEN COULD A PLAYER HAVE INVESTED, AND WHAT DID THEY BUY INSTEAD?
    ///
    /// Replays the recorded action stream (so the actions are ground truth, not simulated
    /// policy) and tracks, tick by tick, whether the next investment rung was AFFORDABLE.
    /// Every window where money >= InvestmentPrice is an opportunity the player declined for
    /// as long as the window stayed open, and every purchase made inside such a window is
    /// money that was spent with the rung already within reach.
    ///
    /// This leans on --replay-fidelity's finding for B0589C: 0 of 56 recorded actions failed
    /// in the rebuild, so the ECONOMY tracks even though combat drifts. Money and
    /// InvestmentPrice are therefore trustworthy here in a way that unit positions are not.
    ///
    /// Usage: --invest-decline &lt;replay&gt; [--side 2] [--from N] [--to N]
    /// </summary>
    public static class InvestDecline
    {
        public static void Run(string path, string[] args)
        {
            int side = 2;
            long from = 0, to = long.MaxValue;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--side" && i + 1 < args.Length) side = int.Parse(args[++i]);
                if (args[i] == "--from" && i + 1 < args.Length) from = long.Parse(args[++i]);
                if (args[i] == "--to" && i + 1 < args.Length) to = long.Parse(args[++i]);
            }

            var rf = ReplayFile.Read(path);
            var (state, engine) = rf.BuildStart();
            // v3 BuildStart already equips the true start loadout; only v2 needs the guess.
            if (!rf.HasV3)
            {
                state.Player1.SetLoadout(new[] { B(rf.P1Off), B(rf.P1Def), B(rf.P1Sig) });
                state.Player2.SetLoadout(new[] { B(rf.P2Off), B(rf.P2Def), B(rf.P2Sig) });
            }
            var me = side == 1 ? state.Player1 : state.Player2;
            var acts = side == 1 ? rf.A1 : rf.A2;

            Console.WriteLine();
            Console.WriteLine("=== INVESTMENT OPPORTUNITIES DECLINED: " + rf.GameId + ", P" + side + " ===");
            Console.WriteLine("  window ticks " + from + " to " + (to == long.MaxValue ? rf.TickCount : to));
            Console.WriteLine();

            var spend = new Dictionary<int, (double total, int count)>();
            int openTick = -1;
            double peak = 0;
            int windows = 0, declinedDecisions = 0;
            double spentWhileAffordable = 0;
            long ticksAffordable = 0;
            var lines = new List<string>();

            for (int i = 0; i < rf.TickCount; i++)
            {
                bool inWindow = state.CurrentTick >= from && state.CurrentTick <= to;
                double before = me.Money;

                if (acts[i] != 0)
                {
                    bool affordableNow = me.Money >= me.InvestmentPrice;
                    rf.ApplyRecorded(engine, side, i, acts[i]);
                    double cost = before - me.Money;
                    if (inWindow && cost > 0.001)
                    {
                        var cur = spend.TryGetValue(acts[i], out var v) ? v : (0.0, 0);
                        spend[acts[i]] = (cur.Item1 + cost, cur.Item2 + 1);
                        if (affordableNow && acts[i] != 9)
                        {
                            declinedDecisions++;
                            spentWhileAffordable += cost;
                            lines.Add("    tick " + state.CurrentTick.ToString().PadLeft(5)
                                + " (" + (state.CurrentTick / 30).ToString().PadLeft(3) + "s)  had $"
                                + before.ToString("F0").PadLeft(6) + "  rung cost $"
                                + me.InvestmentPrice.ToString("F0").PadLeft(6)
                                + "   spent $" + cost.ToString("F0").PadLeft(5)
                                + " on " + Name(acts[i]));
                        }
                    }
                }
                // The other side's actions still have to run or the game is not the same game.
                var other = side == 1 ? rf.A2 : rf.A1;
                if (other[i] != 0) rf.ApplyRecorded(engine, side == 1 ? 2 : 1, i, other[i]);

                if (inWindow)
                {
                    bool affordable = me.Money >= me.InvestmentPrice;
                    if (affordable)
                    {
                        ticksAffordable++;
                        if (openTick < 0) { openTick = (int)state.CurrentTick; peak = me.Money; windows++; }
                        if (me.Money > peak) peak = me.Money;
                    }
                    else if (openTick >= 0)
                    {
                        lines.Add("    WINDOW  ticks " + openTick + "-" + state.CurrentTick
                                + "  (" + ((state.CurrentTick - openTick) / 30.0).ToString("F1")
                                + "s affordable, peak $" + peak.ToString("F0") + ")");
                        openTick = -1;
                    }
                }

                if (state.IsGameOver) break;
                engine.Tick();
            }
            if (openTick >= 0)
                lines.Add("    WINDOW  ticks " + openTick + "-" + state.CurrentTick
                        + "  (" + ((state.CurrentTick - openTick) / 30.0).ToString("F1")
                        + "s affordable, peak $" + peak.ToString("F0") + ", still open at the end)");

            foreach (var l in lines) Console.WriteLine(l);

            Console.WriteLine();
            Console.WriteLine("  SUMMARY");
            Console.WriteLine("    investment rung was affordable for      : " + ticksAffordable + " ticks ("
                            + (ticksAffordable / 30.0).ToString("F1") + "s) across " + windows + " window(s)");
            Console.WriteLine("    purchases made while it WAS affordable  : " + declinedDecisions
                            + "  totalling $" + spentWhileAffordable.ToString("F0"));
            Console.WriteLine("    final investment count / price          : " + me.InvestmentCount
                            + " / $" + me.InvestmentPrice.ToString("F0")
                            + "   money at end $" + me.Money.ToString("F0"));
            Console.WriteLine();
            Console.WriteLine("  WHERE THE MONEY WENT in this window:");
            double tot = 0;
            foreach (var kv in spend.OrderByDescending(k => k.Value.total))
            {
                tot += kv.Value.total;
                Console.WriteLine("    " + Name(kv.Key).PadRight(26) + kv.Value.count.ToString().PadLeft(3)
                                + " x   $" + kv.Value.total.ToString("F0").PadLeft(6));
            }
            Console.WriteLine("    " + "TOTAL".PadRight(26) + "      $" + tot.ToString("F0").PadLeft(6));
        }

        private static string Name(int a)
        {
            switch (a)
            {
                case 9: return "INVEST";
                case 10: return "repair";
                case 11: return "offence gadget";
                case 12: return "defence gadget";
                case 13: return "signature gadget";
                default: return "spawn tier " + a;
            }
        }

        private static string B(string id) => string.IsNullOrEmpty(id) ? id : id.Split('_')[0];
    }
}
