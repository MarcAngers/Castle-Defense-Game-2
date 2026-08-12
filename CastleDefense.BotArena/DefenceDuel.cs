using CastleDefense.Engine;
using CastleDefense.Engine.Bot;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Models;

namespace CastleDefense.BotArena
{
    /// <summary>
    /// HEAD-TO-HEAD defence-gadget duel: same bot, same team, same offence gadget on both
    /// sides, differing ONLY in the defence gadget.
    ///
    /// WHY IT EXISTS (2026-08-11). The capability map found speed defence is the bot's
    /// largest single deficit — 23.7% against ~56.4% for heal/wall/reinforcements — but
    /// that number comes from the dashboard sweep, where a protagonist on a FIXED loadout
    /// plays opponents rolling RANDOM ones. That is "how does this pick do against the
    /// field", and a field average can be dragged around by which matchups happen to be
    /// common. Before spending the option-set lever on a speed macro, the deficit has to
    /// survive a direct matchup with everything else held equal.
    ///
    /// WHAT IS CONTROLLED. Team is identical on both sides (so no team-strength term),
    /// offence gadget is identical, signature follows team and is therefore identical, and
    /// sides are ALTERNATED across the pair so seat bias cancels exactly rather than
    /// approximately. Setups are pre-generated from the seed on one thread, so two runs at
    /// the same seed compare game-for-game and McNemar applies.
    ///
    /// THE SECOND ARM IS THE ONE THAT MATTERS. `--suppress-a` forbids side A from casting
    /// its defence gadget at all (HeuristicBotSettings.DisableDefenseGadget, plus removal
    /// of action 12 from the search candidate list, plus the same suppression inside the
    /// rollout policy — all three, or the arm measures nothing). That separates two
    /// explanations the win rate alone cannot:
    ///
    ///   never-cast BEATS cast-on-cooldown  => the current rule is actively harmful, and a
    ///                                         speed macro's headroom is at least that gap
    ///   never-cast TIES cast-on-cooldown   => the casting is not what costs; the deficit
    ///                                         is elsewhere and a macro buys less
    ///   never-cast LOSES to cast-on-cooldown => firing on cooldown is better than nothing;
    ///                                         speed is simply weak for this bot
    /// </summary>
    public static class DefenceDuel
    {
        public static void Run(string[] args)
        {
            int games = 200, seed = 4242;
            string defA = "speed", defB = "wall", offence = "nuke";
            string teamArg = null;                  // null => roll a team per setup
            bool suppressA = false, suppressB = false, headstart = false, p2First = false;
            bool useSearch = true;
            int spamTier = 0;   // >0 => TierSpamBaseline both sides (no proximity logic at all)
            int threads = Math.Max(1, Environment.ProcessorCount - 2);
            string csvPath = null;

            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--seed" && i + 1 < args.Length) seed = int.Parse(args[++i]);
                else if (args[i] == "--def-a" && i + 1 < args.Length) defA = args[++i].ToLowerInvariant();
                else if (args[i] == "--def-b" && i + 1 < args.Length) defB = args[++i].ToLowerInvariant();
                else if (args[i] == "--offence" && i + 1 < args.Length) offence = args[++i].ToLowerInvariant();
                else if (args[i] == "--team" && i + 1 < args.Length) teamArg = args[++i];
                else if (args[i] == "--suppress-a") suppressA = true;
                else if (args[i] == "--suppress-b") suppressB = true;
                else if (args[i] == "--heuristic") useSearch = false;
                // DIAGNOSTIC. A spam bot spawns one tier and never reads distances, so a
                // mirror of two of them isolates ENGINE residual from BOT-POLICY residual:
                // HeuristicBot's proximity thresholds compare left edges against a castle x
                // and so evaluate different real distances per seat (CLEANUP_BACKLOG).
                else if (args[i] == "--spam" && i + 1 < args.Length) { spamTier = int.Parse(args[++i]); useSearch = false; }
                // DIAGNOSTIC. Reverses the within-tick drive order (P2 polled before P1).
                // If a mirror's winner flips with this, the seat bias is move order --
                // the second bot to be polled sees the first one's action in the same
                // tick -- and not engine geometry.
                else if (args[i] == "--p2-first") p2First = true;
                else if (args[i] == "--headstart") headstart = true;
                else if (args[i] == "--threads" && i + 1 < args.Length) threads = int.Parse(args[++i]);
                else if (args[i] == "--csv" && i + 1 < args.Length) csvPath = args[++i];
                else if (int.TryParse(args[i], out var g)) games = g;
            }

            var teams = (TeamColour[])Enum.GetValues(typeof(TeamColour));
            TeamColour? fixedTeam = teamArg == null ? null : Enum.Parse<TeamColour>(teamArg, true);

            Console.WriteLine($"[defence-duel] {games} games | A={defA}{(suppressA ? " (NEVER CAST)" : "")} " +
                              $"vs B={defB}{(suppressB ? " (NEVER CAST)" : "")}");
            Console.WriteLine($"               bot={(useSearch ? "RolloutSearchBot (shipped: 15/300/0.10)" : "HeuristicBot")}, " +
                              $"offence={offence} both sides, team={(fixedTeam?.ToString() ?? "rolled per setup, IDENTICAL both sides")}, " +
                              $"headstart={headstart}");
            Console.WriteLine($"               sides alternated, paired setups from seed {seed}, {threads} threads\n");

            // Pre-generate every setup on ONE thread so the run is reproducible regardless
            // of scheduling — same discipline as search-test.
            var rng = new Random(seed);
            var setups = new (int gameSeed, int timeSkip, bool aIsP1, TeamColour map, TeamColour team)[games];
            for (int g = 0; g < games; g++)
            {
                int ts = headstart ? Math.Max(rng.Next(-8, 9), 0) : 0;
                setups[g] = (rng.Next(), ts, g % 2 == 0,
                             teams[rng.Next(teams.Length)],
                             fixedTeam ?? teams[rng.Next(teams.Length)]);
            }

            var results = new (bool aWin, bool draw, bool timeLimit, long ticks,
                               double aInv, double bInv, long aUnits, long bUnits)[games];

            var sw = System.Diagnostics.Stopwatch.StartNew();
            int completed = 0;

            Parallel.For(0, games, new ParallelOptions { MaxDegreeOfParallelism = threads }, g =>
            {
                var s = setups[g];
                int aSide = s.aIsP1 ? 1 : 2, bSide = s.aIsP1 ? 2 : 1;

                var state = new GameState(s.map, new Random(s.gameSeed));
                state.Player1 = new PlayerState(s.timeSkip);
                state.Player2 = new PlayerState(s.timeSkip);
                string upg = s.timeSkip > 5 ? "_3" : s.timeSkip > 3 ? "_2" : "";

                void Setup(PlayerState p, int side, string def)
                {
                    p.Side = side;
                    p.Team = s.team;
                    p.SetLoadout(new[] { offence + upg, def + upg,
                        GameDataManager.GetSignatureGadgetIdForTeam(s.team) + upg });
                }
                Setup(s.aIsP1 ? state.Player1 : state.Player2, aSide, defA);
                Setup(s.aIsP1 ? state.Player2 : state.Player1, bSide, defB);

                state.CurrentTick = 30 * 30 * s.timeSkip;
                long startTick = state.CurrentTick;
                var aP = aSide == 1 ? state.Player1 : state.Player2;
                var bP = bSide == 1 ? state.Player1 : state.Player2;
                int aStart = aP.InvestmentCount, bStart = bP.InvestmentCount;

                var engine = new GameEngine(state, null, s.gameSeed);

                object MakeBot(int side, bool suppress) => spamTier > 0
                    ? new TierSpamBaseline(side, spamTier)
                    : useSearch
                    ? new RolloutSearchOpponent(side, 15, 300, 1, s.gameSeed, true, 0.10,
                                                suppressDefenceGadget: suppress)
                    : (object)new HeuristicBotAdapter(side,
                        suppress ? new HeuristicBotSettings { DisableDefenseGadget = true } : null);

                var botA = MakeBot(aSide, suppressA);
                var botB = MakeBot(bSide, suppressB);
                void Tick(object bot)
                {
                    if (bot is RolloutSearchOpponent r) r.Update(engine);
                    else ((IArenaOpponent)bot).Update(engine);
                }

                while (!state.IsGameOver)
                {
                    engine.Tick();
                    // Drive in SEAT order, not A-then-B, so which bot moves first inside a
                    // tick depends on the seat and therefore alternates with it. Driving A
                    // first every game would hand A a systematic within-tick advantage that
                    // side alternation would not cancel.
                    bool aFirst = p2First ? !s.aIsP1 : s.aIsP1;
                    if (aFirst) { Tick(botA); Tick(botB); }
                    else { Tick(botB); Tick(botA); }
                }

                results[g] = (state.WinnerSide == aSide, state.WinnerSide == 0, state.IsTimeLimit,
                              state.CurrentTick - startTick,
                              aP.InvestmentCount - aStart, bP.InvestmentCount - bStart,
                              engine.UnitsPurchased[aSide], engine.UnitsPurchased[bSide]);

                int done = Interlocked.Increment(ref completed);
                if (done % Math.Max(1, games / 10) == 0)
                    Console.WriteLine($"  ... {done}/{games} ({sw.Elapsed.TotalSeconds:F0}s)");
            });
            sw.Stop();

            int aWins = results.Count(r => r.aWin), draws = results.Count(r => r.draw);
            int bWins = games - aWins - draws;
            int caps = results.Count(r => r.timeLimit);
            var (lo, hi) = Ladder.WilsonInterval(aWins, games);

            // Seat split, the instrument check: with sides alternated and identical teams,
            // a large P1/P2 skew would mean the harness, not the gadget, is doing the work.
            int aAsP1 = 0, aAsP1Wins = 0;
            for (int g = 0; g < games; g++)
                if (setups[g].aIsP1) { aAsP1++; if (results[g].aWin) aAsP1Wins++; }
            int aAsP2 = games - aAsP1, aAsP2Wins = aWins - aAsP1Wins;

            Console.WriteLine($"\n  {defA}{(suppressA ? "(no-cast)" : "")} vs {defB}{(suppressB ? "(no-cast)" : "")}" +
                              $" : {(double)aWins / games:P1} [{lo:P1}, {hi:P1}]  ({aWins}W/{bWins}L/{draws}D)");
            Console.WriteLine($"  seat check          : A as P1 {aAsP1Wins}/{aAsP1} ({(aAsP1 > 0 ? 100.0 * aAsP1Wins / aAsP1 : 0):F1}%), " +
                              $"A as P2 {aAsP2Wins}/{aAsP2} ({(aAsP2 > 0 ? 100.0 * aAsP2Wins / aAsP2 : 0):F1}%)");
            Console.WriteLine($"  games hitting cap   : {caps} of {games} ({100.0 * caps / games:F0}%)");
            Console.WriteLine($"  earned invests      : A {results.Average(r => r.aInv):F2}  vs  B {results.Average(r => r.bInv):F2}");
            Console.WriteLine($"  units bought        : A {results.Average(r => (double)r.aUnits):F1}  vs  B {results.Average(r => (double)r.bUnits):F1}");
            Console.WriteLine($"  avg game length     : {results.Average(r => (double)r.ticks) / 30.0:F1}s");
            Console.WriteLine($"  wall clock          : {sw.Elapsed.TotalSeconds:F1}s");

            if (csvPath != null)
            {
                using var csv = new StreamWriter(csvPath);
                csv.WriteLine("game,a_is_p1,team,a_win,draw,time_limit,ticks,a_inv,b_inv,a_units,b_units");
                for (int g = 0; g < games; g++)
                {
                    var r = results[g];
                    csv.WriteLine($"{g},{(setups[g].aIsP1 ? 1 : 0)},{setups[g].team}," +
                                  $"{(r.aWin ? 1 : 0)},{(r.draw ? 1 : 0)},{(r.timeLimit ? 1 : 0)}," +
                                  $"{r.ticks},{r.aInv:F0},{r.bInv:F0},{r.aUnits},{r.bUnits}");
                }
                Console.WriteLine($"  [defence-duel] wrote per-game outcomes to {csvPath}");
            }
        }
    }
}
