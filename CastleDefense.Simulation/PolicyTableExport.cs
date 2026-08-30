using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CastleDefense.Engine;
using CastleDefense.Engine.Models;

namespace CastleDefense.Simulation
{
    /// <summary>
    /// Fits a NON-PARAMETRIC BEHAVIOUR CLONE of the human from his recorded games, and
    /// writes it as a plain lookup table for `HumanCloneBot` to play.
    ///
    /// WHY A TABLE AND NOT A NETWORK. The neural BC pipeline (`bc_pretrain.py`) is the
    /// obvious route and it is blocked on the shape of the data, not on tuning:
    ///
    ///  1. `--export-bc` emits ZERO wait examples — 0 of 8,109 — because it only records
    ///     ticks where the action id was non-zero. Marc actually waits on 98.5% of ticks.
    ///     A policy fitted to that has never seen the wait label as a target and will act
    ///     on every decision, i.e. become a spam bot, which is the exact thing a
    ///     human-shaped rung exists to replace. It also means the "69.3% action accuracy"
    ///     that pipeline last reported was measured on a label set with its majority class
    ///     deleted, so it does not mean what it appears to.
    ///  2. 6,817 P1-win examples is thin for a 348 -> 512 -> 512 -> 14 network.
    ///
    /// A conditional categorical over a coarse state bin fixes both by construction: the
    /// act rate is modelled explicitly rather than being an emergent property of a class
    /// balance that was destroyed, and a 13-way categorical per bin needs orders of
    /// magnitude less data than a network. Measured support: 20 of 24 bins carry 200+
    /// windows, and the fitted table is legible as strategy rather than as weights.
    ///
    /// WHAT IT STILL CANNOT CAPTURE, and this is not fixable from existing recordings:
    ///  * GADGET TARGETING. .replay stores the action id and never the target position, so
    ///    the clone fires gadgets at the engine's auto-target. Marc's documented doctrine —
    ///    freeze and blackhole at the ENEMY's end to buy the march back, damage gadgets at
    ///    the front — is simply absent from the data. The clone gets his gadget TIMING and
    ///    not his gadget PLACEMENT.
    ///  * BURSTINESS. Sampling per tick makes the act process Bernoulli, whereas a human
    ///    clicks three times quickly and then pauses. Same mean rate, smoother arrivals.
    ///  * ANY ADAPTATION the bin does not encode. This is a conditional average of Marc,
    ///    not Marc. It cannot read a window or bait a cooldown.
    ///
    /// So it is a rung, not an opponent to fear — which is exactly what was asked for: it
    /// does not need to be strong, it needs to be SHAPED like him and to owe nothing to
    /// HeuristicBot.
    ///
    /// Usage: --export-policy-table &lt;replayDir&gt; &lt;outCsv&gt; [--all] [--filter substr]
    /// </summary>
    public static class PolicyTableExport
    {
        public const int NActions = 14;
        public const int MaxInvestBin = 7;   // investments 7+ collapse into one bin
        public const int NPressure = 3;

        /// <summary>
        /// The state abstraction, kept deliberately coarse and duplicated in HumanCloneBot
        /// so the clone bins live states exactly as the fit binned recorded ones. Changing
        /// it here without changing it there silently mis-indexes the whole table.
        ///
        /// Chosen because these two axes are what visibly move Marc's policy in the fitted
        /// table — investment count sets the phase of his game (save, then arm, then
        /// commit), and enemy count sets whether he is spending on defence at all. Castle
        /// HP was tried as a third axis and dropped: it splits support roughly threefold
        /// for one action (repair) that is only 2.1% of what he does.
        /// </summary>
        public static int Bin(int investmentCount, int enemyUnitCount)
        {
            int inv = Math.Min(investmentCount, MaxInvestBin);
            int press = enemyUnitCount == 0 ? 0 : enemyUnitCount <= 5 ? 1 : 2;
            return inv * NPressure + press;
        }

        public const int NBins = (MaxInvestBin + 1) * NPressure;

        public static void Run(string replayDir, string outCsv, string[] args)
        {
            bool all = false; string filter = null, half = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--filter" && i + 1 < args.Length) filter = args[++i];
                else if (args[i] == "--half" && i + 1 < args.Length) half = args[++i];
                else if (args[i] == "--all") all = true;
            }

            if (!Directory.Exists(replayDir))
            {
                Console.Error.WriteLine($"[policy-table] replay directory not found: {replayDir}");
                return;
            }

            var selected = ReplayFile.SelectHumanGames(replayDir, "policy-table", all, filter, half);
            Console.WriteLine($"[policy-table] fitting from {selected.Count} human replays\n");

            var ticks = new long[NBins];
            var counts = new long[NBins, NActions];
            int games = 0, skipped = 0;

            foreach (var path in selected)
            {
                ReplayFile rf;
                try { rf = ReplayFile.Read(path); }
                catch (Exception ex) { skipped++; Console.Error.WriteLine($"[policy-table] skip {Path.GetFileName(path)}: {ex.Message}"); continue; }
                if (rf.IsAbandoned) { skipped++; continue; }

                var (state, engine) = rf.BuildStart();
                games++;

                for (int t = 0; t < rf.TickCount && !state.IsGameOver; t++)
                {
                    // Binned on the state the human was LOOKING AT when he acted, i.e.
                    // before either side's action for this tick lands. Binning after would
                    // condition the choice on its own consequence.
                    int bin = Bin(state.Player1.InvestmentCount, state.Units.Count(u => u.Side == 2));
                    ticks[bin]++;
                    byte a = rf.A1[t];
                    if (a > 0 && a < NActions) counts[bin, a]++;

                    engine.ApplyAction(1, rf.A1[t]);
                    engine.ApplyAction(2, rf.A2[t]);
                    engine.Tick();
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outCsv))!);
            using (var w = new StreamWriter(outCsv, false, new UTF8Encoding(false)))
            {
                w.WriteLine("# Human policy table — conditional action distribution fitted from replays.");
                w.WriteLine($"# games={games} bins={NBins} (investment 0-{MaxInvestBin} x pressure 0/1-5/6+ enemy units)");
                w.WriteLine("# act_rate is P(act | tick) in that bin; a1..a13 are raw action counts.");
                w.WriteLine("bin,invest,pressure,ticks,actions,act_rate," +
                            string.Join(",", Enumerable.Range(1, 13).Select(i => "a" + i)));
                for (int b = 0; b < NBins; b++)
                {
                    long acts = 0;
                    for (int a = 1; a < NActions; a++) acts += counts[b, a];
                    double rate = ticks[b] > 0 ? (double)acts / ticks[b] : 0;
                    w.Write($"{b},{b / NPressure},{b % NPressure},{ticks[b]},{acts},{rate:F6}");
                    for (int a = 1; a < NActions; a++) w.Write($",{counts[b, a]}");
                    w.WriteLine();
                }
            }

            // ── Readout, so the table can be sanity-checked by eye against how he plays ──
            Console.WriteLine("  bin  inv press     ticks  actions  act_rate   dominant actions");
            Console.WriteLine("  " + new string('-', 74));
            string[] names = { "", "t1", "t2", "t3", "t4", "t5", "t6", "t7", "t8", "INV", "REP", "off", "def", "sig" };
            long totalTicks = 0, totalActs = 0;
            for (int b = 0; b < NBins; b++)
            {
                long acts = 0;
                for (int a = 1; a < NActions; a++) acts += counts[b, a];
                totalTicks += ticks[b]; totalActs += acts;
                var top = Enumerable.Range(1, 13)
                    .Where(a => counts[b, a] > 0)
                    .OrderByDescending(a => counts[b, a]).Take(3)
                    .Select(a => $"{names[a]} {100.0 * counts[b, a] / Math.Max(1, acts):F0}%");
                Console.WriteLine($"  {b,3}  {b / NPressure,3} {b % NPressure,4} {ticks[b],9} {acts,8}  " +
                                  $"{(ticks[b] > 0 ? (double)acts / ticks[b] : 0),8:F4}   {string.Join("  ", top)}" +
                                  (ticks[b] < 300 ? "   [THIN — backed off at play time]" : ""));
            }
            Console.WriteLine("  " + new string('-', 74));
            Console.WriteLine($"  {games} games, {totalTicks} ticks, {totalActs} actions, " +
                              $"overall act rate {(double)totalActs / Math.Max(1, totalTicks):F4}");
            if (skipped > 0) Console.WriteLine($"  {skipped} replay(s) skipped (abandoned or unreadable)");
            Console.WriteLine($"\n[policy-table] wrote {outCsv}");
            Console.WriteLine("[policy-table] copy to CastleDefense.Engine/Data/human_policy_table.csv " +
                              "to make it the table HumanCloneBot plays.");
        }
    }
}
