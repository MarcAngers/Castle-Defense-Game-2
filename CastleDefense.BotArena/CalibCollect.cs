using System.Text;
using CastleDefense.Engine;
using CastleDefense.Engine.Bot;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Models;

namespace CastleDefense.BotArena
{
    /// <summary>
    /// Collects evaluator calibration data from games the CURRENT bots actually play.
    ///
    /// WHY THIS EXISTS RATHER THAN REUSING Simulation's --collect-calibration:
    /// that collector's player pool is league ONNX brains, RandomBot, AntiSpamBot and
    /// SpamBot tiers 1/2/4/6/8. HeuristicBot is not in it and cannot be — the pool is
    /// typed `Func&lt;GameState,int,int&gt;` (one action id per call), while HeuristicBot
    /// drives the engine directly and may spawn several units in a single decision.
    /// So the deployed evaluator has never seen a position from the strongest agent on
    /// the project, which is precisely the distribution RolloutSearchBot's leaves are
    /// drawn from.
    ///
    /// This mode is DIAGNOSTIC ONLY. It writes data; it does not fit or change any
    /// weights. The question it exists to answer is falsifiable: is EvaluateBoard()
    /// well calibrated on HeuristicBot-driven positions? If yes, nothing needs doing.
    ///
    /// Emits `game_id` and `tick`, which Simulation's exporter does not. Without them
    /// train_evaluator.py / audit_evaluator.py cannot fire their autocorrelation
    /// thinning, so all ~1.7M within-game frames of calib_data.csv are used raw and a
    /// few thousand games masquerade as 1.7M independent observations. Any standard
    /// error computed from that file is meaningless. With these two columns the
    /// existing thinning path in audit_evaluator.load() works as intended.
    ///
    /// Usage: calib-collect [games] [--seed N] [--out PATH] [--mode selfplay|search]
    ///                      [--sample TICKS] [--threads N] [--margin M]
    /// </summary>
    public static class CalibCollect
    {
        public static void Run(string[] args)
        {
            int games = 400, seed = 20260804, sample = 30;
            string outPath = "calib_heuristic.csv";
            string mode = "selfplay";
            double margin = 0.10;
            int threads = Math.Max(1, Environment.ProcessorCount - 2);

            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--seed" && i + 1 < args.Length) seed = int.Parse(args[++i]);
                else if (args[i] == "--out" && i + 1 < args.Length) outPath = args[++i];
                else if (args[i] == "--mode" && i + 1 < args.Length) mode = args[++i];
                else if (args[i] == "--sample" && i + 1 < args.Length) sample = int.Parse(args[++i]);
                else if (args[i] == "--threads" && i + 1 < args.Length) threads = int.Parse(args[++i]);
                else if (args[i] == "--margin" && i + 1 < args.Length) margin = double.Parse(args[++i]);
                else if (int.TryParse(args[i], out var g)) games = g;
            }

            bool searchMode = mode == "search";
            Console.WriteLine($"[calib-collect] {games} games, mode={mode}, sampling every {sample} ticks");
            Console.WriteLine($"                seed={seed}, threads={threads}, out={outPath}");
            if (searchMode) Console.WriteLine($"                search side uses margin {margin} (the deployed config)");
            Console.WriteLine();

            var teams = (TeamColour[])Enum.GetValues(typeof(TeamColour));
            var offense = new[] { "nuke", "firebomb", "snipe", "freeze" };
            var defense = new[] { "heal", "reinforcements", "speed", "wall" };

            // Setups drawn on ONE thread so the run is reproducible regardless of how the
            // work is scheduled — same reason search-test pre-generates them.
            var rng = new Random(seed);
            var setups = new (int gameSeed, TeamColour map,
                              TeamColour teamA, string offA, string defA,
                              TeamColour teamB, string offB, string defB)[games];
            for (int g = 0; g < games; g++)
                setups[g] = (rng.Next(), teams[rng.Next(teams.Length)],
                             teams[rng.Next(teams.Length)], offense[rng.Next(offense.Length)], defense[rng.Next(defense.Length)],
                             teams[rng.Next(teams.Length)], offense[rng.Next(offense.Length)], defense[rng.Next(defense.Length)]);

            var rows = new List<string>[games];
            int completed = 0;

            Parallel.For(0, games, new ParallelOptions { MaxDegreeOfParallelism = threads }, g =>
            {
                var s = setups[g];
                var state = new GameState(s.map, new Random(s.gameSeed));
                state.Player1 = new PlayerState();
                state.Player2 = new PlayerState();
                state.Player1.Side = 1;
                state.Player1.Team = s.teamA;
                state.Player1.SetLoadout(new[] { s.offA, s.defA,
                    GameDataManager.GetSignatureGadgetIdForTeam(s.teamA) });
                state.Player2.Side = 2;
                state.Player2.Team = s.teamB;
                state.Player2.SetLoadout(new[] { s.offB, s.defB,
                    GameDataManager.GetSignatureGadgetIdForTeam(s.teamB) });

                var engine = new GameEngine(state, null, s.gameSeed);

                // P1 is the search bot in search mode so the recorded features — which are
                // all computed from P1's perspective — describe the agent under test.
                var p1 = searchMode
                    ? (object)new RolloutSearchOpponent(1, 15, 300, 1, s.gameSeed, true, margin)
                    : new HeuristicBotAdapter(1);
                var p2 = new HeuristicBotAdapter(2);

                var samples = new List<(long tick, (float Hp, float Income, float Money, float Army, float Gadget, float Repair) c)>();

                while (!state.IsGameOver)
                {
                    if (state.CurrentTick % sample == 0)
                        samples.Add((state.CurrentTick, state.GetEvalComponents()));

                    engine.Tick();
                    if (p1 is RolloutSearchOpponent rs) rs.Update(engine);
                    else ((HeuristicBotAdapter)p1).Update(engine);
                    p2.Update(engine);
                }

                // Matches Simulation's convention so the two datasets stay comparable:
                // a tick-cap game is labelled for whoever holds more castle HP.
                int winner = state.WinnerSide;
                if (winner == 0)
                    winner = state.Player1.CastleHealth >= state.Player2.CastleHealth ? 1 : 2;

                var list = new List<string>(samples.Count);
                foreach (var (tick, c) in samples)
                    list.Add($"{g},{tick},{c.Hp:F4},{c.Income:F4},{c.Money:F4},{c.Army:F4},{c.Gadget:F4},{c.Repair:F4},{winner}");
                rows[g] = list;

                int done = Interlocked.Increment(ref completed);
                if (done % Math.Max(1, games / 10) == 0)
                    Console.WriteLine($"  ... {done}/{games} games");
            });

            using var sw = new StreamWriter(outPath, append: false, Encoding.UTF8);
            sw.WriteLine("game_id,tick,hp_score,income_score,money_score,army_score,gadget_score,repair_score,winner");
            long total = 0;
            for (int g = 0; g < games; g++)
                foreach (var line in rows[g]) { sw.WriteLine(line); total++; }

            int p1w = 0;
            for (int g = 0; g < games; g++)
                if (rows[g].Count > 0 && rows[g][0].EndsWith(",1")) p1w++;

            Console.WriteLine($"\n[calib-collect] {total:N0} rows from {games} games -> {outPath}");
            Console.WriteLine($"[calib-collect] P1 win rate {(double)p1w / games:P1} " +
                              $"({(searchMode ? "search" : "HeuristicBot")} as P1)");
        }
    }
}
