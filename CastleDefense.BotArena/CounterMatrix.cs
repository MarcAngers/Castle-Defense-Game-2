using System.Collections.Concurrent;
using CastleDefense.Engine;
using CastleDefense.Engine.Bot;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Models;

namespace CastleDefense.BotArena;

/// <summary>
/// Sweeps the FULL loadout-vs-loadout cross-tab so the singleplayer bot can counter-pick
/// against whatever the human chose.
///
/// WHY THE `dashboard` MODE CANNOT ANSWER THIS. Dashboard fixes the bot's
/// (team, offense, defense) and hands the OPPONENT `AssignRandomLoadout`, then never
/// writes the opponent's loadout to the CSV. Its numbers are therefore marginalised over
/// exactly the variable a counter table conditions on: they say which loadout is good on
/// average, not which loadout beats which. No amount of re-analysis of results.csv
/// recovers the missing column.
///
/// THREE DESIGN CHOICES THAT DIFFER FROM EVERY OTHER BENCHMARK IN THIS PROJECT:
///
/// 1. SEATS ARE FIXED, NOT ALTERNATED. Every other harness alternates because it wants an
///    unbiased estimate of bot strength, and the engine's seat asymmetry is severe enough
///    to invalidate one that doesn't (`mirror-fixed White nuke wall 100` returns P2
///    100/100). Here the deployed configuration IS fixed-seat -- GameHub's "sp" mode always
///    puts the human on P1 and the bot on P2 -- so alternating would average away a real
///    asymmetry the bot gets to keep. This table is a table of best responses in one
///    specific seat, and is meaningless transposed.
///
/// 2. NO HEADSTART. Matches "sp", which builds a plain `new GameState()` at tick 0.
///
/// 3. COMMON RANDOM NUMBERS ACROSS CELLS. Game index i draws the same map, the same shadow
///    roll and the same engine RNG seed in EVERY cell of the matrix, because the setup seed
///    is derived from i alone and never from the loadout pair. The map is a real
///    gameplay-affecting roll, so without this a cell could look strong purely for having
///    drawn friendlier maps than the cell it is being compared against. Pairing removes
///    that variance from every cross-cell comparison instead of hoping n averages it out.
/// </summary>
public static class CounterMatrix
{
    public static readonly string[] OffenseOptions = { "nuke", "firebomb", "snipe", "freeze" };
    public static readonly string[] DefenseOptions = { "heal", "reinforcements", "speed", "wall" };

    public readonly record struct Loadout(TeamColour Team, string Offense, string Defense)
    {
        public override string ToString() => $"{Team}/{Offense}/{Defense}";
        public string Key => $"{Team}|{Offense}|{Defense}";
    }

    public static List<Loadout> AllLoadouts()
    {
        var list = new List<Loadout>();
        foreach (var team in Enum.GetValues<TeamColour>())
            foreach (var off in OffenseOptions)
                foreach (var def in DefenseOptions)
                    list.Add(new Loadout(team, off, def));
        return list;
    }

    public static IArenaOpponent MakeBot(string kind, int side, int seed) => kind switch
    {
        "heuristic" => new HeuristicBotAdapter(side),
        "clone"     => new HumanCloneBaseline(side),
        "search"    => new RolloutSearchOpponent(side, 15, 300, 1, seed, true, 0.10),
        _           => throw new ArgumentException($"unknown bot kind '{kind}'"),
    };

    /// <summary>
    /// Deterministic 32-bit mix. NOT HashCode.Combine: that is salted with a per-process
    /// random seed, so a matrix built with it would not reproduce across runs.
    /// </summary>
    public static int Mix(int a, int b)
    {
        unchecked
        {
            uint h = (uint)a * 2654435761u;
            h ^= (uint)b + 0x9E3779B9u + (h << 6) + (h >> 2);
            h ^= h >> 15; h *= 0x85EBCA6Bu; h ^= h >> 13;
            return (int)(h & 0x7FFFFFFF);
        }
    }

    /// <summary>
    /// Plays one game in the deployed singleplayer configuration: human on P1, bot on P2, no
    /// headstart, tick 0. Everything random about the setup -- map, shadow roll, engine RNG --
    /// is derived from <paramref name="gameIndex"/> ALONE and never from the loadouts, which
    /// is what makes game i the same board position in every cell and every arm. Returns the
    /// result from the BOT's point of view along with how it was decided.
    /// </summary>
    public static (int winnerSide, bool isTimeLimit, long ticks) PlayOne(
        Loadout human, Loadout bot, int gameIndex, string humanKind, string botKind)
    {
        int setupSeed = Mix(0x5CA1AB1E, gameIndex);
        var setupRng = new Random(setupSeed);
        var mapValues = Enum.GetValues<TeamColour>();
        var map = mapValues[setupRng.Next(mapValues.Length)];

        var state = new GameState(map, setupRng);
        state.GameMode = "sp";
        state.Player1.Side = 1;
        state.Player1.Team = human.Team;
        state.Player1.SetLoadout(new[] { human.Offense, human.Defense,
                                         GameDataManager.GetSignatureGadgetIdForTeam(human.Team) });
        state.Player2.Side = 2;
        state.Player2.Team = bot.Team;
        state.Player2.SetLoadout(new[] { bot.Offense, bot.Defense,
                                         GameDataManager.GetSignatureGadgetIdForTeam(bot.Team) });

        var engine = new GameEngine(state, seed: Mix(setupSeed, 0x1234));
        var p1 = MakeBot(humanKind, 1, Mix(setupSeed, 1));
        var p2 = MakeBot(botKind, 2, Mix(setupSeed, 2));

        while (!state.IsGameOver)
        {
            engine.Tick();
            p1.Update(engine);
            p2.Update(engine);
        }
        (p1 as IDisposable)?.Dispose();
        (p2 as IDisposable)?.Dispose();

        return (state.WinnerSide, state.IsTimeLimit, state.CurrentTick);
    }

    /// <summary>One cell's tally. Wins/losses are from the BOT's (P2's) point of view.</summary>
    public sealed class Cell
    {
        public int DecisiveWins, TimeoutWins, Draws, TimeoutLosses, DecisiveLosses;
        public long TotalTicks;
        public int Total => DecisiveWins + TimeoutWins + Draws + TimeoutLosses + DecisiveLosses;
    }

    public static void Run(string[] args)
    {
        int games = 16;
        string outPath = "counter_matrix.csv";
        string botKind = "heuristic";
        string humanKind = "heuristic";
        int threads = Math.Max(1, Environment.ProcessorCount - 2);
        string pairsFile = null;
        int gameOffset = 0;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--games":       games = int.Parse(args[++i]); break;
                case "--out":         outPath = args[++i]; break;
                case "--bot":         botKind = args[++i]; break;
                case "--human-bot":   humanKind = args[++i]; break;
                case "--threads":     threads = int.Parse(args[++i]); break;
                case "--pairs":       pairsFile = args[++i]; break;
                // Shifts the common-random-number stream so a refinement pass draws games
                // the coarse pass did not, instead of re-running the identical ones.
                case "--game-offset": gameOffset = int.Parse(args[++i]); break;
            }
        }

        var loadouts = AllLoadouts();
        var indexOf = new Dictionary<string, int>();
        for (int i = 0; i < loadouts.Count; i++) indexOf[loadouts[i].Key] = i;

        List<(int h, int b)> cellList;
        if (pairsFile != null)
        {
            cellList = new List<(int, int)>();
            foreach (var line in File.ReadAllLines(pairsFile))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("human_team")) continue;
                var p = line.Split(',');
                cellList.Add((indexOf[$"{p[0]}|{p[1]}|{p[2]}"], indexOf[$"{p[3]}|{p[4]}|{p[5]}"]));
            }
            Console.WriteLine($"Restricted sweep: {cellList.Count} pairs from {pairsFile}");
        }
        else
        {
            cellList = new List<(int, int)>();
            for (int h = 0; h < loadouts.Count; h++)
                for (int b = 0; b < loadouts.Count; b++)
                    cellList.Add((h, b));
        }

        var cells = new Dictionary<(int h, int b), Cell>();
        foreach (var c in cellList) cells[c] = new Cell();

        long totalGames = (long)cellList.Count * games;
        Console.WriteLine($"Counter matrix: human seat P1 = {humanKind}, bot seat P2 = {botKind}");
        Console.WriteLine($"{cellList.Count} cells x {games} games = {totalGames:N0} games on {threads} threads");
        Console.WriteLine("Fixed seats (human P1 / bot P2), no headstart, common random numbers across cells.\n");

        var work = new List<(int h, int b, int g)>((int)totalGames);
        foreach (var (h, b) in cellList)
            for (int g = 0; g < games; g++)
                work.Add((h, b, g + gameOffset));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int completed = 0;
        int lastReport = 0;

        // One work item per GAME, handed out one at a time. Same lesson the dashboard sweep
        // learned the hard way: per-cell partitioning strands the run on one core behind the
        // few cells that happen to draw 600s stalemates.
        Parallel.ForEach(Partitioner.Create(work, EnumerablePartitionerOptions.NoBuffering),
                         new ParallelOptions { MaxDegreeOfParallelism = threads }, item =>
        {
            var (hi, bi, gi) = item;
            var (winner, isTimeLimit, ticks) =
                PlayOne(loadouts[hi], loadouts[bi], gi, humanKind, botKind);

            var cell = cells[(hi, bi)];
            lock (cell)
            {
                cell.TotalTicks += ticks;
                if (winner == 0) cell.Draws++;
                else if (winner == 2)
                {
                    if (isTimeLimit) cell.TimeoutWins++; else cell.DecisiveWins++;
                }
                else
                {
                    if (isTimeLimit) cell.TimeoutLosses++; else cell.DecisiveLosses++;
                }
            }

            int done = Interlocked.Increment(ref completed);
            if (done - Volatile.Read(ref lastReport) >= 20000)
            {
                Volatile.Write(ref lastReport, done);
                double frac = done / (double)totalGames;
                var eta = TimeSpan.FromSeconds(sw.Elapsed.TotalSeconds / Math.Max(frac, 1e-9) * (1 - frac));
                Console.WriteLine($"[{sw.Elapsed:hh\\:mm\\:ss}] {done:N0}/{totalGames:N0} ({100 * frac:F1}%)  ETA {eta:hh\\:mm\\:ss}");
            }
        });

        var dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using (var w = new StreamWriter(outPath, false))
        {
            w.WriteLine("human_team,human_off,human_def,bot_team,bot_off,bot_def," +
                        "games,bot_decisive_wins,bot_timeout_wins,draws,bot_timeout_losses,bot_decisive_losses,avg_ticks");
            foreach (var (h, b) in cellList)
            {
                var c = cells[(h, b)];
                var hl = loadouts[h]; var bl = loadouts[b];
                w.WriteLine($"{hl.Team},{hl.Offense},{hl.Defense},{bl.Team},{bl.Offense},{bl.Defense}," +
                            $"{c.Total},{c.DecisiveWins},{c.TimeoutWins},{c.Draws},{c.TimeoutLosses},{c.DecisiveLosses}," +
                            $"{(c.Total > 0 ? c.TotalTicks / (double)c.Total : 0):F0}");
            }
        }

        long gw = cells.Values.Sum(c => (long)c.DecisiveWins + c.TimeoutWins);
        long gd = cells.Values.Sum(c => (long)c.Draws);
        Console.WriteLine($"\nDone in {sw.Elapsed:hh\\:mm\\:ss}. Wrote {Path.GetFullPath(outPath)}");
        Console.WriteLine($"Overall bot win rate across all cells: {100.0 * gw / totalGames:F1}% (draws {100.0 * gd / totalGames:F1}%)");
    }
}
