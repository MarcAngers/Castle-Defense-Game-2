using System.Collections.Concurrent;
using CastleDefense.Engine;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Models;

namespace CastleDefense.BotArena;

/// <summary>
/// The honest test of the counter table: does counter-picking actually beat the random roll
/// it replaced, on games the table was never fitted on?
///
/// WHY THIS IS A SEPARATE RUN AND NOT A NUMBER FROM analyze.py. The in-sample figure that
/// falls out of the sweep is picked and scored on the same games, so it is inflated by
/// exactly the amount the argmax overfitted -- and at 128 candidates per row that amount can
/// be most of the apparent gain. This mode plays fresh games (shifted off the sweep's
/// common-random-number stream via --game-offset) and puts three arms on the same board
/// positions:
///
///   counter  - CounterPicker's answer, the shipped behaviour
///   random   - a uniform random loadout, the behaviour it replaced
///   fixed    - the single best loadout overall, ignoring what the human picked
///
/// The third arm is the one that decides whether counter-picking earned its complexity. If
/// `counter` does not beat `fixed`, the matrix has no usable interaction and the honest
/// answer is a constant, not a table.
///
/// PAIRED ACROSS ARMS: game index i is the same map, shadow roll and engine seed in all
/// three arms, so the comparison between them is within-position rather than between
/// independent samples.
/// </summary>
public static class CounterEval
{
    public static void Run(string[] args)
    {
        int games = 200;
        int threads = Math.Max(1, Environment.ProcessorCount - 2);
        int gameOffset = 1_000_000;
        string humanKind = "heuristic";
        string botKind = "heuristic";
        string outPath = null;
        string fixedSpec = null;
        int every = 1;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--games":       games = int.Parse(args[++i]); break;
                case "--threads":     threads = int.Parse(args[++i]); break;
                case "--game-offset": gameOffset = int.Parse(args[++i]); break;
                case "--human-bot":   humanKind = args[++i]; break;
                case "--bot":         botKind = args[++i]; break;
                case "--out":         outPath = args[++i]; break;
                // e.g. --fixed White,nuke,wall
                case "--fixed":       fixedSpec = args[++i]; break;
                // Keep every Nth human loadout. A search-bot arm costs ~200x a heuristic
                // one, so the full 128-row sweep is unaffordable there; subsampling the
                // rows buys precision per row instead of coverage.
                case "--every":       every = int.Parse(args[++i]); break;
            }
        }

        var loadouts = CounterMatrix.AllLoadouts();
        if (every > 1)
        {
            // SHUFFLE FIRST, then take a prefix. A plain `ix % every` stride is NOT a
            // representative subsample here: AllLoadouts() varies defense fastest and
            // offense next, so any stride that is a multiple of 4 or 16 holds the gadgets
            // constant and varies only the team. `--every 16` that way returns eight
            // nuke/heal rows and nothing else, which silently answers a different question
            // than the one asked. A fixed seed keeps the subsample reproducible.
            var shuffled = loadouts.OrderBy(l => CounterMatrix.Mix(0x51B5EED, l.Key.GetHashCode(StringComparison.Ordinal))).ToList();
            loadouts = shuffled.Take(Math.Max(1, loadouts.Count / every)).ToList();
            Console.WriteLine($"Subsampled 1-in-{every} human loadouts (seeded shuffle): {loadouts.Count} rows");
        }
        CounterMatrix.Loadout? fixedLoadout = null;
        if (fixedSpec != null)
        {
            var p = fixedSpec.Split(',');
            fixedLoadout = new CounterMatrix.Loadout(Enum.Parse<TeamColour>(p[0], true), p[1], p[2]);
        }

        var arms = new List<string> { "counter", "random" };
        if (fixedLoadout != null) arms.Add("fixed");

        Console.WriteLine($"Counter-eval: {loadouts.Count} human loadouts x {arms.Count} arms x {games} games");
        Console.WriteLine($"human P1 = {humanKind}, bot P2 = {botKind}, game offset {gameOffset:N0}");
        if (fixedLoadout != null) Console.WriteLine($"fixed arm = {fixedLoadout}");
        Console.WriteLine();

        var wins = new double[loadouts.Count, arms.Count];
        var picked = new string[loadouts.Count];

        var work = new List<(int h, int arm, int g)>();
        for (int h = 0; h < loadouts.Count; h++)
            for (int arm = 0; arm < arms.Count; arm++)
                for (int g = 0; g < games; g++)
                    work.Add((h, arm, g + gameOffset));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int completed = 0;
        var pickRngLock = new object();

        Parallel.ForEach(Partitioner.Create(work, EnumerablePartitionerOptions.NoBuffering),
                         new ParallelOptions { MaxDegreeOfParallelism = threads }, item =>
        {
            var (hi, arm, gi) = item;
            var human = loadouts[hi];

            CounterMatrix.Loadout bot;
            if (arms[arm] == "counter")
            {
                // Deterministic per (human loadout, game) so the arm is reproducible even at
                // TopK > 1, where PickCounter samples.
                var rng = new Random(hi * 7919 + gi);
                var pick = CounterPicker.PickCounter(human.Team, human.Offense, human.Defense, rng);
                bot = new CounterMatrix.Loadout(pick.Team, pick.Offense, pick.Defense);
                if (picked[hi] == null) lock (pickRngLock) picked[hi] ??= bot.ToString();
            }
            else if (arms[arm] == "fixed")
            {
                bot = fixedLoadout.Value;
            }
            else
            {
                var rng = new Random(hi * 104729 + gi);
                bot = new CounterMatrix.Loadout(
                    Enum.GetValues<TeamColour>()[rng.Next(8)],
                    CounterMatrix.OffenseOptions[rng.Next(4)],
                    CounterMatrix.DefenseOptions[rng.Next(4)]);
            }

            double result = PlayOne(human, bot, gi, humanKind, botKind);
            lock (wins) wins[hi, arm] += result;

            int done = Interlocked.Increment(ref completed);
            if (done % 20000 == 0)
                Console.WriteLine($"[{sw.Elapsed:hh\\:mm\\:ss}] {done:N0}/{work.Count:N0} ({100.0 * done / work.Count:F1}%)");
        });

        Console.WriteLine($"\nDone in {sw.Elapsed:hh\\:mm\\:ss}\n");
        Console.WriteLine($"{"human loadout",-34} {string.Join(" ", arms.Select(a => $"{a,7}"))}   counter answer");
        Console.WriteLine(new string('-', 100));

        var sums = new double[arms.Count];
        var lines = new List<string>();
        for (int h = 0; h < loadouts.Count; h++)
        {
            var parts = new List<string>();
            for (int a = 0; a < arms.Count; a++)
            {
                sums[a] += wins[h, a];
                parts.Add($"{100.0 * wins[h, a] / games,7:F1}");
            }
            lines.Add($"{loadouts[h],-34} {string.Join(" ", parts)}   -> {picked[h]}");
        }
        foreach (var l in lines) Console.WriteLine(l);

        Console.WriteLine(new string('-', 92));
        Console.WriteLine($"arms: {string.Join(" ", arms)}");
        for (int a = 0; a < arms.Count; a++)
        {
            double rate = sums[a] / (loadouts.Count * (double)games);
            double se = Math.Sqrt(rate * (1 - rate) / (loadouts.Count * (double)games));
            Console.WriteLine($"  {arms[a],-10} {100 * rate,6:F2}%  +/- {100 * 1.96 * se:F2} (95% CI)");
        }

        if (outPath != null)
        {
            using var w = new StreamWriter(outPath, false);
            w.WriteLine("human_team,human_off,human_def," + string.Join(",", arms.Select(a => a + "_winrate")) + ",counter_pick");
            for (int h = 0; h < loadouts.Count; h++)
            {
                var vals = Enumerable.Range(0, arms.Count).Select(a => (wins[h, a] / games).ToString("F4"));
                w.WriteLine($"{loadouts[h].Team},{loadouts[h].Offense},{loadouts[h].Defense}," +
                            string.Join(",", vals) + $",{picked[h]}");
            }
            Console.WriteLine($"\nWrote {Path.GetFullPath(outPath)}");
        }
    }

    private static double PlayOne(CounterMatrix.Loadout human, CounterMatrix.Loadout bot,
                                  int gi, string humanKind, string botKind)
    {
        var (winner, _, _) = CounterMatrix.PlayOne(human, bot, gi, humanKind, botKind);
        return winner == 2 ? 1.0 : winner == 0 ? 0.5 : 0.0;
    }
}
