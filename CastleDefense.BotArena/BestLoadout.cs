using CastleDefense.Engine;
using CastleDefense.Engine.Bot;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Models;

namespace CastleDefense.BotArena
{
    /// <summary>
    /// Sweeps all 128 of the bot's own (team x offense x defense) loadouts and reports which
    /// it is strongest with. Added 2026-09-02.
    ///
    /// WHY NOT counter-matrix OR dashboard. counter-matrix conditions on the OPPONENT's
    /// loadout and fixes seats, because it is fitting a counter-pick table -- a different
    /// question, 128x128 cells, and meaningless transposed. dashboard fixes the bot's loadout
    /// and hands the opponent AssignRandomLoadout without recording it, which is the right
    /// marginalisation for this question but sweeps far fewer cells. This mode asks the plain
    /// question directly: averaged over everything else, which loadout wins most?
    ///
    /// WHAT MAKES IT TRUSTWORTHY, each one a rule this project learned the hard way (see
    /// CLAUDE.md's measurement pitfalls):
    ///  - SEATS ALTERNATE. A perfect mirror is decided by the seat, not the play, so any
    ///    bot-vs-bot number that does not balance seats is worthless and fails silently.
    ///  - COMMON RANDOM NUMBERS. Every loadout cell plays the SAME pre-generated specs (map,
    ///    opponent team, opponent loadout, engine seed), so cells are paired and a cell cannot
    ///    look strong merely for having drawn friendlier maps.
    ///  - A POOL CHOSEN FOR DISCRIMINATION. Rungs that HeuristicBot beats regardless of what
    ///    it is holding contribute ceiling and no signal -- the first version of this sweep
    ///    used the full ladder set and every cell came back at 93-97%. See the pool itself.
    ///  - HUMANCLONE IS SCORED SEPARATELY, because it is the only rung fitted to Marc rather
    ///    than derived from HeuristicBot, and this exercise exists to pick a loadout to play a
    ///    human with.
    ///
    /// WHAT IT IS NOT. Both seats are bots. The absolute rates will not transfer to a game
    /// against Marc; only the ordering has any claim to, and even that is a claim about which
    /// loadout is good ON AVERAGE, not which one beats him specifically.
    /// </summary>
    public static class BestLoadout
    {
        private static readonly string[] Offense = { "nuke", "firebomb", "snipe", "freeze" };
        private static readonly string[] Defense = { "heal", "reinforcements", "speed", "wall" };

        private sealed class Spec
        {
            public TeamColour OppTeam, Map;
            public string OppOff, OppDef;
            public int EngineSeed;
            public int OpponentIndex;
        }

        private sealed class Cell
        {
            public int Wins, Games, CloneWins, CloneGames;
            public double Invests, EndHp;
        }

        public static void Run(string[] args)
        {
            int games = 48;
            int seed = 12345;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--games" && i + 1 < args.Length) games = int.Parse(args[++i]);
                else if (args[i] == "--seed" && i + 1 < args.Length) seed = int.Parse(args[++i]);
            }

            // POOL CHOSEN FOR DISCRIMINATION, not for coverage. The first version used the
            // full ladder rung set and every cell came back at 93-97%: HeuristicBot beats
            // DoNothing, Investor, BalancedHuman and Chipper whatever it is holding, so those
            // rungs contribute ceiling and no signal, and a 4-point spread across 128 cells
            // cannot be read. These five all have real room to move.
            //
            // HeuristicBot (with an independently drawn loadout) is in deliberately, and it is
            // the most informative rung here rather than the most self-referential: BOTH SEATS
            // RUN THE SAME POLICY, so what varies between cells is only the loadout. That is
            // exactly the question, and it is the same design counter-matrix uses.
            //
            // Index 0 is HumanClone so it can be scored separately -- see the class comment.
            var pool = new List<(string name, Func<int, IArenaOpponent> make)>
            {
                ("HumanClone",    side => new HumanCloneBaseline(side)),
                ("Tier4Spam",     side => new TierSpamBaseline(side, 4)),
                ("Tier6Spam",     side => new TierSpamBaseline(side, 6)),
                ("Tier7Spam",     side => new TierSpamBaseline(side, 7)),
                ("HeuristicBot",  side => new HeuristicBotAdapter(side)),
            };

            var teams = (TeamColour[])Enum.GetValues(typeof(TeamColour));

            // Drawn ONCE and replayed by every loadout cell. That pairing is what makes the
            // 128 cells comparable to each other at this sample size.
            var rng = new Random(seed);
            var specs = new List<Spec>(games);
            for (int i = 0; i < games; i++)
            {
                specs.Add(new Spec
                {
                    OppTeam = teams[rng.Next(teams.Length)],
                    Map = teams[rng.Next(teams.Length)],
                    OppOff = Offense[rng.Next(Offense.Length)],
                    OppDef = Defense[rng.Next(Defense.Length)],
                    EngineSeed = rng.Next(),
                    OpponentIndex = i % pool.Count,
                });
            }

            var results = new List<(string label, Cell cell)>();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            foreach (var team in teams)
            {
                foreach (var off in Offense)
                {
                    foreach (var def in Defense)
                    {
                        var cell = new Cell();
                        foreach (var spec in specs)
                        {
                            // Both seats, same spec. Seat bias is severe enough that a
                            // one-sided reading of any cell here would be noise.
                            for (int pass = 0; pass < 2; pass++)
                            {
                                bool botIsP1 = pass == 0;
                                int botSide = botIsP1 ? 1 : 2;

                                var state = new GameState(spec.Map, new Random(spec.EngineSeed));
                                state.Player1 = new PlayerState();
                                state.Player2 = new PlayerState();

                                var botPlayer = botIsP1 ? state.Player1 : state.Player2;
                                var oppPlayer = botIsP1 ? state.Player2 : state.Player1;
                                botPlayer.Side = botSide;
                                oppPlayer.Side = botIsP1 ? 2 : 1;
                                botPlayer.Team = team;
                                oppPlayer.Team = spec.OppTeam;
                                botPlayer.SetLoadout(new[]
                                    { off, def, GameDataManager.GetSignatureGadgetIdForTeam(team) });
                                oppPlayer.SetLoadout(new[]
                                    { spec.OppOff, spec.OppDef,
                                      GameDataManager.GetSignatureGadgetIdForTeam(spec.OppTeam) });

                                var engine = new GameEngine(state, null, spec.EngineSeed);
                                var bot = new HeuristicBotAdapter(botSide);
                                var foe = pool[spec.OpponentIndex].make(botIsP1 ? 2 : 1);

                                while (!state.IsGameOver)
                                {
                                    engine.Tick();
                                    if (botIsP1) { bot.Update(engine); foe.Update(engine); }
                                    else { foe.Update(engine); bot.Update(engine); }
                                }

                                bool won = state.WinnerSide == botSide;
                                cell.Games++;
                                if (won) cell.Wins++;
                                cell.Invests += botPlayer.InvestmentCount;
                                cell.EndHp += botPlayer.CastleMaxHealth > 0
                                    ? 100.0 * botPlayer.CastleHealth / botPlayer.CastleMaxHealth : 0;
                                if (spec.OpponentIndex == 0)
                                {
                                    cell.CloneGames++;
                                    if (won) cell.CloneWins++;
                                }

                                (bot as IDisposable)?.Dispose();
                                (foe as IDisposable)?.Dispose();
                            }
                        }
                        results.Add(($"{team},{off},{def}", cell));
                    }
                }
            }
            sw.Stop();

            int total = results.Sum(r => r.cell.Games);
            Console.WriteLine($"BEST-LOADOUT SWEEP -- 128 cells x {games} specs x 2 seats = {total} games");
            Console.WriteLine($"seed={seed}  elapsed={sw.Elapsed.TotalSeconds:F1}s");
            Console.WriteLine("pool: HumanClone / Tier4Spam / Tier6Spam / Tier7Spam / HeuristicBot(random loadout)");
            Console.WriteLine();
            Console.WriteLine("  rank  loadout                        win%    95% CI     vsClone  invests  endHP%");
            Console.WriteLine("  " + new string('-', 82));

            int rank = 1;
            foreach (var (label, c) in results.OrderByDescending(r => (double)r.cell.Wins / r.cell.Games).Take(15))
            {
                var (lo, hi) = Ladder.WilsonInterval(c.Wins, c.Games);
                Console.WriteLine($"  {rank,4}  {label,-28} {100.0 * c.Wins / c.Games,6:F1}  " +
                                  $"[{100 * lo,4:F0},{100 * hi,4:F0}]  " +
                                  $"{100.0 * c.CloneWins / Math.Max(1, c.CloneGames),7:F1}  " +
                                  $"{c.Invests / c.Games,7:F2} {c.EndHp / c.Games,7:F1}");
                rank++;
            }

            Console.WriteLine();
            Console.WriteLine("  WORST 5 (sanity check: these should look plainly bad)");
            foreach (var (label, c) in results.OrderBy(r => (double)r.cell.Wins / r.cell.Games).Take(5))
                Console.WriteLine($"        {label,-28} {100.0 * c.Wins / c.Games,6:F1}");

            // Averaging over the other two axes is the only way to tell a real main effect
            // from one cell that got lucky, and marginals replicate across seeds far better
            // than individual cells do at this n.
            void Marginal(string title, Func<string, string> key)
            {
                Console.WriteLine();
                Console.WriteLine($"  MARGINAL -- {title}");
                foreach (var g in results.GroupBy(r => key(r.label))
                                         .Select(g => (k: g.Key,
                                                       w: g.Sum(x => x.cell.Wins),
                                                       n: g.Sum(x => x.cell.Games)))
                                         .OrderByDescending(x => (double)x.w / x.n))
                    Console.WriteLine($"        {g.k,-18} {100.0 * g.w / g.n,6:F1}");
            }
            Marginal("team", l => l.Split(',')[0]);
            Marginal("offense", l => l.Split(',')[1]);
            Marginal("defense", l => l.Split(',')[2]);
        }
    }
}
