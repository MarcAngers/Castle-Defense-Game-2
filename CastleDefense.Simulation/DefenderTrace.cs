using CastleDefense.Engine;
using CastleDefense.Engine.Bot;
using CastleDefense.Engine.Models;

namespace CastleDefense.Simulation
{
    /// <summary>
    /// WHAT THE SIMULATED OPPONENT DOES ABOUT AN INCOMING UNIT, AND WHY.
    ///
    /// Marc's hypothesis, 2026-08-20: search likes spawning a unit because in its ROLLOUT the
    /// opponent is HeuristicBot, which is too attached to its castle HP and answers the spawn
    /// by spending. If that response delays the OPPONENT's next investment by more than the
    /// spawn delayed OUR own, the spawn looks profitable in simulation while being worthless
    /// against a human who simply tanks the chip damage and keeps saving.
    ///
    /// This runs the same position twice from the same tick -- once with our action applied
    /// (default 4, spawn tier 4) and once with wait -- drives BOTH sides with HeuristicBot,
    /// and reports:
    ///   * every P1 decision in which HeuristicBot did something, with the internal state
    ///     that produced it (inDanger, time-to-death, time-to-invest, threat/defence, and
    ///     its own spend reason), so the WHY is read off the bot rather than inferred;
    ///   * the investment timeline for BOTH sides in BOTH lines, which is the actual test:
    ///     compare how much the spawn delayed THEM against how much it delayed US.
    ///
    /// RESULT, 7A385A tick 781, action 4 vs wait, 1600 ticks. THE HYPOTHESIS IS NOT BORNE
    /// OUT, and the near-miss is informative.
    ///
    ///   line        P1 (defender) rungs      P2 (us) rungs
    ///   action 4    380, 1020                470, 960
    ///   wait        380, 840, 1560           350, 810, 1560
    ///
    /// The defender's NEXT investment is not delayed at all -- 380 in both lines -- while
    /// OURS slips 350 -> 470, a 120-tick (4.0s) delay. On the following rung the defender is
    /// delayed 180 ticks against our 150, so cumulatively it is only 30 ticks (1.0s) worse
    /// off than us. Neither side reaches a third rung. The arms race is very close to
    /// SYMMETRIC: both end on investment 4 and income 19.7, where the wait line takes both
    /// to investment 5 and income 59.9.
    ///
    /// WHAT THE DEFENDER ACTUALLY DOES, read off the bot rather than inferred: it invests
    /// FIRST (tick 380) and only then repairs (tick 385, -\$32), triggered by castle HP
    /// falling to 73%, i.e. under RepairHpThreshold 0.75. So its priorities are not
    /// inverted. It then spends steadily on gadgets for the rest of the horizon -- roughly
    /// \$400 across the window -- which is the arms-race participation a human refuses.
    ///
    /// SO THE SPAWN IS NOT PROFITABLE BY STARVING THE OPPONENT. It is profitable because the
    /// ringo chips P1's castle to 86.6% while ours stays at 95-100%, and castle HP is the one
    /// axis left ASYMMETRIC. The economic wreckage is real and roughly equal on both sides --
    /// and being equal, a differential evaluator prices it at exactly zero. Marc's instinct
    /// that the rollout opponent joins an arms race a human would decline is correct; the
    /// mechanism is symmetric mutual damage that costs nothing to score, not an asymmetric
    /// delay that profits us.
    ///
    /// Usage: --defender-trace &lt;replay&gt; &lt;tick&gt; [--action N] [--ticks N] [--quiet]
    /// </summary>
    public static class DefenderTrace
    {
        public static void Run(string path, string[] args)
        {
            long target = long.Parse(args[0]);
            int action = 4, ticks = 1600;
            bool quiet = false;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--action" && i + 1 < args.Length) action = int.Parse(args[++i]);
                if (args[i] == "--ticks" && i + 1 < args.Length) ticks = int.Parse(args[++i]);
                if (args[i] == "--quiet") quiet = true;
            }

            var rf = ReplayFile.Read(path);
            var (state, engine) = rf.BuildStart();
            state.Player1.SetLoadout(new[] { Bs(rf.P1Off), Bs(rf.P1Def), Bs(rf.P1Sig) });
            state.Player2.SetLoadout(new[] { Bs(rf.P2Off), Bs(rf.P2Def), Bs(rf.P2Sig) });
            for (int i = 0; i < rf.TickCount && state.CurrentTick < target; i++)
            {
                if (rf.A1[i] != 0) engine.ApplyAction(1, rf.A1[i]);
                if (rf.A2[i] != 0) engine.ApplyAction(2, rf.A2[i]);
                engine.Tick();
                if (state.IsGameOver) break;
            }

            Console.WriteLine();
            Console.WriteLine("=== " + rf.GameId + " @ tick " + state.CurrentTick
                            + "  P2 action " + action + " vs wait, " + ticks + " ticks, HeuristicBot both sides ===");
            Console.WriteLine("  start: P1 $" + state.Player1.Money.ToString("F1")
                            + " inc " + state.Player1.Income.ToString("F1")
                            + " inv " + state.Player1.InvestmentCount
                            + " price " + state.Player1.InvestmentPrice.ToString("F1")
                            + " castle " + state.Player1.CastleHealth + "/" + state.Player1.CastleMaxHealth
                            + "   |   P2 $" + state.Player2.Money.ToString("F1")
                            + " inv " + state.Player2.InvestmentCount);

            var withAct = Line(engine, action, ticks, verbose: !quiet, label: "ACTION " + action);
            var withWait = Line(engine, 0, ticks, verbose: false, label: "WAIT");

            Console.WriteLine();
            Console.WriteLine("  INVESTMENT TIMELINE (tick after the decision at which each rung landed):");
            Console.WriteLine("    line          P1 (defender)                P2 (us)");
            Console.WriteLine("    " + new string('-', 66));
            Console.WriteLine("    action " + action + "     " + Fmt(withAct.p1).PadRight(28) + Fmt(withAct.p2));
            Console.WriteLine("    wait         " + Fmt(withWait.p1).PadRight(28) + Fmt(withWait.p2));
            Console.WriteLine();
            Console.WriteLine("  THE TEST: how much did the spawn delay each side's NEXT investment?");
            string d1 = Delay(withWait.p1, withAct.p1);
            string d2 = Delay(withWait.p2, withAct.p2);
            Console.WriteLine("    P1 (defender) delayed by: " + d1);
            Console.WriteLine("    P2 (us)       delayed by: " + d2);
            Console.WriteLine("    If the defender's delay exceeds ours, the spawn is PROFITABLE IN SIMULATION");
            Console.WriteLine("    -- which is exactly the trade a human who ignores chip damage refuses to make.");
        }

        private static string Fmt(List<int> v)
            => v.Count == 0 ? "(none)" : string.Join(", ", v);

        private static string Delay(List<int> baseLine, List<int> actLine)
        {
            if (baseLine.Count == 0) return "n/a (no investment in the wait line)";
            if (actLine.Count == 0) return "NEVER reached it within the horizon (baseline tick " + baseLine[0] + ")";
            return (actLine[0] - baseLine[0]) + " ticks (" + ((actLine[0] - baseLine[0]) / 30.0).ToString("F1") + "s)"
                 + "   [" + baseLine[0] + " -> " + actLine[0] + "]";
        }

        private static (List<int> p1, List<int> p2) Line(GameEngine engine, int action, int ticks,
                                                        bool verbose, string label)
        {
            var clone = engine.Clone(rngSeed: 4242);
            var cs = clone._state;
            if (action > 0) clone.ApplyAction(2, action);

            var p1Bot = new HeuristicBot(1);
            var p2Bot = new HeuristicBot(2);
            var p1 = cs.Player1;
            var p2 = cs.Player2;
            int prev1 = p1.InvestmentCount, prev2 = p2.InvestmentCount;
            var t1 = new List<int>();
            var t2 = new List<int>();

            if (verbose)
            {
                Console.WriteLine();
                Console.WriteLine("  P1 (HeuristicBot, standing in for Marc) -- every decision where it ACTED:");
                Console.WriteLine("    tick   $     inc  inv  price   hp%   danger  TTD     TTI     threat  def    act  why");
                Console.WriteLine("    " + new string('-', 108));
            }

            for (int t = 0; t < ticks && !cs.IsGameOver; t++)
            {
                double moneyBefore = p1.Money;
                clone.Tick();
                p1Bot.Update(clone);
                p2Bot.Update(clone);

                if (p1.InvestmentCount > prev1) { prev1 = p1.InvestmentCount; t1.Add(t); }
                if (p2.InvestmentCount > prev2) { prev2 = p2.InvestmentCount; t2.Add(t); }

                if (verbose && p1.Money < moneyBefore - 0.001)
                {
                    string ttd = p1Bot.LastTimeToDeathSeconds >= 999999f ? "inf"
                               : p1Bot.LastTimeToDeathSeconds.ToString("F1");
                    string tti = p1Bot.LastTimeToInvestSeconds >= 999999f ? "inf"
                               : p1Bot.LastTimeToInvestSeconds.ToString("F1");
                    Console.WriteLine("    " + t.ToString().PadLeft(5)
                        + moneyBefore.ToString("F0").PadLeft(6)
                        + p1.Income.ToString("F1").PadLeft(7)
                        + p1.InvestmentCount.ToString().PadLeft(4)
                        + p1.InvestmentPrice.ToString("F0").PadLeft(7)
                        + (100.0 * p1.CastleHealth / p1.CastleMaxHealth).ToString("F0").PadLeft(6)
                        + p1Bot.LastDecisionWasDanger.ToString().PadLeft(8)
                        + ttd.PadLeft(8) + tti.PadLeft(8)
                        + p1Bot.LastThreatScore.ToString("F1").PadLeft(8)
                        + p1Bot.LastDefenseScore.ToString("F1").PadLeft(7)
                        + clone.LastActionP1.ToString().PadLeft(5)
                        + "  " + (string.IsNullOrEmpty(p1Bot.LastSpawnReason) ? p1Bot.LastSpendDebug : p1Bot.LastSpawnReason)
                        + " (-$" + (moneyBefore - p1.Money).ToString("F0") + ")");
                }
            }

            if (verbose)
            {
                Console.WriteLine("    leaf: P1 $" + p1.Money.ToString("F0") + " inc " + p1.Income.ToString("F1")
                                + " inv " + p1.InvestmentCount + " castle "
                                + (100.0 * p1.CastleHealth / p1.CastleMaxHealth).ToString("F0") + "%"
                                + "   |   P2 $" + p2.Money.ToString("F0") + " inc " + p2.Income.ToString("F1")
                                + " inv " + p2.InvestmentCount);
            }
            return (t1, t2);
        }

        private static string Bs(string id) => string.IsNullOrEmpty(id) ? id : id.Split('_')[0];
    }
}
