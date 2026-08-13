using System.Diagnostics;
using CastleDefense.Engine;
using CastleDefense.Engine.Bot;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Models;

namespace CastleDefense.BotArena
{
    /// <summary>
    /// Benchmark-side wrapper around <see cref="RolloutSearchBot"/>.
    ///
    /// The search logic MOVED to CastleDefense.Engine on 2026-07-28 so the web game could
    /// use it. This subclass exists only to satisfy IArenaOpponent, whose single method
    /// has the same signature as the base class's Update — so nothing here re-implements
    /// any behaviour. Keeping one implementation matters more than usual on this project:
    /// a second copy would drift, and search-test's numbers would stop describing the bot
    /// people actually play against.
    /// </summary>
    public class RolloutSearchOpponent : RolloutSearchBot, IArenaOpponent
    {
        public RolloutSearchOpponent(int side, int decisionInterval = 15, int horizon = 300,
                                     int rolloutsPerAction = 1, int seed = 0,
                                     bool usePrior = true, double overrideMargin = 0.01,
                                     bool useMacro = true, bool usePressMacro = true,
                                     int maxDecisionMs = 0, int maxParallelism = 1,
                                     double macroMargin = double.NaN,
                                     int pressWaveUnits = 6, int pressMinTier = 6,
                                     double pressPeakMargin = 0.02, double pressOffPeakMargin = 0.30,
                                     int pressPeakMinInvest = 6, int pressPeakMaxInvest = 7,
                                     bool pressWaveCommit = false,
                                     bool useArmageddonMacro = true,
                                     double armageddonMargin = double.NaN,
                                     RolloutPolicyKind ownRolloutPolicy = RolloutPolicyKind.Heuristic,
                                     RolloutPolicyKind oppRolloutPolicy = RolloutPolicyKind.Heuristic,
                                     double saveCommitFraction = 0.5,
                                     double macroRandomRate = 0.0,
                                     bool macroRandomAffordable = false,
                                     bool deepMacroEval = false,
                                     int deepPlayouts = 2,
                                     int deepCommitTicks = 225,
                                     int staleTicks = 0,
                                     bool suppressDefenceGadget = false,
                                     GadgetSuppression gadgetSuppression = GadgetSuppression.None,
                                     bool useUpgradeMacro = false,
                                     double upgradeMargin = double.NaN)
            : base(side, decisionInterval, horizon, rolloutsPerAction, seed,
                   usePrior, overrideMargin, useMacro, usePressMacro,
                   maxDecisionMs, maxParallelism, asyncDecisions: false,
                   macroMargin: macroMargin,
                   pressWaveUnits: pressWaveUnits, pressMinTier: pressMinTier,
                   pressPeakMargin: pressPeakMargin, pressOffPeakMargin: pressOffPeakMargin,
                   pressPeakMinInvest: pressPeakMinInvest, pressPeakMaxInvest: pressPeakMaxInvest,
                   pressWaveCommit: pressWaveCommit,
                   useArmageddonMacro: useArmageddonMacro, armageddonMargin: armageddonMargin,
                   ownRolloutPolicy: ownRolloutPolicy, oppRolloutPolicy: oppRolloutPolicy,
                   saveCommitFraction: saveCommitFraction, macroRandomRate: macroRandomRate,
                   macroRandomAffordable: macroRandomAffordable,
                   deepMacroEval: deepMacroEval, deepPlayouts: deepPlayouts,
                   deepCommitTicks: deepCommitTicks, staleTicks: staleTicks,
                   suppressDefenceGadget: suppressDefenceGadget,
                   gadgetSuppression: gadgetSuppression,
                   useUpgradeMacro: useUpgradeMacro, upgradeMargin: upgradeMargin)
        {
        }
    }

    public static class SearchTest
    {
        private static RolloutPolicyKind ParsePolicy(string s) => s.ToLowerInvariant() switch
        {
            "heuristic" => RolloutPolicyKind.Heuristic,
            "saving" => RolloutPolicyKind.Saving,
            _ => throw new ArgumentException($"unknown rollout policy '{s}' (heuristic|saving)"),
        };

        public static void Run(string[] args)
        {
            int games = 20, seed = 20260728, interval = 15, horizon = 300, rollouts = 1;
            bool headstart = false, usePrior = true, useMacro = true, usePress = true, linearEval = false;
            bool refitEval = false;
            // Direct override of the six deployed logistic weights, so a single
            // coefficient can be moved at a time. Ablating one weight is the only way to
            // find WHICH part of a refit helps or hurts -- swapping the whole vector
            // confounds six changes into one number.
            string evalWeights = null;
            string csvPath = null;
            int maxDecisionMs = 0;
            // Intra-decision parallelism (cores per decision). 1 keeps each benchmark game
            // single-threaded, because search-test already parallelises ACROSS games and
            // nesting the two oversubscribes the box. --live flips this: it runs games one
            // at a time with the whole machine on each decision, which is what live play
            // actually does.
            int intraThreads = 1;
            double margin = 0.01;
            double macroMargin = double.NaN;
            int pressWave = 6, pressMinTier = 6, pressLo = 6, pressHi = 7;
            // NaN => fall back to the shared macro margin (the committed behaviour).
            double pressPeak = double.NaN, pressOff = double.NaN;
            bool pressWaveCommit = false;
            bool useArma = true;
            double armaMargin = double.NaN;
            // PROBE A. Which policy drives each side inside a rollout. Both default to
            // Heuristic, which is the committed behaviour and therefore the control arm —
            // run the control in the SAME build as the treatment, never against a number
            // copied from an earlier session.
            var ownRollout = RolloutPolicyKind.Heuristic;
            var oppRollout = RolloutPolicyKind.Heuristic;
            double saveCommit = 0.5;
            // STAGE 0. Pair with --no-macro so the macro is fired ONLY by the coin flip;
            // leaving it selectable as well would confound the two.
            double macroRandomRate = 0.0;
            bool macroRandomAffordable = false;
            // STAGE 1b. Off by default -- the control arm must be the shipped bot.
            bool deepMacro = false; int deepPlayouts = 2; int deepCommitTicks = 225;
            // ASYNC STALENESS. The live bot acts on state as old as its own thinking
            // time; this benchmark has always decided and acted on the same tick, so
            // every number it has ever produced describes a bot with no latency at all.
            // 0 keeps that (and reproduces byte-for-byte). One tick is 33 ms.
            int staleTicks = 0;
            // Suppresses the search bot's defence gadget entirely (prior + rollout policy +
            // action 12). Exists to check a defence-duel result against the CALIBRATED
            // instrument: the duel said never-casting is worth +25 to +34 points, which is
            // too large to believe from a near-mirror harness with a ~50% draw rate.
            bool noDefGadget = false;
            // CANDIDATE-COUNT CONTROL (2026-08-11). --no-def-gadget does three things at
            // once; these name each part so the attribution can be measured:
            //   --sup def-cand    action 12 leaves the candidate list, prior still casts
            //   --sup def-cast    prior/rollout stop casting, action 12 still a candidate
            //   --sup off-cand,off-cast   the same for the OFFENCE gadget (specificity)
            var suppression = GadgetSuppression.None;
            // GADGET-UPGRADE MACRO. Off by default -- the control arm must stay the shipped
            // bot. As a RULE inside HeuristicBot this behaviour was monotonically harmful;
            // this asks whether search picking the moment rescues it, the way it did for
            // the defensive casting rule.
            bool useUpgradeMacro = false; double upgradeMargin = double.NaN;
            // Leave a couple of cores for the OS and the desktop. The 2026-07-27 crash
            // post-mortem traced a machine-wide stall to running 15 CPU-bound processes on
            // 20 logical cores, so don't saturate by default.
            int threads = Math.Max(1, Environment.ProcessorCount - 2);
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--threads" && i + 1 < args.Length) threads = int.Parse(args[++i]);
                else if (args[i] == "--linear-eval") linearEval = true;
                else if (args[i] == "--refit-eval") refitEval = true;
                else if (args[i] == "--press-wave" && i + 1 < args.Length) pressWave = int.Parse(args[++i]);
                else if (args[i] == "--press-wave-commit") pressWaveCommit = true;
                else if (args[i] == "--no-arma") useArma = false;
                else if (args[i] == "--arma-margin" && i + 1 < args.Length) armaMargin = double.Parse(args[++i]);
                else if (args[i] == "--press-min-tier" && i + 1 < args.Length) pressMinTier = int.Parse(args[++i]);
                else if (args[i] == "--press-peak-margin" && i + 1 < args.Length) pressPeak = double.Parse(args[++i]);
                else if (args[i] == "--press-offpeak-margin" && i + 1 < args.Length) pressOff = double.Parse(args[++i]);
                else if (args[i] == "--press-window" && i + 1 < args.Length) { var pw = args[++i].Split('-'); pressLo = int.Parse(pw[0]); pressHi = int.Parse(pw[1]); }
                else if (args[i] == "--eval-weights" && i + 1 < args.Length) evalWeights = args[++i];
                else if (args[i] == "--rollout-policy" && i + 1 < args.Length) ownRollout = ParsePolicy(args[++i]);
                else if (args[i] == "--opp-rollout-policy" && i + 1 < args.Length) oppRollout = ParsePolicy(args[++i]);
                else if (args[i] == "--save-commit" && i + 1 < args.Length) saveCommit = double.Parse(args[++i]);
                else if (args[i] == "--macro-random-rate" && i + 1 < args.Length) macroRandomRate = double.Parse(args[++i]);
                else if (args[i] == "--macro-random-affordable") macroRandomAffordable = true;
                else if (args[i] == "--stale-ticks" && i + 1 < args.Length) staleTicks = int.Parse(args[++i]);
                else if (args[i] == "--no-def-gadget") noDefGadget = true;
                else if (args[i] == "--upgrade-macro") useUpgradeMacro = true;
                else if (args[i] == "--upgrade-margin" && i + 1 < args.Length) { upgradeMargin = double.Parse(args[++i]); useUpgradeMacro = true; }
                else if (args[i] == "--sup" && i + 1 < args.Length)
                    foreach (var part in args[++i].Split(','))
                        suppression |= part.Trim().ToLowerInvariant() switch
                        {
                            "def-cand" => GadgetSuppression.DefenceCandidate,
                            "def-cast" => GadgetSuppression.DefenceCasting,
                            "off-cand" => GadgetSuppression.OffenceCandidate,
                            "off-cast" => GadgetSuppression.OffenceCasting,
                            var o => throw new ArgumentException($"unknown --sup part '{o}' (def-cand|def-cast|off-cand|off-cast)"),
                        };
                else if (args[i] == "--deep-macro") deepMacro = true;
                else if (args[i] == "--deep-playouts" && i + 1 < args.Length) deepPlayouts = int.Parse(args[++i]);
                else if (args[i] == "--deep-commit-ticks" && i + 1 < args.Length) deepCommitTicks = int.Parse(args[++i]);
                else if (args[i] == "--seed" && i + 1 < args.Length) seed = int.Parse(args[++i]);
                else if (args[i] == "--interval" && i + 1 < args.Length) interval = int.Parse(args[++i]);
                else if (args[i] == "--horizon" && i + 1 < args.Length) horizon = int.Parse(args[++i]);
                else if (args[i] == "--rollouts" && i + 1 < args.Length) rollouts = int.Parse(args[++i]);
                else if (args[i] == "--margin" && i + 1 < args.Length) margin = double.Parse(args[++i]);
                // Omitted => macros use --margin, i.e. the old single-margin behaviour.
                else if (args[i] == "--macro-margin" && i + 1 < args.Length) macroMargin = double.Parse(args[++i]);
                else if (args[i] == "--no-prior") usePrior = false;
                else if (args[i] == "--no-macro") useMacro = false;
                else if (args[i] == "--no-press") usePress = false;
                // Lets the benchmark run the LIVE game's configuration. Without these the
                // harness measures an agent the player never faces — which is how a 490ms
                // decision shipped as "16.8ms".
                else if (args[i] == "--max-ms" && i + 1 < args.Length) maxDecisionMs = int.Parse(args[++i]);
                // Exactly what GameHostingService ships. The one thing it cannot reproduce
                // is asyncDecisions: live, a decision is applied up to ~250ms after the
                // state it was computed from, and modelling that needs a real-time loop.
                // Everything else matches.
                //
                // DO NOT set linearEval here. It was set to true until 2026-08-04, which
                // was simply wrong: UseLinearEval is a static defaulting to false and
                // NOTHING in CastleDefenseGame2 or CastleDefense.Simulation ever assigns
                // it, so the live game has always run the LOGISTIC evaluator. The preset
                // therefore benchmarked an evaluator no player has ever faced — the same
                // failure the comment above it warns about, in the same method.
                else if (args[i] == "--live")
                {
                    interval = 10; horizon = 900; margin = 0.03;
                    maxDecisionMs = 250; threads = 1;
                    intraThreads = Math.Max(1, Environment.ProcessorCount - 2);
                }
                else if (args[i] == "--headstart") headstart = true;
                // PER-GAME OUTCOMES, so two arms on the same --seed can be paired game by
                // game. Setups are pre-generated from the seed, so row g is the SAME setup
                // in both arms and McNemar applies to the discordant rows. The aggregate
                // win rates alone cannot support that test.
                else if (args[i] == "--csv" && i + 1 < args.Length) csvPath = args[++i];
                else if (int.TryParse(args[i], out var g)) games = g;
            }

            Console.WriteLine($"[search-test] {games} games vs HeuristicBot | decision every {interval} ticks, " +
                              $"horizon {horizon} ticks, {rollouts} rollout(s)/action, headstart={headstart}");
            Console.WriteLine($"              prior={(usePrior ? $"HeuristicBot (override margin {margin}, macro margin {(double.IsNaN(macroMargin) ? margin : macroMargin)})" : "none")}, " +
                              $"save-invest macro={(useMacro ? "on" : "off")}, " +
                              $"press-advantage macro={(usePress ? "on" : "off")}, " +
                              $"maxDecisionMs={(maxDecisionMs > 0 ? maxDecisionMs.ToString() : "unlimited")}, " +
                              $"coresPerDecision={intraThreads}, " +
                              $"eval={(linearEval ? "LINEAR (pre-audit)" : refitEval ? "LOGISTIC REFIT (2026-08-05)" : "logistic (deployed)")}");
            if (useUpgradeMacro)
                Console.WriteLine($"              GADGET-UPGRADE MACRO on, margin {(double.IsNaN(upgradeMargin) ? "= macro margin" : upgradeMargin.ToString())}");
            if (suppression != GadgetSuppression.None)
                Console.WriteLine($"              GADGET SUPPRESSION: {suppression}");
            if (noDefGadget)
                Console.WriteLine("              DEFENCE GADGET SUPPRESSED for the search bot (prior + rollout + action 12)");
            if (staleTicks > 0)
                Console.WriteLine($"              ASYNC STALENESS: decisions committed {staleTicks} tick(s) " +
                                  $"({staleTicks * 33.3:F0} ms) after the state they were computed from");
            if (deepMacro)
                Console.WriteLine($"              STAGE 1b: DEEP macro eval on -- {deepPlayouts} bounded play-to-completion " +
                                  $"rollout(s)/branch, commit window {deepCommitTicks} ticks");
            if (macroRandomRate > 0)
                Console.WriteLine($"              STAGE 0: save-macro fired at RANDOM on {macroRandomRate:P1} of {(macroRandomAffordable ? "AFFORDABLE " : "")}decisions" +
                                  $"{(useMacro ? "  *** WARNING: --no-macro not set, macro is ALSO selectable — arms are confounded ***" : " (selection deleted)")}");
            Console.WriteLine($"              rollout policy: own={ownRollout}, opponent={oppRollout}" +
                              $"{(ownRollout == RolloutPolicyKind.Saving || oppRollout == RolloutPolicyKind.Saving ? $", saveCommit={saveCommit}" : "")}");
            Console.WriteLine($"              {threads} threads on {Environment.ProcessorCount} logical cores\n");

            RolloutSearchOpponent.UseLinearEval = linearEval;
            RolloutSearchOpponent.UseRefitEval  = refitEval;

            if (evalWeights != null)
            {
                var w = evalWeights.Split(',');
                if (w.Length != 6) { Console.WriteLine("--eval-weights needs 6 comma-separated values: hp,income,money,army,gadget,repair"); return; }
                GameState.LogitWeightHp     = float.Parse(w[0]);
                GameState.LogitWeightIncome = float.Parse(w[1]);
                GameState.LogitWeightMoney  = float.Parse(w[2]);
                GameState.LogitWeightArmy   = float.Parse(w[3]);
                GameState.LogitWeightGadget = float.Parse(w[4]);
                GameState.LogitWeightRepair = float.Parse(w[5]);
                Console.WriteLine($"              eval weights OVERRIDDEN: hp={w[0]} income={w[1]} money={w[2]} army={w[3]} gadget={w[4]} repair={w[5]}");
            }

            var teams = (TeamColour[])Enum.GetValues(typeof(TeamColour));
            var offense = new[] { "nuke", "firebomb", "snipe", "freeze" };
            var defense = new[] { "heal", "reinforcements", "speed", "wall" };

            // ── Pre-generate every setup from the seed, on ONE thread ──────────────
            // Games are fully independent, so they can run in parallel — but only if the
            // setups are drawn deterministically first. Drawing from a shared Random
            // inside parallel workers would make results depend on thread scheduling, and
            // an unreproducible benchmark is worth very little (see the ladder).
            var rng = new Random(seed);
            var setups = new (int gameSeed, int timeSkip, bool searchIsP1, TeamColour map,
                              TeamColour teamA, string offA, string defA,
                              TeamColour teamB, string offB, string defB)[games];
            for (int g = 0; g < games; g++)
            {
                int ts = headstart ? Math.Max(rng.Next(-8, 9), 0) : 0;
                setups[g] = (rng.Next(), ts, g % 2 == 0,
                             teams[rng.Next(teams.Length)],
                             teams[rng.Next(teams.Length)], offense[rng.Next(offense.Length)], defense[rng.Next(defense.Length)],
                             teams[rng.Next(teams.Length)], offense[rng.Next(offense.Length)], defense[rng.Next(defense.Length)]);
            }

            var results = new (bool win, bool draw, bool timeLimit, long gameTicks, long wallMs,
                               long decisions, long rollouts, long simTicks, double spread,
                               long flat, long wait, long overrides, long macro, long press, long arma,
                               double earnedInv, double oppInv,
                               // Stage 0: separates "invested more" from "attacked less".
                               long units, long oppUnits, double spend, double oppSpend,
                               long deepPromo, long deepVeto, long deepRollouts, long deepTicks, long upgrade)[games];

            var sw = Stopwatch.StartNew();
            int completed = 0;

            Parallel.For(0, games, new ParallelOptions { MaxDegreeOfParallelism = threads }, g =>
            {
                var s = setups[g];
                int searchSide = s.searchIsP1 ? 1 : 2;

                var state = new GameState(s.map, new Random(s.gameSeed));
                state.Player1 = new PlayerState(s.timeSkip);
                state.Player2 = new PlayerState(s.timeSkip);
                string upg = s.timeSkip > 5 ? "_3" : s.timeSkip > 3 ? "_2" : "";

                state.Player1.Side = 1;
                state.Player1.Team = s.teamA;
                state.Player1.SetLoadout(new[] { s.offA + upg, s.defA + upg,
                    GameDataManager.GetSignatureGadgetIdForTeam(s.teamA) + upg });
                state.Player2.Side = 2;
                state.Player2.Team = s.teamB;
                state.Player2.SetLoadout(new[] { s.offB + upg, s.defB + upg,
                    GameDataManager.GetSignatureGadgetIdForTeam(s.teamB) + upg });

                state.CurrentTick = 30 * 30 * s.timeSkip;
                long startTick = state.CurrentTick;
                // Headstart grants both sides free investments, so record the starting
                // count and report EARNED invests only — see the ladder for why.
                int searchStartInv = (s.searchIsP1 ? state.Player1 : state.Player2).InvestmentCount;
                int oppStartInv = (s.searchIsP1 ? state.Player2 : state.Player1).InvestmentCount;
                var engine = new GameEngine(state, null, s.gameSeed);

                var searcher = new RolloutSearchOpponent(searchSide, interval, horizon, rollouts, s.gameSeed,
                                                         usePrior, margin, useMacro, usePress,
                                                         maxDecisionMs, intraThreads, macroMargin,
                                                         pressWave, pressMinTier, pressPeak, pressOff,
                                                         pressLo, pressHi, pressWaveCommit,
                                                         useArma, armaMargin,
                                                         ownRollout, oppRollout, saveCommit,
                                                         macroRandomRate, macroRandomAffordable,
                                                         deepMacro, deepPlayouts, deepCommitTicks,
                                                         staleTicks, noDefGadget, suppression,
                                                         useUpgradeMacro, upgradeMargin);
                var heuristic = new HeuristicBotAdapter(s.searchIsP1 ? 2 : 1);

                var gameSw = Stopwatch.StartNew();
                while (!state.IsGameOver)
                {
                    engine.Tick();
                    if (s.searchIsP1) { searcher.Update(engine); heuristic.Update(engine); }
                    else { heuristic.Update(engine); searcher.Update(engine); }
                }
                gameSw.Stop();

                results[g] = (state.WinnerSide == searchSide, state.WinnerSide == 0, state.IsTimeLimit,
                              state.CurrentTick - startTick, gameSw.ElapsedMilliseconds,
                              searcher.Decisions, searcher.Rollouts, searcher.SimulatedTicks,
                              searcher.ScoreSpreadSum, searcher.FlatDecisions, searcher.WaitDecisions,
                              searcher.Overrides, searcher.MacroChosen, searcher.PressChosen, searcher.ArmageddonChosen,
                              (s.searchIsP1 ? state.Player1 : state.Player2).InvestmentCount - searchStartInv,
                              (s.searchIsP1 ? state.Player2 : state.Player1).InvestmentCount - oppStartInv,
                              engine.UnitsPurchased[searchSide], engine.UnitsPurchased[searchSide == 1 ? 2 : 1],
                              engine.MoneySpentOnUnits[searchSide], engine.MoneySpentOnUnits[searchSide == 1 ? 2 : 1],
                              searcher.DeepPromotions, searcher.DeepVetoes, searcher.DeepRollouts, searcher.DeepSimTicks,
                              searcher.UpgradeChosen);

                int done = Interlocked.Increment(ref completed);
                if (done % Math.Max(1, games / 20) == 0)
                    Console.WriteLine($"  ... {done}/{games} games ({sw.Elapsed.TotalSeconds:F0}s elapsed)");
            });

            int wins = 0, losses = 0, draws = 0, timeLimitGames = 0, timeLimitWins = 0;
            long totalDecisions = 0, totalRollouts = 0, totalSimTicks = 0, totalGameTicks = 0;
            double totalSpread = 0; long totalFlat = 0, totalWait = 0, totalOverrides = 0, totalMacro = 0, totalPress = 0, totalArma = 0;
            double totalEarnedInvests = 0, totalOppInvests = 0; long totalUpgrade = 0;

            for (int g = 0; g < games; g++)
            {
                var r = results[g];
                if (r.win) wins++; else if (r.draw) draws++; else losses++;
                if (r.timeLimit) { timeLimitGames++; if (r.win) timeLimitWins++; }

                totalDecisions += r.decisions;
                totalRollouts += r.rollouts;
                totalSimTicks += r.simTicks;
                totalGameTicks += r.gameTicks;
                totalSpread += r.spread;
                totalFlat += r.flat;
                totalWait += r.wait;
                totalOverrides += r.overrides;
                totalMacro += r.macro;
                totalPress += r.press;
                totalArma += r.arma;
                totalUpgrade += r.upgrade;
                totalEarnedInvests += r.earnedInv;
                totalOppInvests += r.oppInv;

                Console.WriteLine($"  game {g,3}: {(r.win ? "WIN " : r.draw ? "DRAW" : "loss")}" +
                                  $"  {r.gameTicks / 30.0,6:F1}s game  " +
                                  $"{r.wallMs,7}ms wall  {r.decisions,5} decisions" +
                                  $"{(r.timeLimit ? "  [tick cap]" : "")}");
            }
            sw.Stop();

            if (csvPath != null)
            {
                using var csv = new StreamWriter(csvPath);
                csv.WriteLine("game,win,draw,time_limit,game_ticks,decisions,overrides,macro,press,arma," +
                              "earned_inv,opp_inv,units,opp_units,spend,opp_spend");
                for (int g = 0; g < games; g++)
                {
                    var r = results[g];
                    csv.WriteLine($"{g},{(r.win ? 1 : 0)},{(r.draw ? 1 : 0)},{(r.timeLimit ? 1 : 0)}," +
                                  $"{r.gameTicks},{r.decisions},{r.overrides},{r.macro},{r.press},{r.arma}," +
                                  $"{r.earnedInv:F0},{r.oppInv:F0},{r.units},{r.oppUnits}," +
                                  $"{r.spend:F1},{r.oppSpend:F1}");
                }
                Console.WriteLine($"\n  [search-test] wrote per-game outcomes to {csvPath}");
            }

            int n = wins + losses + draws;
            var (lo, hi) = Ladder.WilsonInterval(wins, n);
            Console.WriteLine($"\n  win rate vs HeuristicBot : {(double)wins / n:P1} [{lo:P1}, {hi:P1}]  ({wins}W/{losses}L/{draws}D)");
            Console.WriteLine($"  HeuristicBot's own ladder result vs itself is ~46-56%, and it beats every");
            Console.WriteLine($"  RL checkpoint 83-100%. Anything above ~20% here already exceeds every");
            Console.WriteLine($"  learned policy this project has produced.");
            Console.WriteLine();
            Console.WriteLine($"  decisive wins       : {wins - timeLimitWins} of {wins}  ({timeLimitWins} awarded on castle HP at the {GameEngine.MAX_TICKS / 30}s tick cap)");
            Console.WriteLine($"  games hitting cap   : {timeLimitGames} of {n} ({(double)timeLimitGames / n:P0}) — high values mean the bot stalls rather than closing games out");
            Console.WriteLine();
            Console.WriteLine($"  wall clock          : {sw.Elapsed.TotalSeconds:F1}s total, {sw.Elapsed.TotalSeconds / n:F2}s per game");
            Console.WriteLine($"  decisions           : {totalDecisions} ({(double)totalDecisions / n:F0} per game)");
            Console.WriteLine($"  rollouts            : {totalRollouts} ({(double)totalRollouts / Math.Max(1, totalDecisions):F1} per decision)");
            Console.WriteLine($"  simulated ticks     : {totalSimTicks:N0} vs {totalGameTicks:N0} real ({(double)totalSimTicks / Math.Max(1, totalGameTicks):F0}x overhead)");
            // MUST use summed per-game time, not wall clock. Wall clock covers all threads
            // running concurrently, so dividing it by total decisions understates the real
            // single-game cost by roughly the thread count — which is how a 300-500ms
            // decision got reported as 16.75ms and shipped to the live game with a 167ms
            // budget. Per-game Stopwatch time is the number that matters for latency.
            long totalGameWallMs = 0;
            for (int g = 0; g < games; g++) totalGameWallMs += results[g].wallMs;
            double msPerDecision = (double)totalGameWallMs / Math.Max(1, totalDecisions);
            Console.WriteLine($"  ms per decision     : {msPerDecision:F1}   (single-game cost — this is the latency figure)");
            Console.WriteLine($"  slowest game        : {results.Max(r => r.decisions > 0 ? (double)r.wallMs / r.decisions : 0):F0} ms/decision");
            Console.WriteLine();
            Console.WriteLine($"  DOES SEARCH SEE ANYTHING?");
            Console.WriteLine($"  avg best-worst gap  : {totalSpread / Math.Max(1, totalDecisions):F5}   (evaluator output is a 0-1 win probability)");
            Console.WriteLine($"  flat decisions      : {(double)totalFlat / Math.Max(1, totalDecisions):P1}  (all candidates scored identically -> argmax was arbitrary)");
            Console.WriteLine($"  chose to wait       : {(double)totalWait / Math.Max(1, totalDecisions):P1}");
            Console.WriteLine($"  overrode the prior  : {(double)totalOverrides / Math.Max(1, totalDecisions):P1}  (otherwise HeuristicBot played the move)");
            Console.WriteLine($"  chose save-macro    : {(double)totalMacro / Math.Max(1, totalDecisions):P1}");
            Console.WriteLine($"  chose press-macro   : {(double)totalPress / Math.Max(1, totalDecisions):P1}  (converting the economic lead into an attack)");
            Console.WriteLine($"  chose arma-macro    : {(double)totalArma / Math.Max(1, totalDecisions):P1}  (committing to the race to investment 8)");
            // MUST be reported: a null result is ambiguous without it. A flat win rate with
            // 0% selection means search DECLINED the option; a flat win rate with a real
            // selection rate means it took it and it did not pay. Different conclusions.
            Console.WriteLine($"  chose upgrade-macro : {(double)totalUpgrade / Math.Max(1, totalDecisions):P1}  (farming XP toward the next gadget tier)");
            Console.WriteLine();
            // Stage 0 decomposition: an economic lead can be built by investing more OR by
            // the opponent investing less, and a macro that only suppresses its own attacking
            // shows up here rather than in the invest line.
            double units = 0, oppUnits = 0, spend = 0, oppSpend = 0;
            for (int g = 0; g < games; g++)
            {
                units += results[g].units; oppUnits += results[g].oppUnits;
                spend += results[g].spend; oppSpend += results[g].oppSpend;
            }
            if (deepMacro)
            {
                double promo = 0, veto = 0, dr = 0, dt = 0;
                for (int g = 0; g < games; g++)
                { promo += results[g].deepPromo; veto += results[g].deepVeto; dr += results[g].deepRollouts; dt += results[g].deepTicks; }
                Console.WriteLine($"  DEEP promotions/game: {promo / n:F1}  (deep fired the macro where shallow would not)");
                Console.WriteLine($"  DEEP vetoes/game    : {veto / n:F1}  (shallow chose the macro, deep overruled)");
                Console.WriteLine($"  deep rollouts/game  : {dr / n:F0}, {dt / Math.Max(1, dr):F0} ticks each");
                Console.WriteLine();
            }
            Console.WriteLine($"  units bought/game   : {units / n:F1}  vs HeuristicBot's {oppUnits / n:F1}");
            Console.WriteLine($"  spent on units/game : {spend / n:F0}  vs HeuristicBot's {oppSpend / n:F0}");
            Console.WriteLine();
            Console.WriteLine($"  earned invests/game : {totalEarnedInvests / n:F2}  vs HeuristicBot's {totalOppInvests / n:F2}");
            Console.WriteLine($"  (HeuristicBot manages ~4.1-5.1 earned in ladder play; Marc's winning line");
            Console.WriteLine($"   is to out-invest it, so this number is the strategy working or not.)");
            Console.WriteLine();
            Console.WriteLine($"  A tiny gap means the leaf evaluator cannot distinguish the available moves,");
            Console.WriteLine($"  so more search cannot help however much compute it is given.");
            Console.WriteLine();
            Console.WriteLine($"  For the web game, a decision must fit comfortably inside {interval} ticks");
            Console.WriteLine($"  = {interval / 30.0 * 1000:F0}ms of real time, with several concurrent games sharing the box.");
        }
    }
}
