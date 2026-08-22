using System.Security.Cryptography;
using System.Text;
using CastleDefense.Engine;
using CastleDefense.Engine.Bot;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Models;

namespace CastleDefense.BotArena
{
    /// <summary>
    /// Deterministic fingerprint of HeuristicBot's play, for proving a refactor changed nothing.
    ///
    /// EXISTS BECAUSE THE OBVIOUS BENCHMARKS CANNOT DO THIS. counter-eval is not reproducible
    /// (the same binary gives different rows run to run) and counter-matrix is byte-identical
    /// but sweeps 128x128 cells, which is minutes of wall clock before it says anything. A
    /// refactor guard has to be exact AND fast or it will not get run.
    ///
    /// Everything here is seeded from the game index alone: the map roll, the loadouts and the
    /// engine stream. Two runs of the same binary agree exactly; two builds that play the same
    /// way agree exactly.
    /// </summary>
    public static class BotChecksum
    {
        public static void Run(string[] args)
        {
            int games = 24;
            bool defenceOnly = false;
            bool p1Only = false;   // P1 defensive, P2 the shipped attacking bot -- the head-to-head
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--games" && i + 1 < args.Length) games = int.Parse(args[++i]);
                if (args[i] == "--defence-only") defenceOnly = true;
                if (args[i] == "--p1-defence-only") { p1Only = true; defenceOnly = true; }
            }

            var settings = defenceOnly ? HeuristicBotSettings.DefenceOnlyProfile : null;
            var teams = (TeamColour[])Enum.GetValues(typeof(TeamColour));
            var offense = new[] { "nuke", "firebomb", "snipe", "freeze" };
            var defense = new[] { "heal", "reinforcements", "speed", "wall" };

            var sb = new StringBuilder();
            Console.WriteLine($"BOT CHECKSUM -- {games} seeded self-play games, "
                            + $"{(defenceOnly ? "DEFENCE-ONLY" : "shipped")} settings\n");
            Console.WriteLine($"{"game",-6}{"map",-9}{"winner",-8}{"ticks",8}{"p1hp",9}{"p2hp",9}{"p1inv",7}{"p2inv",7}{"p1units",9}");

            for (int g = 0; g < games; g++)
            {
                var rng = new Random(g);
                var map = teams[rng.Next(teams.Length)];
                var state = new GameState(map, new Random(g));
                state.Player1 = new PlayerState();
                state.Player2 = new PlayerState();
                for (int side = 1; side <= 2; side++)
                {
                    var p = side == 1 ? state.Player1 : state.Player2;
                    var t = teams[rng.Next(teams.Length)];
                    p.Side = side;
                    p.Team = t;
                    p.SetLoadout(new[] { offense[rng.Next(offense.Length)],
                                         defense[rng.Next(defense.Length)],
                                         GameDataManager.GetSignatureGadgetIdForTeam(t) });
                }

                var engine = new GameEngine(state, null, g);
                var p1 = new HeuristicBot(1, settings);
                var p2 = new HeuristicBot(2, p1Only ? null : settings);
                int p1Spawns = 0;

                while (!state.IsGameOver)
                {
                    engine.Tick();
                    int before = state.Units.Count(u => u.Side == 1);
                    p1.Update(engine);
                    p2.Update(engine);
                    int after = state.Units.Count(u => u.Side == 1);
                    if (after > before) p1Spawns += after - before;
                }

                string row = $"{g,-6}{map,-9}{state.WinnerSide,-8}{state.CurrentTick,8}"
                           + $"{state.Player1.CastleHealth,9}{state.Player2.CastleHealth,9}"
                           + $"{state.Player1.InvestmentCount,7}{state.Player2.InvestmentCount,7}{p1Spawns,9}";
                Console.WriteLine(row);
                sb.Append(row).Append('\n');
            }

            using var md5 = MD5.Create();
            string hash = Convert.ToHexString(md5.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())));
            Console.WriteLine($"\nCHECKSUM {hash}");
        }
    }
}
