using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CastleDefense.Engine.Models;

namespace CastleDefense.Engine.Bot
{
    /// <summary>
    /// A behaviour clone of the human, played from a fitted lookup table rather than a
    /// network. Produced by `CastleDefense.Simulation --export-policy-table`, which carries
    /// the full rationale for the table form; the short version is that the neural BC
    /// exporter emits zero wait examples while the human waits on 98.5% of ticks, so a
    /// network fitted to it cannot learn the one thing that most distinguishes him.
    ///
    /// WHY THIS RUNG EXISTS. Every other ladder rung is a spam bot, an Investor, or
    /// HeuristicBot — and HeuristicBot is simultaneously RolloutSearchBot's policy prior and
    /// its rollout policy for both sides, so "75% vs HeuristicBot" partly measures how well
    /// the search exploits its own simulator. That number can rise while real strength does
    /// not. This rung owes nothing to HeuristicBot: every parameter in it came off Marc's
    /// recorded games. Beating it cannot be achieved by getting better at exploiting the
    /// bot's own model of the world.
    ///
    /// WHAT IT IS NOT. It is a conditional average of Marc, not Marc. It cannot read a
    /// window, bait a cooldown, or aim a gadget (the replay format never recorded target
    /// positions, so casts use the engine's auto-target). Expect it to be BEATABLE — it was
    /// asked for as a differently-SHAPED opponent, not a strong one. Its value is that a
    /// change which helps against it is a change that helps against something that spends
    /// its money the way a person does.
    ///
    /// DETERMINISM. Seeded once per game off the engine's own seeded stream, so a ladder run
    /// at a given --seed reproduces exactly, matching the discipline the rest of the ladder
    /// depends on.
    /// </summary>
    public class HumanCloneBot
    {
        // Must match PolicyTableExport.Bin exactly — the fit and the play-time lookup index
        // the same table, so a change to one is a silent mis-index unless mirrored.
        public const int MaxInvestBin = 7;
        public const int NPressure = 3;
        public const int NBins = (MaxInvestBin + 1) * NPressure;
        public const int NActions = 14;

        /// <summary>Below this many observed ticks a bin's estimate is too noisy to sample
        /// from directly, so it backs off to the same investment level across all pressure
        /// bands, and then to the global distribution. Four of the 24 bins need this.</summary>
        private const long MinBinTicks = 300;

        private sealed class Table
        {
            public readonly double[] ActRate = new double[NBins];
            public readonly double[][] Cdf = new double[NBins][];   // over actions 1..13
            public bool[] Usable = new bool[NBins];
        }

        private static Table _table;
        private static readonly object _loadLock = new object();

        private readonly int _side;
        private readonly int _interval;
        private long _next;
        private Random _rng;

        /// <summary>Action counts taken, index 0..13, for benchmark readouts.</summary>
        public long[] ActionCounts { get; } = new long[NActions];

        /// <param name="decisionIntervalTicks">How often the clone is even allowed to
        /// consider acting. 1 reproduces the fitted per-tick rate exactly; the fit is a
        /// per-tick probability, so anything larger is rescaled to keep the same expected
        /// actions per second.</param>
        public HumanCloneBot(int side, int decisionIntervalTicks = 1)
        {
            _side = side;
            _interval = Math.Max(1, decisionIntervalTicks);
            EnsureLoaded();
        }

        public void Update(GameEngine engine)
        {
            var state = engine._state;
            if (state.IsGameOver) return;

            // Seeded off the engine's own stream so each game gets an independent but
            // reproducible sequence, and a re-run at the same ladder seed is identical.
            _rng ??= new Random(engine.Rng.Next());

            if (state.CurrentTick < _next) return;
            _next = state.CurrentTick + _interval;

            var me = _side == 1 ? state.Player1 : state.Player2;
            int enemyUnits = 0;
            foreach (var u in state.Units) if (u.Side != _side) enemyUnits++;

            int bin = ResolveBin(me.InvestmentCount, enemyUnits);

            // Rescaled so a coarser decision cadence still yields the fitted actions per
            // second rather than silently slowing the clone down by the interval factor.
            double p = Math.Min(1.0, _table.ActRate[bin] * _interval);
            if (_rng.NextDouble() >= p) return;

            int action = Sample(bin);
            if (action <= 0) return;

            // Marc's distribution is conditioned on states where he could AFFORD what he
            // chose. The clone can land in the same bin while poorer, so an unaffordable
            // draw becomes a WAIT rather than a fallback to something cheaper. That is the
            // deliberate choice: substituting a cheap unit would turn "he was saving for a
            // tier 6" into chaff spam and destroy the saving behaviour the rung exists to
            // represent. The bin is self-correcting — staying poor keeps it in a
            // low-investment bin, where his distribution is invest-heavy anyway.
            var mask = state.GetActionMask(_side);
            if (action >= mask.Length || mask[action] == 0) return;

            if (engine.ApplyAction(_side, action)) ActionCounts[action]++;
        }

        /// <summary>Backs a thin bin off to its investment level, then to global.</summary>
        private int ResolveBin(int investmentCount, int enemyUnits)
        {
            int inv = Math.Min(investmentCount, MaxInvestBin);
            int press = enemyUnits == 0 ? 0 : enemyUnits <= 5 ? 1 : 2;
            int bin = inv * NPressure + press;
            if (_table.Usable[bin]) return bin;
            for (int q = 0; q < NPressure; q++)
                if (_table.Usable[inv * NPressure + q]) return inv * NPressure + q;
            for (int b = NBins - 1; b >= 0; b--)
                if (_table.Usable[b]) return b;
            return bin;
        }

        private int Sample(int bin)
        {
            var cdf = _table.Cdf[bin];
            if (cdf == null) return 0;
            double u = _rng.NextDouble();
            for (int a = 1; a < NActions; a++)
                if (u <= cdf[a]) return a;
            return 0;
        }

        // ── Table loading ────────────────────────────────────────────────────────────

        private static void EnsureLoaded()
        {
            if (_table != null) return;
            lock (_loadLock)
            {
                if (_table != null) return;
                string path = Path.Combine(AppContext.BaseDirectory, "Data", "human_policy_table.csv");
                if (!File.Exists(path))
                    throw new FileNotFoundException(
                        $"HumanCloneBot needs a fitted policy table at {path}. Produce one with:\n" +
                        "  CastleDefense.Simulation.exe --export-policy-table <recordings/singleplayer> " +
                        "<CastleDefense.Engine/Data/human_policy_table.csv>", path);

                var t = new Table();
                foreach (var line in File.ReadLines(path))
                {
                    if (line.Length == 0 || line[0] == '#' || line.StartsWith("bin,")) continue;
                    var f = line.Split(',');
                    if (f.Length < 6 + 13) continue;
                    int bin = int.Parse(f[0], CultureInfo.InvariantCulture);
                    if (bin < 0 || bin >= NBins) continue;
                    long ticks = long.Parse(f[3], CultureInfo.InvariantCulture);
                    t.ActRate[bin] = double.Parse(f[5], CultureInfo.InvariantCulture);

                    var counts = new double[NActions];
                    double total = 0;
                    for (int a = 1; a < NActions; a++)
                    {
                        counts[a] = double.Parse(f[5 + a], CultureInfo.InvariantCulture);
                        total += counts[a];
                    }
                    if (total <= 0) continue;

                    var cdf = new double[NActions];
                    double acc = 0;
                    for (int a = 1; a < NActions; a++) { acc += counts[a] / total; cdf[a] = acc; }
                    cdf[NActions - 1] = 1.0;   // guard against float drift on the last bucket
                    t.Cdf[bin] = cdf;
                    t.Usable[bin] = ticks >= MinBinTicks;
                }
                _table = t;
            }
        }

        /// <summary>Test hook: forces a reload, so a re-fitted table can be picked up
        /// without restarting a long-running harness.</summary>
        public static void InvalidateTable() { lock (_loadLock) { _table = null; } }
    }
}
