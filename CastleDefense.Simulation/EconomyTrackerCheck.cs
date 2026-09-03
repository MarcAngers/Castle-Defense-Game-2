using CastleDefense.Engine;
using CastleDefense.Engine.Bot;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Models;

namespace CastleDefense.Simulation
{
    /// <summary>
    /// Measures how wrong <see cref="OpponentEconomy"/> is, against the engine's true values.
    ///
    /// WHY THIS EXISTS AT ALL. The tracker exists to answer "am I losing the economy race",
    /// and a number that steers the bot's spending while nobody re-derives it is precisely the
    /// failure this project keeps hitting -- the 2026-07-28 audit found two of the two
    /// instruments it examined were broken. This one is unusually cheap to validate because
    /// the engine knows the truth: the tracker is only allowed to read what a player can see,
    /// but the CHECK may read everything.
    ///
    /// WHAT TO LOOK AT. The headline is the INCOME row: the tracker's job is to know which
    /// rung the opponent is on, and an income estimate that is right is worth more than a
    /// money estimate that is close. Money is expected to run LOW by construction -- see the
    /// bound note on OpponentEconomy -- so a negative money bias is the design working, and a
    /// POSITIVE money bias means spending is being missed.
    ///
    /// Run against several opponent archetypes on purpose. A tracker that is right against a
    /// spam bot and wrong against an investor is worse than useless, because the investor is
    /// the opponent the race is against.
    /// </summary>
    public static class EconomyTrackerCheck
    {
        private static readonly string[] Offense = { "nuke", "firebomb", "snipe", "freeze" };
        private static readonly string[] Defense = { "heal", "reinforcements", "speed", "wall" };

        private sealed class Acc
        {
            public int Samples, Games;
            public double AbsIncomeErr, AbsMoneyErr, MoneyBias, IncomeBias;
            public int IncomeExact, CountExact, CountOver, CountUnder;
            public double WorstMoneyErr;
        }

        public static void Run(string[] args)
        {
            int games = 40;
            int seed = 12345;
            bool trace = false;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--games" && i + 1 < args.Length) games = int.Parse(args[++i]);
                else if (args[i] == "--seed" && i + 1 < args.Length) seed = int.Parse(args[++i]);
                else if (args[i] == "--trace") trace = true;
            }

            Console.WriteLine("=== ECONOMY TRACKER CHECK ===");
            Console.WriteLine("The tracker reads only what a player can see; this check reads the truth.\n");

            // Archetypes are action-PROVIDERS: given the engine, what does side 1 do this
            // decision? The sim bots already work this way (GetAction), and HeuristicBot is
            // wrapped so it can sit in the same list.
            //
            // DoNothing and PureInvestor are the correctness anchors. DoNothing spends
            // nothing, so the tracker must be EXACT -- any error there is a bug in the income
            // accrual itself. PureInvestor is the opponent this whole feature exists for: the
            // patient economic player the old spending-inference design would have reported at
            // income 2 after five minutes.
            // `economic` marks the archetypes that actually play the ladder. The tracker is
            // REQUIRED to be accurate against those, because they are the opponents the
            // ARMAGEDDON race is against. A HOARDER -- one that could invest and chooses not
            // to -- is expected to be over-credited: that is assume-ASAP working as specified,
            // not a defect, and the first version of this check wrongly failed it for that.
            var archetypes = new List<(string name, bool economic, Func<GameEngine, HeuristicBot, int> act)>
            {
                ("DoNothing",    false, (e, h) => 0),
                ("PureInvestor", true,  (e, h) => e._state.Player1.Money >= e._state.Player1.InvestmentPrice ? 9 : 0),
                ("Tier1Spam",    false, (e, h) => 1),
                ("Tier5Spam",    false, (e, h) => 5),
                ("Random",       false, (e, h) => -1),   // -1 => driven by RandomBot below
                ("HeuristicBot", true,  (e, h) => -2),   // -2 => driven by HeuristicBot.Update
            };

            var teams = (TeamColour[])Enum.GetValues(typeof(TeamColour));
            var rng = new Random(seed);
            bool allOk = true;

            Console.WriteLine("  opponent        games  income exact  |inc err|   money bias   |money err|   worst $");
            Console.WriteLine("  " + new string('-', 88));

            foreach (var (name, economic, act) in archetypes)
            {
                var acc = new Acc();
                bool traceFirst = trace;
                for (int g = 0; g < games; g++)
                {
                    var map = teams[rng.Next(teams.Length)];
                    var state = new GameState(map, new Random(rng.Next()));
                    state.Player1 = new PlayerState { Side = 1, Team = teams[rng.Next(teams.Length)] };
                    state.Player2 = new PlayerState { Side = 2, Team = teams[rng.Next(teams.Length)] };
                    state.Player1.SetLoadout(new[] { Offense[rng.Next(4)], Defense[rng.Next(4)],
                        GameDataManager.GetSignatureGadgetIdForTeam(state.Player1.Team) });
                    state.Player2.SetLoadout(new[] { Offense[rng.Next(4)], Defense[rng.Next(4)],
                        GameDataManager.GetSignatureGadgetIdForTeam(state.Player2.Team) });

                    var engine = new GameEngine(state, null, rng.Next());

                    // Side 1 is the tracked OPPONENT; side 2 is whoever is watching.
                    var subject = new HeuristicBot(1);
                    var randomBot = new RandomBot();
                    var watcher = new HeuristicBot(2);
                    var tracker = new OpponentEconomy(1);

                    // A cast is animated, so observing it is fair. Subscribing is also the
                    // only unambiguous way to see one: five effects never raise
                    // OnGadgetAnimation, which is what made the recorder miss 28 of 52 casts.
                    engine.OnGadgetCast += (side, gadgetId, pos) =>
                    {
                        if (side == 1) tracker.ObserveGadgetCast(engine.GetGadgetDefinition(gadgetId), state.Player1.Team);
                    };

                    acc.Games++;
                    while (!state.IsGameOver && state.CurrentTick < GameEngine.MAX_TICKS)
                    {
                        engine.Tick();

                        // Side 1 acts on the evaluation cadence every other harness uses.
                        if (state.CurrentTick % 3 == 0)
                        {
                            int a = act(engine, subject);
                            if (a == -2) subject.Update(engine);
                            else if (a == -1) engine.ApplyAction(1, randomBot.GetAction());
                            else if (a > 0 || a == 0) engine.ApplyAction(1, a);
                        }

                        watcher.Update(engine);
                        tracker.Update(engine);

                        // Sample once a second, after the opening squad has finished landing.
                        // FIRST-DIVERGENCE TRACE. The aggregate says the tracker briefly
                        // believes the opponent is on a lower rung than it is; this says
                        // exactly when and with what balances, which is the only way to tell
                        // a timing artefact from a real accounting bug.
                        if (traceFirst && g == 0 && tracker.InvestmentCount != state.Player1.InvestmentCount)
                        {
                            Console.WriteLine($"    [{name}] FIRST DIVERGENCE at tick {state.CurrentTick}: " +
                                              $"tracker count={tracker.InvestmentCount} money={tracker.Money:F2} " +
                                              $"inc={tracker.Income:F2}  |  truth count={state.Player1.InvestmentCount} " +
                                              $"money={state.Player1.Money:F2} inc={state.Player1.Income:F2}  " +
                                              $"| spendSeen={tracker.SpendSeen:F0} gadgetIncome={tracker.IncomeSeen:F0}");
                            traceFirst = false;
                        }

                        if (state.CurrentTick % 30 == 0 && state.CurrentTick > 180)
                        {
                            var truth = state.Player1;
                            double dInc = tracker.Income - truth.Income;
                            double dMon = tracker.Money - truth.Money;
                            acc.Samples++;
                            acc.AbsIncomeErr += Math.Abs(dInc);
                            acc.IncomeBias += dInc;
                            acc.AbsMoneyErr += Math.Abs(dMon);
                            acc.MoneyBias += dMon;
                            if (Math.Abs(dInc) < 0.001) acc.IncomeExact++;
                            if (tracker.InvestmentCount == truth.InvestmentCount) acc.CountExact++;
                            else if (tracker.InvestmentCount > truth.InvestmentCount) acc.CountOver++;
                            else acc.CountUnder++;
                            if (Math.Abs(dMon) > Math.Abs(acc.WorstMoneyErr)) acc.WorstMoneyErr = dMon;
                        }
                    }
                }

                double n = Math.Max(1, acc.Samples);
                double incExactPct = 100.0 * acc.IncomeExact / n;
                Console.WriteLine($"  {name,-14} {acc.Games,6}  {incExactPct,10:F1}%  {acc.AbsIncomeErr / n,9:F1}  " +
                                  $"{acc.MoneyBias / n,11:N0}  {acc.AbsMoneyErr / n,11:N0}  {acc.WorstMoneyErr,9:N0}");
                Console.WriteLine($"                 investment count: exact {100.0 * acc.CountExact / n,5:F1}%  " +
                                  $"over {100.0 * acc.CountOver / n,5:F1}%  under {100.0 * acc.CountUnder / n,5:F1}%");

                // (1) THE ONE DANGEROUS DIRECTION. Under-estimating their income means
                // believing we are ahead when we are behind, which is what makes the bot
                // press instead of save. Over-estimating is the benign failure -- it makes
                // the bot save harder, which is the trade Marc accepted explicitly.
                // Asserted on the FRACTION OF SAMPLES, not on the size of the bias. The rungs
                // are 3x apart at the top (252 -> 750 -> 2500), so crediting one a couple of
                // seconds late produces a large average bias from a brief, self-correcting
                // lag -- a magnitude threshold would be measuring the ladder's step size, not
                // the tracker's accuracy. What matters is how OFTEN we believe they are on a
                // lower rung than they are.
                double underPct = 100.0 * acc.CountUnder / n;
                if (underPct > 10.0)
                {
                    Console.WriteLine($"      FAIL -- believes {name} is on a LOWER rung than it is on " +
                                      $"{underPct:F1}% of samples. That is the direction that makes the bot " +
                                      $"press while behind.");
                    allOk = false;
                }

                // (2) Accuracy is REQUIRED against opponents that play an economy, because
                // those are the ones the race is against. It is not required against hoarders.
                if (economic && incExactPct < 80.0)
                {
                    Console.WriteLine($"      FAIL -- only {incExactPct:F1}% exact vs {name}, an economic opponent.");
                    allOk = false;
                }
                else if (!economic && acc.CountOver / n > 0.5)
                {
                    Console.WriteLine($"      (expected) over-credits a non-investing hoarder -- assume-ASAP by design.");
                }
            }

            Console.WriteLine();
            Console.WriteLine(allOk ? "ALL CHECKS PASSED" : "CHECKS FAILED -- see above.");
        }
    }
}
