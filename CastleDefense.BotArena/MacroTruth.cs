using System.Diagnostics;
using CastleDefense.Engine;
using CastleDefense.Engine.Bot;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Models;

namespace CastleDefense.BotArena
{
    /// <summary>
    /// STAGE 1a — how much win probability does search throw away when it decides whether
    /// to hold money?
    ///
    /// WHY. Stage 0 established that the save-invest macro's entire +11.2 points live in
    /// WHICH decisions it fires on, and that ~3/4 of its firings are decisions where the
    /// investment is not yet affordable — i.e. search is choosing when to HOLD. That choice
    /// is currently made from a 300-tick truncated rollout scored by a 6-feature logistic,
    /// and horizon has a floor (250 -> 30%, 200 -> 1.5%) because below ~270 an
    /// investment does not repay inside the window. The INFERENCE is that the decision
    /// carrying all the bot's margin is evaluated right where the estimator breaks down.
    /// That inference has never been measured. This measures it.
    ///
    /// METHOD. Play games with the shipped bot. At a sampled subset of decisions where the
    /// macro was a candidate, fork the live engine and compute a GROUND-TRUTH value for
    /// both {macro, prior} by playing to a real terminal outcome K times under HeuristicBot,
    /// with common random numbers across the two branches. Then score estimators by REGRET:
    ///
    ///     regret(estimator) = truth(best option) - truth(the option that estimator picked)
    ///
    /// Regret, not agreement rate. Disagreeing when the two options are genuinely equal
    /// costs nothing, and an agreement rate cannot tell those apart.
    ///
    /// TWO CONTROLS, because a small regret number is unreadable on its own — the project
    /// has been burned by exactly this (see the --divergence oracle, and the action-volume
    /// trap that inverted a real comparison):
    ///
    ///   NOISE FLOOR  a perfect but NOISY estimator: choose using half the playouts, score
    ///                on the full truth. Regret above zero here is pure sampling noise at
    ///                K/2, and it lower-bounds what any estimator can achieve at this K.
    ///                If the shallow estimator's regret is near this, it is already as good
    ///                as a truth estimator with half the samples, and Stage 1b is pointless.
    ///
    ///   RANDOM FLOOR coin-flip between macro and prior. This establishes the metric has
    ///                RANGE. If random regret is also tiny, the two options are simply
    ///                interchangeable at these decisions and nothing can be gained.
    ///
    /// The self-oracle (choose and score on the same numbers) is 0 by construction and is
    /// printed only as a wiring check.
    ///
    /// Usage:
    ///   macro-truth [games] [--seed N] [--k N] [--sample P] [--csv path] [--threads N]
    /// </summary>
    public static class MacroTruth
    {
        private sealed class Sample
        {
            public long Tick;
            public int InvestmentCount;
            public double SavingsFraction;
            public double ShallowMacro, ShallowPrior;
            public double TruthMacro, TruthPrior;
            public double HalfMacro, HalfPrior;   // first K/2 playouts only
            // Per-playout outcomes, so Report can price an estimator at ANY budget m<=K.
            // The whole affordability question for Stage 1b is "how small can m be", and
            // storing these answers it from one run instead of one run per m.
            public double[] Vm, Vp;
            public bool ShallowChoseMacro;
        }

        public static void Run(string[] args)
        {
            int games = 60, seed = 4242, k = 40, threads = Math.Max(1, Environment.ProcessorCount - 2);
            double sampleRate = 0.05;
            // commit  = truth under the shallow rollout's own semantics (cheap, but holds the
            //           purse to game end -- see PlayToEndFaithful for why that misleads).
            // onestep = force one action, then the REAL bot plays both branches out.
            string truthMode = "commit";
            // In TICKS. The live decision interval is 15, so 225 ticks = 15 consecutive
            // decisions of saving before the branch gives up. Swept, not guessed -- a
            // conclusion that only holds at one window is not a conclusion.
            int commitTicks = 225;
            string csvPath = null;
            // The shipped configuration. Stage 1a must describe the bot that actually plays.
            int interval = 15, horizon = 300;
            double margin = 0.10;

            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--seed" && i + 1 < args.Length) seed = int.Parse(args[++i]);
                else if (args[i] == "--k" && i + 1 < args.Length) k = int.Parse(args[++i]);
                else if (args[i] == "--sample" && i + 1 < args.Length) sampleRate = double.Parse(args[++i]);
                else if (args[i] == "--csv" && i + 1 < args.Length) csvPath = args[++i];
                else if (args[i] == "--threads" && i + 1 < args.Length) threads = int.Parse(args[++i]);
                else if (args[i] == "--margin" && i + 1 < args.Length) margin = double.Parse(args[++i]);
                else if (args[i] == "--truth-mode" && i + 1 < args.Length) truthMode = args[++i];
                else if (args[i] == "--commit-ticks" && i + 1 < args.Length) commitTicks = int.Parse(args[++i]);
                else if (args[i] == "--horizon" && i + 1 < args.Length) horizon = int.Parse(args[++i]);
                else if (int.TryParse(args[i], out var g)) games = g;
            }

            Console.WriteLine($"[macro-truth] {games} games, sampling {sampleRate:P0} of macro-candidate decisions, " +
                              $"K={k} play-to-completion rollouts per branch");
            Console.WriteLine($"              truth mode: {truthMode}" +
                              (truthMode == "commit"
                                  ? "  (shallow semantics held to game end -- see PlayToEndFaithful)"
                                  : "  (one forced action, then the REAL bot plays both branches out)"));
            if (truthMode == "commit")
                Console.WriteLine($"              commit window: {commitTicks} ticks (~{commitTicks / 15} decisions of saving)");
            Console.WriteLine($"              shipped config: interval {interval}, horizon {horizon}, margin {margin}");
            Console.WriteLine($"              {threads} threads on {Environment.ProcessorCount} logical cores\n");

            var teams = (TeamColour[])Enum.GetValues(typeof(TeamColour));
            var offense = new[] { "nuke", "firebomb", "snipe", "freeze" };
            var defense = new[] { "heal", "reinforcements", "speed", "wall" };

            var rng = new Random(seed);
            var setups = new (int gameSeed, bool searchIsP1, TeamColour map,
                              TeamColour teamA, string offA, string defA,
                              TeamColour teamB, string offB, string defB)[games];
            for (int g = 0; g < games; g++)
                setups[g] = (rng.Next(), g % 2 == 0,
                             teams[rng.Next(teams.Length)],
                             teams[rng.Next(teams.Length)], offense[rng.Next(offense.Length)], defense[rng.Next(defense.Length)],
                             teams[rng.Next(teams.Length)], offense[rng.Next(offense.Length)], defense[rng.Next(defense.Length)]);

            var all = new List<Sample>[games];
            var sw = Stopwatch.StartNew();
            int done = 0;

            Parallel.For(0, games, new ParallelOptions { MaxDegreeOfParallelism = threads }, g =>
            {
                var s = setups[g];
                int side = s.searchIsP1 ? 1 : 2;
                var samples = new List<Sample>();
                // Separate stream from the engine's, so sampling decisions never perturb play.
                var sampleRng = new Random(s.gameSeed ^ 0x7A57);

                var state = new GameState(s.map, new Random(s.gameSeed));
                state.Player1 = new PlayerState(0);
                state.Player2 = new PlayerState(0);
                state.Player1.Side = 1; state.Player1.Team = s.teamA;
                state.Player1.SetLoadout(new[] { s.offA, s.defA, GameDataManager.GetSignatureGadgetIdForTeam(s.teamA) });
                state.Player2.Side = 2; state.Player2.Team = s.teamB;
                state.Player2.SetLoadout(new[] { s.offB, s.defB, GameDataManager.GetSignatureGadgetIdForTeam(s.teamB) });
                var engine = new GameEngine(state, null, s.gameSeed);

                var searcher = new RolloutSearchOpponent(side, interval, horizon, 1, s.gameSeed,
                                                         usePrior: true, overrideMargin: margin,
                                                         useMacro: true, usePressMacro: true,
                                                         macroMargin: margin);
                var heuristic = new HeuristicBotAdapter(s.searchIsP1 ? 2 : 1);

                searcher.OnMacroDecision = (live, info) =>
                {
                    if (sampleRng.NextDouble() >= sampleRate) return;

                    // COMMON RANDOM NUMBERS across the two branches: the same K futures are
                    // used for macro and prior, so the difference is attributable to the
                    // decision rather than to luck. Same discipline the shallow search uses.
                    var seeds = new int[k];
                    for (int r = 0; r < k; r++) seeds[r] = sampleRng.Next();

                    double tm = 0, tp = 0, hm = 0, hp = 0;
                    var vmArr = new double[k]; var vpArr = new double[k];
                    for (int r = 0; r < k; r++)
                    {
                        double vm, vp;
                        if (truthMode == "commit")
                        {
                            vm = PlayToEnd(live, info.Side, true, seeds[r], commitTicks);
                            vp = PlayToEnd(live, info.Side, false, seeds[r], commitTicks);
                        }
                        else
                        {
                            vm = PlayToEndFaithful(live, info.Side, true, seeds[r], interval, horizon, margin);
                            vp = PlayToEndFaithful(live, info.Side, false, seeds[r], interval, horizon, margin);
                        }
                        tm += vm; tp += vp;
                        vmArr[r] = vm; vpArr[r] = vp;
                        if (r < k / 2) { hm += vm; hp += vp; }
                    }

                    samples.Add(new Sample
                    {
                        Tick = info.Tick,
                        InvestmentCount = info.InvestmentCount,
                        SavingsFraction = info.InvestmentPrice > 0 ? info.Money / info.InvestmentPrice : 1.0,
                        ShallowMacro = info.MacroScore,
                        ShallowPrior = info.PriorScore,
                        TruthMacro = tm / k,
                        TruthPrior = tp / k,
                        HalfMacro = hm / Math.Max(1, k / 2),
                        HalfPrior = hp / Math.Max(1, k / 2),
                        // Mirrors the real override test: the macro must clear the prior by
                        // its own margin, otherwise the prior plays.
                        Vm = vmArr, Vp = vpArr,
                        ShallowChoseMacro = info.MacroScore - margin > info.PriorScore,
                    });
                };

                while (!state.IsGameOver)
                {
                    engine.Tick();
                    if (s.searchIsP1) { searcher.Update(engine); heuristic.Update(engine); }
                    else { heuristic.Update(engine); searcher.Update(engine); }
                }

                all[g] = samples;
                int d = Interlocked.Increment(ref done);
                if (d % Math.Max(1, games / 10) == 0)
                    Console.WriteLine($"  ... {d}/{games} games ({sw.Elapsed.TotalSeconds:F0}s, " +
                                      $"{all.Where(x => x != null).Sum(x => x.Count)} samples)");
            });

            var samplesAll = all.Where(x => x != null).SelectMany(x => x).ToList();
            sw.Stop();
            Report(samplesAll, k, margin, sw.Elapsed.TotalSeconds, csvPath);
        }

        /// <summary>
        /// Plays one branch to a REAL terminal outcome. Mirrors RolloutSearchBot.Rollout
        /// exactly except that there is no horizon cap and no evaluator — the return value is
        /// the actual game result, which is the whole point.
        /// </summary>
        private static double PlayToEnd(GameEngine live, int side, bool macro, int seed, int commitTicks)
        {
            var clone = live.Clone(rngSeed: seed);
            var cs = clone._state;
            var mine = new HeuristicBot(side);
            var theirs = new HeuristicBot(side == 1 ? 2 : 1);
            var me = side == 1 ? cs.Player1 : cs.Player2;

            // BOUNDED COMMITMENT. Two earlier definitions of "the value of choosing this
            // macro" were both wrong, in opposite directions, and the data said so:
            //
            //   hold until affordable, no bound  -> in slow-saving states this holds for the
            //       rest of the game and dies with a full bank (the A4 failure). It implied a
            //       single decision swings win probability by 0.62, which is not credible.
            //   force one decision only          -> at ~99% of decisions the investment is not
            //       affordable, so the macro's action IS a no-op and both branches play out
            //       identically. Measured truth gap: exactly 0.0000.
            //
            // The macro is a COMMITMENT: worth nothing for one decision, ruinous forever, and
            // real somewhere in between. commitTicks bounds it, after which the branch hands
            // back to HeuristicBot whether or not the investment landed -- which is what the
            // live bot does when the rollouts turn against continuing to save.
            long start = cs.CurrentTick;
            bool stillSaving = macro;
            while (!cs.IsGameOver)
            {
                clone.Tick();
                if (stillSaving)
                {
                    if (me.Money >= me.InvestmentPrice) { clone.ApplyAction(side, 9); stillSaving = false; }
                    else if (cs.CurrentTick - start >= commitTicks) stillSaving = false;
                }
                else mine.Update(clone);
                theirs.Update(clone);
            }

            if (cs.WinnerSide == 0) return 0.5;
            return cs.WinnerSide == side ? 1.0 : 0.0;
        }

        /// <summary>
        /// FAITHFUL truth: apply the option's action for THIS DECISION ONLY, then let the
        /// REAL bot (search, macros and all) play both branches to a terminal outcome.
        ///
        /// WHY THIS EXISTS. <see cref="PlayToEnd"/> holds the purse until the investment
        /// lands with no horizon, which in slow-saving states means holding for the rest of
        /// the game and dying with a full bank — the A4 saturation failure. That is the
        /// SEMANTICS the shallow rollout uses, and reproducing it is right for asking "is the
        /// estimate accurate under its own model", but it is NOT what choosing the macro
        /// actually does: the real macro is re-priced every interval and abandoned the moment
        /// the rollouts turn against it. Measured against the commit model, a single decision
        /// appeared to swing win probability by 0.62, which is not credible for one move.
        ///
        /// This variant differs between branches by exactly one action and then hands both to
        /// the same policy, so the number it produces is the real regret of the real
        /// decision. It is far more expensive — a full search playout per sample per branch —
        /// which is why it runs at lower K.
        /// </summary>
        private static double PlayToEndFaithful(GameEngine live, int side, bool macro, int seed,
                                                int interval, int horizon, double margin)
        {
            var clone = live.Clone(rngSeed: seed);
            var cs = clone._state;
            var me = side == 1 ? cs.Player1 : cs.Player2;

            // The one forced decision.
            if (macro)
            {
                if (me.Money >= me.InvestmentPrice) clone.ApplyAction(side, 9);
                // else: hold the purse for this decision, which is what the macro does.
            }
            else
            {
                new HeuristicBot(side).Update(clone);
            }

            var searcher = new RolloutSearchOpponent(side, interval, horizon, 1, seed,
                                                     usePrior: true, overrideMargin: margin,
                                                     useMacro: true, usePressMacro: true,
                                                     macroMargin: margin);
            var theirs = new HeuristicBot(side == 1 ? 2 : 1);
            while (!cs.IsGameOver)
            {
                clone.Tick();
                if (side == 1) { searcher.Update(clone); theirs.Update(clone); }
                else { theirs.Update(clone); searcher.Update(clone); }
            }

            if (cs.WinnerSide == 0) return 0.5;
            return cs.WinnerSide == side ? 1.0 : 0.0;
        }

        private static void Report(List<Sample> s, int k, double margin, double secs, string csvPath)
        {
            if (s.Count == 0) { Console.WriteLine("  no samples collected."); return; }

            double regretShallow = 0, regretHalf = 0, regretRandom = 0, regretSelf = 0;
            int disagree = 0, contended = 0, regretContended = 0;
            double contendedRegretSum = 0;
            double gapSum = 0;

            foreach (var x in s)
            {
                double best = Math.Max(x.TruthMacro, x.TruthPrior);
                double shallowPick = x.ShallowChoseMacro ? x.TruthMacro : x.TruthPrior;
                bool halfPicksMacro = x.HalfMacro > x.HalfPrior;
                double halfPick = halfPicksMacro ? x.TruthMacro : x.TruthPrior;

                regretShallow += best - shallowPick;
                regretHalf += best - halfPick;
                regretRandom += best - 0.5 * (x.TruthMacro + x.TruthPrior);
                regretSelf += best - Math.Max(x.TruthMacro, x.TruthPrior);   // 0 by construction

                bool truthPrefersMacro = x.TruthMacro > x.TruthPrior;
                if (truthPrefersMacro != x.ShallowChoseMacro) disagree++;
                gapSum += Math.Abs(x.TruthMacro - x.TruthPrior);

                // "Contended" = the shallow scores were close enough that a better estimator
                // could plausibly flip the decision.
                if (Math.Abs(x.ShallowMacro - x.ShallowPrior) < margin * 2)
                {
                    contended++;
                    contendedRegretSum += best - shallowPick;
                    if (truthPrefersMacro != x.ShallowChoseMacro) regretContended++;
                }
            }

            int n = s.Count;
            Console.WriteLine();
            Console.WriteLine(new string('=', 78));
            Console.WriteLine($"  STAGE 1a RESULT   {n} sampled decisions, K={k}, {secs:F0}s");
            Console.WriteLine(new string('=', 78));
            Console.WriteLine();
            Console.WriteLine($"  mean |truth gap| between macro and prior : {gapSum / n:F4}");
            Console.WriteLine($"  shallow/truth disagreement rate          : {(double)disagree / n:P1}");
            Console.WriteLine();
            Console.WriteLine("  REGRET (win probability thrown away per decision; lower is better)");
            Console.WriteLine($"    self-oracle  (wiring check, must be 0) : {regretSelf / n:F5}");
            Console.WriteLine($"    NOISE FLOOR  (truth at K/2={k / 2})         : {regretHalf / n:F5}");
            Console.WriteLine($"    SHALLOW      (what ships today)        : {regretShallow / n:F5}");
            Console.WriteLine($"    RANDOM FLOOR (coin flip)               : {regretRandom / n:F5}");
            Console.WriteLine();
            Console.WriteLine($"  contended decisions ({contended} of {n}, {(double)contended / n:P0}):");
            Console.WriteLine($"    shallow regret on those                : {(contended > 0 ? contendedRegretSum / contended : 0):F5}");
            Console.WriteLine($"    disagreements among those              : {regretContended}");
            Console.WriteLine();
            Console.WriteLine("  HOW TO READ THIS. The shallow estimator can only be improved by as much as");
            Console.WriteLine("  it sits ABOVE the noise floor, and the whole exercise only matters if the");
            Console.WriteLine("  random floor is well above both -- otherwise the two options are simply");
            Console.WriteLine("  interchangeable at these decisions and no estimator can gain anything.");
            Console.WriteLine();
            Console.WriteLine("  Stage 1b proceeds if disagreement >= 15% AND shallow regret >= 0.03.");
            Console.WriteLine();
            Console.WriteLine("  AFFORDABILITY: regret of a bounded-commitment truth estimator at budget m.");
            Console.WriteLine("  In-game cost scales with m, so the smallest m reaching ~0 is what Stage 1b");
            Console.WriteLine("  would actually run. Ties broken toward the PRIOR, matching the override test.");
            foreach (int m in new[] { 1, 2, 3, 4, 6, 8, 12, 16 })
            {
                if (m > k) break;
                double reg = 0; int flips = 0;
                foreach (var x in s)
                {
                    if (x.Vm == null) continue;
                    double a = 0, b = 0;
                    for (int i = 0; i < m; i++) { a += x.Vm[i]; b += x.Vp[i]; }
                    bool pickMacro = a > b;                 // tie -> prior, as the margin does
                    double best = Math.Max(x.TruthMacro, x.TruthPrior);
                    reg += best - (pickMacro ? x.TruthMacro : x.TruthPrior);
                    if (pickMacro != (x.TruthMacro > x.TruthPrior)) flips++;
                }
                Console.WriteLine($"    m={m,2}  regret {reg / s.Count:F5}   sign-errors {flips,4} ({(double)flips / s.Count:P1})");
            }

            if (csvPath != null)
            {
                using var w = new StreamWriter(csvPath);
                w.WriteLine("tick,investment_count,savings_fraction,shallow_macro,shallow_prior," +
                            "truth_macro,truth_prior,half_macro,half_prior,shallow_chose_macro");
                foreach (var x in s)
                    w.WriteLine($"{x.Tick},{x.InvestmentCount},{x.SavingsFraction:F4}," +
                                $"{x.ShallowMacro:F5},{x.ShallowPrior:F5},{x.TruthMacro:F5},{x.TruthPrior:F5}," +
                                $"{x.HalfMacro:F5},{x.HalfPrior:F5},{(x.ShallowChoseMacro ? 1 : 0)}");
                Console.WriteLine($"\n  wrote {csvPath}");
            }
        }
    }
}
