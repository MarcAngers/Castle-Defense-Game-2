using System.Diagnostics;
using CastleDefense.Engine;
using CastleDefense.Engine.Bot;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Models;

namespace CastleDefense.BotArena
{
    /// <summary>
    /// STRATEGY MATRIX: plays each of Marc's three win conditions against each other.
    ///
    /// WHY THIS EXISTS. Every benchmark on this project measures against ONE opponent, and
    /// on 2026-08-06 that turned out to invalidate a whole line of work: the rebuilt blitz
    /// macro measured as a 21-point regression in search-test, but search-test's only
    /// opponent is HeuristicBot, which plays SUSTAINED PRESSURE -- and in Marc's model
    /// sustained pressure is precisely what BEATS a blitz. So the result was what his model
    /// predicts, not evidence against the blitz. A single-opponent yardstick cannot
    /// evaluate a rock/paper/scissors strategy, full stop.
    ///
    /// This measures the 3x3 directly. Marc's model claims:
    ///     ARMAGEDDON beats PRESSURE   (pressure cannot close, economy wins the long game)
    ///     BLITZ      beats ARMAGEDDON (rushed down before the bank matures)
    ///     PRESSURE   beats BLITZ      (a standing stream absorbs the wave)
    /// If those three hold, a matchup prior is worth building. If they do not, the prior
    /// would be built on a model the game does not actually implement, and we should find
    /// that out for ~20 minutes of compute rather than after writing it.
    ///
    /// HOW THE STRATEGIES ARE FORCED. Not by hard-coding a policy -- by biasing the margins
    /// that already govern which macro the search commits to, and disabling the others.
    /// Primitive overrides are switched off in all three (margin 1.0 is unreachable for a
    /// 0-1 evaluator), because the 2026-08-04 sweep showed search's primitive suggestions
    /// are actively harmful and they would otherwise contaminate every cell.
    ///
    /// These are PROXIES and should be read as such. "Armageddon" is aggressive saving with
    /// HeuristicBot still handling defence, not a literal 8-investment commitment; there is
    /// no full Armageddon macro yet. "Pressure" is plain HeuristicBot, which is what Marc
    /// identifies as the bot's existing default. Only "Blitz" is a purpose-built macro.
    ///
    /// Usage: strategy-matrix [gamesPerCell] [--seed N] [--threads N]
    /// </summary>
    public static class StrategyMatrix
    {
        private enum Strat { Armageddon, Blitz, Pressure }

        // Investment window in which the blitz proxy may fire. The first run used 0-8
        // (blitz at any time) and the blitz lost to everything -- but Marc's whole point
        // is that a blitz BEFORE investment 6 is a mistake, because tier-6 units are what
        // make it lethal and they are not affordable earlier. So 0-8 may have measured
        // premature blitzing rather than blitzing. Configurable so the gated version can
        // be compared against the ungated one.
        private static int BlitzLo = 0, BlitzHi = 8;

        // Margin applied OUTSIDE the window. This must be high for the window to mean
        // anything: the 2026-08-06 gated run came back BYTE-IDENTICAL to the ungated one
        // because both the peak and off-peak margins were 0.0, so MarginFor returned 0.0
        // either way and the gate suppressed nothing. 1.0 is unreachable for a 0-1
        // evaluator, i.e. "never blitz outside the window".
        private static double BlitzOffPeak = 0.0;

        private static RolloutSearchOpponent Make(Strat s, int side, int seed) => s switch
        {
            // Save readily (macro margin 0 = take the investment whenever it beats the
            // prior at all), never blitz, no primitive overrides.
            Strat.Armageddon => new RolloutSearchOpponent(side, 15, 300, 1, seed,
                usePrior: true, overrideMargin: 1.0, useMacro: true, usePressMacro: false,
                maxDecisionMs: 0, maxParallelism: 1, macroMargin: 0.0),

            // Blitz readily, never bank for investments, no primitive overrides.
            Strat.Blitz => new RolloutSearchOpponent(side, 15, 300, 1, seed,
                usePrior: true, overrideMargin: 1.0, useMacro: false, usePressMacro: true,
                maxDecisionMs: 0, maxParallelism: 1, macroMargin: 1.0,
                pressWaveUnits: 6, pressMinTier: 6,
                pressPeakMargin: 0.0, pressOffPeakMargin: BlitzOffPeak,
                pressPeakMinInvest: BlitzLo, pressPeakMaxInvest: BlitzHi, pressWaveCommit: true),

            // No macros at all -> the search never intervenes -> plain HeuristicBot.
            _ => new RolloutSearchOpponent(side, 15, 300, 1, seed,
                usePrior: true, overrideMargin: 1.0, useMacro: false, usePressMacro: false,
                maxDecisionMs: 0, maxParallelism: 1, macroMargin: 1.0),
        };

        public static void Run(string[] args)
        {
            int gamesPerCell = 60, seed = 20260806;
            int threads = Math.Max(1, Environment.ProcessorCount - 2);
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--seed" && i + 1 < args.Length) seed = int.Parse(args[++i]);
                else if (args[i] == "--threads" && i + 1 < args.Length) threads = int.Parse(args[++i]);
                else if (args[i] == "--blitz-gate" && i + 1 < args.Length)
                { var bw = args[++i].Split('-'); BlitzLo = int.Parse(bw[0]); BlitzHi = int.Parse(bw[1]);
                  BlitzOffPeak = 1.0; }   // a gate implies suppression outside it
                else if (int.TryParse(args[i], out var g)) gamesPerCell = g;
            }

            var strats = (Strat[])Enum.GetValues(typeof(Strat));
            Console.WriteLine($"[strategy-matrix] blitz window = investment {BlitzLo}-{BlitzHi}");
            Console.WriteLine($"[strategy-matrix] {strats.Length}x{strats.Length} cells, {gamesPerCell} games each " +
                              $"({strats.Length * strats.Length * gamesPerCell} total), seed {seed}, {threads} threads");
            Console.WriteLine("[strategy-matrix] rows = ROW strategy's win rate against the COLUMN strategy\n");

            var teams = (TeamColour[])Enum.GetValues(typeof(TeamColour));
            var offense = new[] { "nuke", "firebomb", "snipe", "freeze" };
            var defense = new[] { "heal", "reinforcements", "speed", "wall" };

            // Setups drawn once, on one thread, and REUSED BY EVERY CELL so the whole
            // matrix is paired: a difference between cells is the strategy pairing, not
            // which loadouts happened to be dealt.
            var rng = new Random(seed);
            var setups = new (int gameSeed, TeamColour map, TeamColour tA, string oA, string dA,
                              TeamColour tB, string oB, string dB)[gamesPerCell];
            for (int g = 0; g < gamesPerCell; g++)
                setups[g] = (rng.Next(), teams[rng.Next(teams.Length)],
                             teams[rng.Next(teams.Length)], offense[rng.Next(offense.Length)], defense[rng.Next(defense.Length)],
                             teams[rng.Next(teams.Length)], offense[rng.Next(offense.Length)], defense[rng.Next(defense.Length)]);

            var wins = new int[strats.Length, strats.Length];
            var played = new int[strats.Length, strats.Length];
            var capped = new int[strats.Length, strats.Length];

            // One work item per GAME, not per cell -- cells vary hugely in cost (a stall
            // mirror runs to the tick cap, a blitz kill ends in seconds) and per-cell
            // scheduling leaves workers idle behind the slow ones.
            var work = new List<(int r, int c, int g)>();
            for (int r = 0; r < strats.Length; r++)
                for (int c = 0; c < strats.Length; c++)
                    for (int g = 0; g < gamesPerCell; g++) work.Add((r, c, g));

            var sw = Stopwatch.StartNew();
            int done = 0;
            Parallel.ForEach(work, new ParallelOptions { MaxDegreeOfParallelism = threads }, w =>
            {
                var s = setups[w.g];
                bool rowIsP1 = w.g % 2 == 0;          // alternate seats to cancel side bias
                int rowSide = rowIsP1 ? 1 : 2, colSide = rowIsP1 ? 2 : 1;

                var state = new GameState(s.map, new Random(s.gameSeed));
                state.Player1 = new PlayerState();
                state.Player2 = new PlayerState();
                state.Player1.Side = 1; state.Player2.Side = 2;
                var (t1, o1, d1) = rowIsP1 ? (s.tA, s.oA, s.dA) : (s.tB, s.oB, s.dB);
                var (t2, o2, d2) = rowIsP1 ? (s.tB, s.oB, s.dB) : (s.tA, s.oA, s.dA);
                state.Player1.Team = t1;
                state.Player1.SetLoadout(new[] { o1, d1, GameDataManager.GetSignatureGadgetIdForTeam(t1) });
                state.Player2.Team = t2;
                state.Player2.SetLoadout(new[] { o2, d2, GameDataManager.GetSignatureGadgetIdForTeam(t2) });

                var engine = new GameEngine(state, null, s.gameSeed);
                var row = Make(strats[w.r], rowSide, s.gameSeed);
                var col = Make(strats[w.c], colSide, s.gameSeed ^ 0x5bf03);

                while (!state.IsGameOver)
                {
                    engine.Tick();
                    if (rowIsP1) { row.Update(engine); col.Update(engine); }
                    else { col.Update(engine); row.Update(engine); }
                }

                lock (wins)
                {
                    played[w.r, w.c]++;
                    if (state.WinnerSide == rowSide) wins[w.r, w.c]++;
                    if (state.IsTimeLimit) capped[w.r, w.c]++;
                }
                int n = Interlocked.Increment(ref done);
                if (n % Math.Max(1, work.Count / 10) == 0)
                    Console.WriteLine($"  ... {n}/{work.Count} ({sw.Elapsed:mm\\:ss})");
            });

            Console.WriteLine($"\n  win rate of ROW vs COLUMN   ({gamesPerCell} games/cell, sides alternated)\n");
            Console.Write($"  {"",-12}");
            foreach (var c in strats) Console.Write($"{c,12}");
            Console.WriteLine();
            for (int r = 0; r < strats.Length; r++)
            {
                Console.Write($"  {strats[r],-12}");
                for (int c = 0; c < strats.Length; c++)
                {
                    double wr = played[r, c] > 0 ? 100.0 * wins[r, c] / played[r, c] : 0;
                    Console.Write($"{wr,11:F1}%");
                }
                Console.WriteLine();
            }

            Console.WriteLine($"\n  games hitting the tick cap:\n");
            Console.Write($"  {"",-12}");
            foreach (var c in strats) Console.Write($"{c,12}");
            Console.WriteLine();
            for (int r = 0; r < strats.Length; r++)
            {
                Console.Write($"  {strats[r],-12}");
                for (int c = 0; c < strats.Length; c++)
                    Console.Write($"{(played[r, c] > 0 ? 100.0 * capped[r, c] / played[r, c] : 0),11:F0}%");
                Console.WriteLine();
            }

            Console.WriteLine("\n  Marc's model predicts Armageddon>Pressure, Blitz>Armageddon, Pressure>Blitz.");
            Console.WriteLine("  The DIAGONAL is the control: a strategy against itself should sit near 50%.");
            Console.WriteLine($"\n  wall clock {sw.Elapsed.TotalMinutes:F1} min");
        }
    }
}
