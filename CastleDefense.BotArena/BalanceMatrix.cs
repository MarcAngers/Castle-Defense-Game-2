using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using CastleDefense.Engine;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Models;

namespace CastleDefense.BotArena;

/// <summary>
/// THE BALANCE DASHBOARD. Every loadout against every other loadout, in both seats,
/// HeuristicBot on both sides. Added 2026-09-03 to Marc's spec: "when I ask you to update the
/// balance dashboard, this is what I want you to run."
///
///     CastleDefense.BotArena.exe balance-matrix [--games N] [--out DIR] [--threads N]
///
/// 128 loadouts (8 teams x 4 offence x 4 defence) x 128 opponents = **16,384 ordered cells**.
/// One game per cell means every unordered matchup is played TWICE, once in each seat, which
/// is what makes the result a balance measurement rather than a seat measurement.
///
/// WHY NOT THE OLD `dashboard` MODE. That sweep fixed the bot's loadout and handed the
/// opponent `AssignRandomLoadout` WITHOUT RECORDING IT, so its numbers were marginalised over
/// exactly the variable balance work wants to condition on. It also spent ~80% of its wall
/// clock on 15 ONNX opponents that say nothing about game balance -- 24,960 games in 4m55s,
/// of which the HeuristicBot mirror was 3,200. This mode is 16,384 games of nothing but the
/// mirror, in about 2m10s.
///
/// WHY NOT `counter-matrix`, which sweeps the same 16,384 cells. Two reasons, and the second
/// one is the important one:
///
///  1. It is fitted for COUNTER-PICKING -- a table of best responses in the deployed
///     singleplayer seating -- so it reports from the bot's (P2's) point of view and its own
///     doc says the table is meaningless transposed. Balance wants the opposite: both
///     directions of every pair, averaged.
///
///  2. **ITS MAP IS DERIVED FROM THE GAME INDEX ALONE**, deliberately, so that cell i of every
///     cell plays the identical board -- common random numbers, which is right when you are
///     comparing cells against each other. But it means a one-game-per-cell sweep puts ALL
///     16,384 games on ONE map, and every map changes the rules (see CLAUDE.md's map effects).
///     The whole run would describe a single map and the map column would be a constant.
///
/// SO THE MAP IS SEEDED FROM THE UNORDERED PAIR, not from the game index. The two seatings of
/// {A,B} therefore share a map and an engine seed -- which is what makes the seat comparison
/// within a matchup exactly paired, the one pairing that matters here -- while different
/// matchups spread across all maps. A team's marginal then averages thousands of games over
/// every map rather than describing one.
/// </summary>
public static class BalanceMatrix
{
    /// <summary>
    /// Measured HeuristicBot-mirror throughput per worker thread, for the up-front estimate
    /// only. Calibrated 2026-09-03: 16,384 games on 18 threads in 2m10s = 126 games/s total.
    /// Nothing depends on this being right; the live ETA below replaces it within seconds.
    /// </summary>
    private const double GamesPerSecondPerThread = 7.0;

    private sealed class GameRow
    {
        public CounterMatrix.Loadout P1, P2;
        public TeamColour Map;
        public bool Shadow;
        public int Winner;          // 1, 2, or 0 for a draw
        public bool TimeLimit;
        public long Ticks;
    }

    public static void Run(string[] args)
    {
        int games = 1;
        string outDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "dashboard"));
        int threads = Math.Max(1, Environment.ProcessorCount - 2);
        bool fromCsv = false;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--games":    games = int.Parse(args[++i]); break;
                case "--out":      outDir = Path.GetFullPath(args[++i]); break;
                case "--threads":  threads = int.Parse(args[++i]); break;
                // Rebuild dashboard.html from an existing balance_matrix.csv without
                // replaying anything. The sweep is the expensive half and the presentation is
                // the half that gets iterated on; making a layout tweak cost two minutes of
                // CPU is how a report stops getting improved.
                case "--from-csv": fromCsv = true; break;
            }
        }

        var loadouts = CounterMatrix.AllLoadouts();
        int n = loadouts.Count;
        long totalGames = (long)n * n * games;

        Directory.CreateDirectory(outDir);
        string csvPath = Path.Combine(outDir, "balance_matrix.csv");
        string htmlPath = Path.Combine(outDir, "dashboard.html");

        if (fromCsv)
        {
            var reloaded = ReadCsv(csvPath);
            Console.WriteLine($"Rebuilding {htmlPath} from {reloaded.Length:N0} rows in {csvPath}");
            WriteHtml(htmlPath, reloaded, games, threads, TimeSpan.Zero);
            Console.WriteLine("Done.");
            return;
        }

        double estSeconds = totalGames / (GamesPerSecondPerThread * threads);
        Console.WriteLine("=== BALANCE MATRIX ===");
        Console.WriteLine($"HeuristicBot mirror. {n} loadouts x {n} opponents x {games} game(s)");
        Console.WriteLine($"= {totalGames:N0} games on {threads} threads");
        Console.WriteLine($"Every unordered matchup is played {2 * games}x, {games}x in each seat.");
        Console.WriteLine($"ESTIMATED RUNTIME: {TimeSpan.FromSeconds(estSeconds):hh\\:mm\\:ss}" +
                          $"  (at a measured {GamesPerSecondPerThread * threads:F0} games/s)");
        Console.WriteLine($"Output: {csvPath}\n        {htmlPath}\n");

        // One work item per GAME, handed out one at a time. Same lesson as the other sweeps:
        // per-cell partitioning strands the run on one core behind the few cells that draw
        // 600s stalemates.
        var work = new List<(int p1, int p2, int g)>((int)totalGames);
        for (int a = 0; a < n; a++)
            for (int b = 0; b < n; b++)
                for (int g = 0; g < games; g++)
                    work.Add((a, b, g));

        var results = new GameRow[totalGames];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int completed = 0, lastReport = 0;

        Parallel.ForEach(Partitioner.Create(work, EnumerablePartitionerOptions.NoBuffering),
                         new ParallelOptions { MaxDegreeOfParallelism = threads }, item =>
        {
            var (a, b, g) = item;
            var row = PlayOne(loadouts[a], loadouts[b], a, b, g);
            results[((long)a * n + b) * games + g] = row;

            int done = Interlocked.Increment(ref completed);
            if (done - Volatile.Read(ref lastReport) >= 2000)
            {
                Volatile.Write(ref lastReport, done);
                double frac = done / (double)totalGames;
                var eta = TimeSpan.FromSeconds(sw.Elapsed.TotalSeconds / Math.Max(frac, 1e-9) * (1 - frac));
                Console.WriteLine($"[{sw.Elapsed:hh\\:mm\\:ss}] {done:N0}/{totalGames:N0} " +
                                  $"({100 * frac:F1}%)  ETA {eta:hh\\:mm\\:ss}");
            }
        });

        Console.WriteLine($"\n[{sw.Elapsed:hh\\:mm\\:ss}] {totalGames:N0} games done. Writing output...");

        WriteCsv(csvPath, results);
        WriteHtml(htmlPath, results, games, threads, sw.Elapsed);

        Console.WriteLine($"Wrote {csvPath}");
        Console.WriteLine($"Wrote {htmlPath}");
        Console.WriteLine($"\nDone in {sw.Elapsed:hh\\:mm\\:ss}.");
    }

    /// <summary>
    /// One game, HeuristicBot both seats, in the plain `sp` configuration (no headstart,
    /// tick 0).
    ///
    /// THE SEED IS DERIVED FROM THE UNORDERED PAIR, which is the whole design point -- see the
    /// class note. `min/max` of the two loadout indices means (A vs B) and (B vs A) hash to the
    /// same value, so the two seatings of a matchup play the same map with the same engine
    /// stream and differ ONLY in who sits where.
    ///
    /// The map is READ BACK OFF THE STATE rather than recomputed, because GameState's
    /// constructor may reroll it into a shadow map; recomputing would silently disagree with
    /// what was actually played.
    /// </summary>
    private static GameRow PlayOne(CounterMatrix.Loadout p1, CounterMatrix.Loadout p2,
                                   int i1, int i2, int gameIndex)
    {
        int pairSeed = CounterMatrix.Mix(CounterMatrix.Mix(Math.Min(i1, i2), Math.Max(i1, i2)), gameIndex);
        var setupRng = new Random(pairSeed);
        var mapValues = Enum.GetValues<TeamColour>();
        var map = mapValues[setupRng.Next(mapValues.Length)];

        var state = new GameState(map, setupRng);
        state.GameMode = "sp";
        state.Player1.Side = 1;
        state.Player1.Team = p1.Team;
        state.Player1.SetLoadout(new[] { p1.Offense, p1.Defense,
                                         GameDataManager.GetSignatureGadgetIdForTeam(p1.Team) });
        state.Player2.Side = 2;
        state.Player2.Team = p2.Team;
        state.Player2.SetLoadout(new[] { p2.Offense, p2.Defense,
                                         GameDataManager.GetSignatureGadgetIdForTeam(p2.Team) });

        var engine = new GameEngine(state, seed: CounterMatrix.Mix(pairSeed, 0x1234));
        var b1 = CounterMatrix.MakeBot("heuristic", 1, CounterMatrix.Mix(pairSeed, 1));
        var b2 = CounterMatrix.MakeBot("heuristic", 2, CounterMatrix.Mix(pairSeed, 2));

        while (!state.IsGameOver)
        {
            engine.Tick();
            b1.Update(engine);
            b2.Update(engine);
        }
        (b1 as IDisposable)?.Dispose();
        (b2 as IDisposable)?.Dispose();

        return new GameRow
        {
            P1 = p1, P2 = p2,
            Map = state.Map, Shadow = state.ShadowMap,
            Winner = state.WinnerSide, TimeLimit = state.IsTimeLimit, Ticks = state.CurrentTick,
        };
    }

    /// <summary>
    /// ONE ROW PER GAME, not per cell. The map varies within a cell once --games > 1, and Marc
    /// asked for the map so it can be analysed later; aggregating to cells here would throw
    /// away exactly that. 16,384 rows at the default.
    /// </summary>
    private static void WriteCsv(string path, GameRow[] rows)
    {
        using var w = new StreamWriter(path, false);
        w.WriteLine("p1_team,p1_off,p1_def,p2_team,p2_off,p2_def,map,shadow,winner_side,decided,ticks,seconds");
        foreach (var r in rows)
        {
            if (r == null) continue;
            string decided = r.Winner == 0 ? (r.TimeLimit ? "timeout_draw" : "draw")
                                           : (r.TimeLimit ? "timeout" : "decisive");
            w.WriteLine($"{r.P1.Team},{r.P1.Offense},{r.P1.Defense}," +
                        $"{r.P2.Team},{r.P2.Offense},{r.P2.Defense}," +
                        $"{r.Map},{(r.Shadow ? 1 : 0)},{r.Winner},{decided},{r.Ticks}," +
                        $"{(r.Ticks / 30.0).ToString("F1", CultureInfo.InvariantCulture)}");
        }
    }

    /// <summary>Reads back what WriteCsv wrote, for --from-csv.</summary>
    private static GameRow[] ReadCsv(string path)
    {
        var list = new List<GameRow>();
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var c = line.Split(',');
            list.Add(new GameRow
            {
                P1 = new CounterMatrix.Loadout(Enum.Parse<TeamColour>(c[0]), c[1], c[2]),
                P2 = new CounterMatrix.Loadout(Enum.Parse<TeamColour>(c[3]), c[4], c[5]),
                Map = Enum.Parse<TeamColour>(c[6]),
                Shadow = c[7] == "1",
                Winner = int.Parse(c[8]),
                TimeLimit = c[9].StartsWith("timeout"),
                Ticks = long.Parse(c[10]),
            });
        }
        return list.ToArray();
    }

    // ---------------------------------------------------------------------------------------
    // AGGREGATION
    //
    // EVERY TALLY COUNTS A GAME TWICE, once for each side's attribute. A game between White and
    // Red is a White game AND a Red game; the winner takes a win in its own bucket and the
    // loser a loss in its. That is what makes the marginal seat-unbiased: the full ordered
    // cross-tab contains both seatings of every pair, so a team's record automatically spans
    // both seats in equal number. Reading only one seat's column would bake the engine's seat
    // asymmetry straight into the answer -- see CLAUDE.md's seat-bias entry.
    // ---------------------------------------------------------------------------------------

    private sealed class Tally
    {
        public int Wins, Losses, Draws;
        public long Ticks;
        public int Games => Wins + Losses + Draws;
        public double Rate => Games > 0 ? (double)Wins / Games : 0;
        /// <summary>Half-width of the 95% interval, in percentage points.</summary>
        public double Ci95 => Games > 0 ? 196.0 * Math.Sqrt(Math.Max(Rate * (1 - Rate), 1e-9) / Games) : 0;
        public void Add(bool won, bool drew, long ticks)
        {
            if (drew) Draws++; else if (won) Wins++; else Losses++;
            Ticks += ticks;
        }
    }

    private static Dictionary<string, Tally> Marginal(GameRow[] rows, Func<CounterMatrix.Loadout, string> key)
    {
        var d = new Dictionary<string, Tally>();
        Tally Get(string k) { if (!d.TryGetValue(k, out var t)) d[k] = t = new Tally(); return t; }
        foreach (var r in rows)
        {
            if (r == null) continue;
            bool drew = r.Winner == 0;
            Get(key(r.P1)).Add(r.Winner == 1, drew, r.Ticks);
            Get(key(r.P2)).Add(r.Winner == 2, drew, r.Ticks);
        }
        return d;
    }

    private static void WriteHtml(string path, GameRow[] rows, int games, int threads, TimeSpan elapsed)
    {
        var live = rows.Where(r => r != null).ToArray();
        var teams = Marginal(live, l => l.Team.ToString());
        var offs = Marginal(live, l => l.Offense);
        var defs = Marginal(live, l => l.Defense);
        var full = Marginal(live, l => $"{l.Team}/{l.Offense}/{l.Defense}");

        // Seat check. P1 win rate over the WHOLE matrix is the single number that says whether
        // the sweep is seat-balanced; every matchup appears in both seats, so anything far
        // from 50% is the engine's asymmetry, not a loadout effect.
        int p1w = live.Count(r => r.Winner == 1), p2w = live.Count(r => r.Winner == 2),
            drw = live.Count(r => r.Winner == 0);

        // Per-map: P1 advantage and game length. A map cannot favour a LOADOUT (both sides play
        // the same map), so its win rate is meaningless -- what it can do is favour a SEAT or
        // change how long games run, and those are the two things shown.
        var mapKeys = live.Select(r => (r.Shadow ? "shadow " : "") + r.Map).Distinct().OrderBy(s => s).ToList();

        var sb = new StringBuilder();
        sb.Append(@"<!doctype html>
<html lang=""en""><head><meta charset=""utf-8"">
<title>Castle Defense — Balance Matrix</title>
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<style>
  :root{--bg:#f9f9f7;--card:#fcfcfb;--ink:#0b0b0b;--ink2:#52514e;--ink3:#898781;
        --line:#e1e0d9;--good:#0ca30c;--bad:#d03b3b;--bar:#3987e5;--bar2:#c3d9f5}
  @media (prefers-color-scheme:dark){
    :root{--bg:#0d0d0d;--card:#1a1a19;--ink:#fff;--ink2:#c3c2b7;--ink3:#898781;
          --line:#2c2c2a;--bad:#e66767;--bar2:#1c3a5e}}
  *{box-sizing:border-box}
  body{margin:0;background:var(--bg);color:var(--ink);
       font:14px/1.5 ui-sans-serif,system-ui,-apple-system,'Segoe UI',sans-serif}
  .wrap{max-width:1100px;margin:0 auto;padding:32px 20px 64px}
  h1{font-size:24px;margin:0 0 4px;letter-spacing:-.01em}
  h2{font-size:15px;margin:36px 0 10px;letter-spacing:.04em;text-transform:uppercase;color:var(--ink2)}
  .sub{color:var(--ink3);font-size:13px;margin:0 0 8px}
  .card{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:16px 18px;margin-top:10px}
  table{border-collapse:collapse;width:100%;font-variant-numeric:tabular-nums}
  th,td{text-align:right;padding:5px 8px;border-bottom:1px solid var(--line);white-space:nowrap}
  th{font-size:11px;letter-spacing:.04em;text-transform:uppercase;color:var(--ink3);font-weight:600}
  td:first-child,th:first-child{text-align:left}
  tr:last-child td{border-bottom:none}
  .bar{position:relative;height:9px;border-radius:5px;background:var(--bar2);min-width:120px}
  .bar>i{position:absolute;inset:0 auto 0 0;border-radius:5px;background:var(--bar);display:block}
  .ci{color:var(--ink3);font-size:12px}
  .hi{color:var(--good);font-weight:600}.lo{color:var(--bad);font-weight:600}
  .grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(300px,1fr));gap:14px}
  .scroll{overflow-x:auto}
  .mx td,.mx th{padding:4px 6px;font-size:12px;text-align:center}
  .mx td:first-child{text-align:left;font-weight:600}
  .note{color:var(--ink3);font-size:12px;margin-top:8px;line-height:1.55}
  code{font:12px ui-monospace,SFMono-Regular,Menlo,monospace;background:var(--bar2);
       padding:1px 5px;border-radius:4px}
</style></head><body><div class=""wrap"">");

        // Built outside the interpolation: a TimeSpan format string is full of backslashes and
        // they do not survive being nested inside a verbatim interpolated string.
        string runLabel = elapsed > TimeSpan.Zero
            ? $"run {elapsed:hh\\:mm\\:ss} on {threads} threads &middot; "
            : "rebuilt from CSV &middot; ";

        sb.Append($@"
<h1>Balance Matrix</h1>
<p class=""sub"">HeuristicBot mirror &middot; every loadout vs every loadout, both seats &middot;
{live.Length:N0} games &middot; {games} game(s) per ordered cell &middot;
{runLabel}{DateTime.Now:yyyy-MM-dd HH:mm}</p>");

        // ---- MARGINALS ------------------------------------------------------------------
        sb.Append(@"<h2>Marginals</h2>
<p class=""sub"">Win rate averaged over every opponent and both seats. This is the balance
signal: a team appears in 16 loadouts against 128 opponents, so even one game per cell gives
it thousands of games.</p><div class=""grid"">");
        sb.Append(MarginalTable("Team", teams));
        sb.Append(MarginalTable("Offence", offs));
        sb.Append(MarginalTable("Defence", defs));
        sb.Append("</div>");

        // ---- SEAT CHECK -----------------------------------------------------------------
        sb.Append($@"<h2>Seat check</h2><div class=""card"">
<table><tr><th>Outcome</th><th>Games</th><th>Share</th></tr>
<tr><td>P1 wins</td><td>{p1w:N0}</td><td>{100.0 * p1w / live.Length:F1}%</td></tr>
<tr><td>P2 wins</td><td>{p2w:N0}</td><td>{100.0 * p2w / live.Length:F1}%</td></tr>
<tr><td>Draws</td><td>{drw:N0}</td><td>{100.0 * drw / live.Length:F1}%</td></tr></table>
<p class=""note"">Every matchup is played in both seats, so this should sit near 50/50.
A large gap is the engine's seat asymmetry showing through, not a balance finding — see
CLAUDE.md's seat-bias entry. It does not distort the marginals above, which count both seats
equally by construction.</p></div>");

        // ---- MAPS -----------------------------------------------------------------------
        sb.Append(@"<h2>Maps</h2>
<p class=""sub"">A map cannot favour a loadout — both sides play it — so its win rate carries no
signal. What it can move is the seat advantage and how long games run.</p>
<div class=""card scroll""><table><tr><th>Map</th><th>Games</th><th>P1 wins</th><th>Draws</th>
<th>Avg length</th></tr>");
        foreach (var mk in mapKeys)
        {
            var g = live.Where(r => ((r.Shadow ? "shadow " : "") + r.Map) == mk).ToArray();
            if (g.Length == 0) continue;
            double p1 = 100.0 * g.Count(r => r.Winner == 1) / g.Length;
            double dr = 100.0 * g.Count(r => r.Winner == 0) / g.Length;
            double sec = g.Average(r => r.Ticks) / 30.0;
            sb.Append($@"<tr><td>{mk}</td><td>{g.Length:N0}</td><td>{p1:F1}%</td>" +
                      $@"<td>{dr:F1}%</td><td>{sec:F0}s</td></tr>");
        }
        sb.Append("</table></div>");

        // ---- TEAM x TEAM ----------------------------------------------------------------
        sb.Append(@"<h2>Team vs team</h2>
<p class=""sub"">Row team's win rate against column team, both seats pooled. The diagonal is a
mirror and should sit near 50%.</p><div class=""card scroll""><table class=""mx"">");
        var teamList = Enum.GetValues<TeamColour>().Select(t => t.ToString()).ToList();
        sb.Append("<tr><th></th>");
        foreach (var c in teamList) sb.Append($"<th>{c[..3]}</th>");
        sb.Append("<th>all</th></tr>");
        foreach (var rt in teamList)
        {
            sb.Append($"<tr><td>{rt}</td>");
            foreach (var ct in teamList)
            {
                // COUNTED PER SIDE, NOT PER GAME, for the same reason the marginals are: a
                // game contributes to whichever side's bucket it belongs in. On the DIAGONAL
                // the row team is on BOTH sides, so the game lands in the bucket twice -- once
                // as a win and once as a loss -- and a mirror correctly reads ~50%. Counting
                // it once instead scored every mirror as a guaranteed win and put the whole
                // diagonal at 93-98%.
                int w = 0, tot = 0;
                foreach (var r in live)
                {
                    if (r.P1.Team.ToString() == rt && r.P2.Team.ToString() == ct)
                    {
                        tot++;
                        if (r.Winner == 1) w++;
                    }
                    if (r.P2.Team.ToString() == rt && r.P1.Team.ToString() == ct)
                    {
                        tot++;
                        if (r.Winner == 2) w++;
                    }
                }
                double v = tot > 0 ? 100.0 * w / tot : 0;
                string cls = v >= 60 ? "hi" : v <= 40 ? "lo" : "";
                sb.Append($@"<td class=""{cls}"">{v:F0}</td>");
            }
            sb.Append($@"<td><b>{100 * teams[rt].Rate:F1}</b></td></tr>");
        }
        sb.Append("</table></div>");

        // ---- EXTREME LOADOUTS -----------------------------------------------------------
        var ordered = full.OrderByDescending(kv => kv.Value.Rate).ToList();
        sb.Append(@"<h2>Strongest and weakest loadouts</h2>
<p class=""sub"">Individual loadouts, 256 games each at one game per cell — noisier than the
marginals above. Read the ordering, not the exact rates.</p><div class=""grid"">");
        sb.Append(LoadoutTable("Top 12", ordered.Take(12)));
        sb.Append(LoadoutTable("Bottom 12", ordered.AsEnumerable().Reverse().Take(12)));
        sb.Append("</div>");

        sb.Append($@"<p class=""note"" style=""margin-top:28px"">
Regenerate with <code>CastleDefense.BotArena.exe balance-matrix</code>.
Raw per-game rows, including the map, are in <code>balance_matrix.csv</code> alongside this file.
Both seats are HeuristicBot, so absolute rates say nothing about a human opponent — only the
ordering has any claim to transfer.</p>");

        sb.Append("</div></body></html>");
        File.WriteAllText(path, sb.ToString());
    }

    private static string MarginalTable(string title, Dictionary<string, Tally> d)
    {
        var sb = new StringBuilder();
        sb.Append($@"<div class=""card""><table><tr><th>{title}</th><th>Win rate</th><th></th><th>95%</th></tr>");
        foreach (var kv in d.OrderByDescending(kv => kv.Value.Rate))
        {
            double v = 100 * kv.Value.Rate;
            string cls = v >= 60 ? "hi" : v <= 40 ? "lo" : "";
            sb.Append($@"<tr><td>{kv.Key}</td><td class=""{cls}"">{v:F1}%</td>" +
                      $@"<td style=""width:100%""><span class=""bar""><i style=""width:{v:F1}%""></i></span></td>" +
                      $@"<td class=""ci"">&plusmn;{kv.Value.Ci95:F1}</td></tr>");
        }
        return sb.Append("</table></div>").ToString();
    }

    private static string LoadoutTable(string title, IEnumerable<KeyValuePair<string, Tally>> items)
    {
        var sb = new StringBuilder();
        sb.Append($@"<div class=""card""><table><tr><th>{title}</th><th>Win rate</th><th>Games</th></tr>");
        foreach (var kv in items)
        {
            double v = 100 * kv.Value.Rate;
            string cls = v >= 60 ? "hi" : v <= 40 ? "lo" : "";
            sb.Append($@"<tr><td>{kv.Key}</td><td class=""{cls}"">{v:F1}%</td><td>{kv.Value.Games}</td></tr>");
        }
        return sb.Append("</table></div>").ToString();
    }
}
