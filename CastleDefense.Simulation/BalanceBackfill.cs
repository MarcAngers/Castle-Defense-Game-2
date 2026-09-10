using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CastleDefense.Api.Data;
using CastleDefense.Engine;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Replays;

namespace CastleDefense.Simulation
{
    /// <summary>
    /// WHICH BALANCE DATA WAS EACH OLD GAME PLAYED UNDER? Recovers it for replays recorded
    /// before CDRP v4 started writing the fingerprint down.
    ///
    /// ── WHY THE DATE IS ONLY A PRIOR, NOT THE ANSWER ─────────────────────────────────────
    ///
    /// The obvious method is "look at the commit timestamps and pick the version in effect
    /// when the game was played". That is wrong often enough to be dangerous, for two
    /// independent reasons already recorded in CLAUDE.md:
    ///
    ///  * THE ENGINE READS ITS BUILD OUTPUT, NOT THE SOURCE TREE. `AppContext.BaseDirectory/
    ///    Data` only updates on a SUCCESSFUL build, and a rebuild fails silently (MSB3027)
    ///    while the dev server holds Engine.dll open. Game 1F3C07 was played three minutes
    ///    after the CSV was edited and ran the copy from thirteen hours earlier.
    ///  * MARC DOES NOT ALWAYS COMMIT PROMPTLY. His own words. A game can be played under
    ///    working-tree values that reach git hours or days later, or never in that exact form.
    ///
    /// So the date picks the CANDIDATE ORDER, and the replay itself decides. Every historical
    /// version of both CSVs is pulled out of git, and each candidate is scored by REBUILDING
    /// THE GAME against it:
    ///
    ///     EXACT  = no recorded action failed AND the rebuild lands on the recorded tick,
    ///              winner and final money (checked to the dollar against game_records.db).
    ///
    /// The recorder only ever writes an action the live engine accepted, so a version under
    /// which every recorded action is legal AND the outcome reproduces is the version the game
    /// was played under. Where two versions both reproduce a game -- which happens constantly
    /// for short or idle games that never touch the changed rows -- the result is honestly
    /// reported as ambiguous rather than resolved by coin flip.
    ///
    /// ── WHAT GETS WRITTEN, AND WHERE ─────────────────────────────────────────────────────
    ///
    /// Into game_records.db, NOT into the .replay files. Two reasons, both load-bearing:
    ///
    ///  * AN INFERENCE IS NOT A RECORDING. The v4 fields mean "the engine wrote down what it
    ///    loaded". Back-filling a guess into them destroys exactly the distinction that makes
    ///    the fingerprint worth having, and no later reader could tell the two apart.
    ///  * The replay files are the durable artefact of Marc's play record. Rewriting 366 of
    ///    them in place to add derived metadata is a large irreversible risk for no gain --
    ///    and this inference IMPROVES as more of the history is committed, so it wants to be
    ///    re-runnable, which a one-way file rewrite is not.
    ///
    /// The analysis pages merge the two, and label which they are showing.
    /// </summary>
    public static class BalanceBackfill
    {
        private sealed class Version
        {
            public string Kind;              // "gadgets" or "roster"
            public string Hash;
            public string Sha;               // first commit that introduced this content
            public DateTime Date;            // ...and its author date
            public string Dir;               // where the pair gets materialised
        }

        private sealed class Candidate
        {
            public Version Gadgets, Roster;
            public string Dir;
            public string Label => $"{Gadgets.Sha}/{Roster.Sha}";
        }

        private const string GadgetsPath = "CastleDefense.Engine/Data/master_gadgets.csv";
        private const string RosterPath = "CastleDefense.Engine/Data/master_roster.csv";

        public static void Run(string[] args, string recordingsDir)
        {
            string repoRoot = FindRepoRoot();
            if (repoRoot == null) { Console.WriteLine("Not inside a git checkout."); return; }

            bool force = args.Contains("--force");
            int limit = 0;
            string only = null;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--limit" && i + 1 < args.Length) limit = int.Parse(args[++i]);
                else if (args[i] == "--game" && i + 1 < args.Length) only = args[++i];
            }

            string cache = Path.Combine(Path.GetTempPath(), "cd_balance_history");
            Directory.CreateDirectory(cache);

            Console.WriteLine("=== BALANCE BACKFILL ===");
            Console.WriteLine($"  repo      {repoRoot}");
            Console.WriteLine($"  recordings{recordingsDir}");

            var gadgetVersions = HistoryOf(repoRoot, GadgetsPath, "gadgets", cache);
            var rosterVersions = HistoryOf(repoRoot, RosterPath, "roster", cache);
            AddWorkingTree(repoRoot, GadgetsPath, "gadgets", cache, gadgetVersions);
            AddWorkingTree(repoRoot, RosterPath, "roster", cache, rosterVersions);

            Console.WriteLine($"\n  {gadgetVersions.Count} distinct gadget versions, " +
                              $"{rosterVersions.Count} distinct roster versions");

            var db = new GameDatabase(Path.Combine(recordingsDir, "game_records.db"));
            var rows = db.GetAnalysisRows();

            var files = new List<string>();
            foreach (var dir in Directory.EnumerateDirectories(recordingsDir))
            {
                if (Path.GetFileName(dir).StartsWith("quarantine", StringComparison.OrdinalIgnoreCase)) continue;
                files.AddRange(Directory.EnumerateFiles(dir, "*.replay"));
            }
            files.Sort();

            var jobs = new List<(string path, ReplayFile rf, GameDatabase.AnalysisRow row)>();
            foreach (var f in files)
            {
                ReplayFile rf;
                try { rf = ReplayFile.Read(f); } catch { continue; }
                if (only != null && !string.Equals(rf.GameId, only, StringComparison.OrdinalIgnoreCase)) continue;
                // A v4 replay already KNOWS. Never overwrite a recording with an inference.
                if (rf.HasV4 && !string.IsNullOrEmpty(rf.GadgetsHash) && !force) continue;
                rows.TryGetValue(rf.GameId, out var row);
                jobs.Add((f, rf, row));
                if (limit > 0 && jobs.Count >= limit) break;
            }
            Console.WriteLine($"  {jobs.Count} replays need a fingerprint\n");
            if (jobs.Count == 0) return;

            // Only versions that could plausibly have been in effect are worth testing. The
            // cut is the OLDEST game in the corpus, less a month of slack for the case this
            // whole tool exists for: a build (or a commit) lagging behind the source tree.
            // Everything older is not merely unlikely, it is often not loadable at all.
            long oldest = jobs.Where(j => j.row != null && j.row.PlayedAt > 0)
                              .Select(j => j.row.PlayedAt).DefaultIfEmpty(0).Min();
            if (oldest > 0)
            {
                var cut = DateTimeOffset.FromUnixTimeSeconds(oldest).LocalDateTime.AddDays(-31);
                int gBefore = gadgetVersions.Count, rBefore = rosterVersions.Count;
                gadgetVersions = KeepRelevant(gadgetVersions, cut);
                rosterVersions = KeepRelevant(rosterVersions, cut);
                Console.WriteLine($"  oldest game {DateTimeOffset.FromUnixTimeSeconds(oldest).LocalDateTime:yyyy-MM-dd}; " +
                                  $"testing {gadgetVersions.Count}/{gBefore} gadget and " +
                                  $"{rosterVersions.Count}/{rBefore} roster versions\n");
            }

            // Candidate ORDER per game: the version implied by the date first, then outward in
            // time. Almost every game resolves on the first try, which is what keeps this a
            // two-minute job rather than a 366 x 30 sweep.
            var results = new ConcurrentDictionary<string, (string g, string r, string method, string detail)>();
            var sw = Stopwatch.StartNew();

            // EVERY candidate is tried against EVERY game, rather than stopping at the first
            // that works. First-match-wins cannot tell "this version reproduces it and the
            // others do not" from "nothing reproduces it", and those mean completely
            // different things -- see the labels below. A failing rebuild is cheap (the game
            // collapses early once actions start being refused), so the full grid is seconds.
            var trials = new ConcurrentDictionary<string, List<(Candidate cand, Trial t, string g, string r)>>();
            foreach (var j in jobs) trials[j.rf.GameId] = new List<(Candidate, Trial, string, string)>();

            // Group by candidate so GameDataManager is initialised once per version, not once
            // per game -- it is static, so this cannot be parallelised across versions.
            var ordered = OrderedCandidates(gadgetVersions, rosterVersions, cache);
            foreach (var cand in ordered)
            {
                var batch = jobs;
                if (batch.Count == 0) continue;

                // A CSV old enough to predate a column the current engine requires simply
                // cannot be loaded, and that is not an error: it means the game in question
                // was played by a build this one is not compatible with, so it cannot be a
                // candidate. The oldest rosters here are from February, five months before
                // the first surviving replay.
                string gHash, rHash;
                try
                {
                    GameDataManager.Initialize(cand.Dir);
                    gHash = GameDataManager.GadgetsFileHash;
                    rHash = GameDataManager.RosterFileHash;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  [skip] {cand.Label,-20} not loadable by this engine ({ex.GetType().Name})");
                    continue;
                }

                int hits = 0;
                System.Threading.Tasks.Parallel.ForEach(batch, j =>
                {
                    var t = Score(j.rf, j.row);
                    lock (trials) trials[j.rf.GameId].Add((cand, t, gHash, rHash));
                    if (t.Exact) System.Threading.Interlocked.Increment(ref hits);
                });
                Console.WriteLine($"  [{sw.Elapsed:mm\\:ss}] {cand.Label,-20} reproduced {hits,4} of {batch.Count}");
                if (only != null)
                    foreach (var j in batch)
                        Console.WriteLine($"      {cand.Label,-20} {trials[j.rf.GameId].Last().t.Why}");
            }

            // ── verdicts ────────────────────────────────────────────────────────────────
            foreach (var j in jobs)
            {
                var all = trials[j.rf.GameId];
                var exact = all.Where(x => x.t.Exact).ToList();
                int recorded = j.rf.A1.Count(b => b != 0) + j.rf.A2.Count(b => b != 0);

                if (exact.Count > 0)
                {
                    // Prefer the date-plausible one among equals; they reproduce the game
                    // identically, so the tie is broken on which was actually in effect.
                    var pick = exact.OrderBy(x => RankFor(j.row, x.cand)).First();
                    string method = exact.Select(x => x.g + x.r).Distinct().Count() == 1
                                        && recorded >= MinActionsToDiscriminate
                                    ? "verified" : "ambiguous";
                    results[j.rf.GameId] = (pick.g, pick.r, method,
                        pick.cand.Label + (method == "ambiguous"
                            ? $" ({exact.Count} versions reproduce it, {recorded} actions)"
                            : $" ({recorded} actions)"));
                    continue;
                }

                // NOT CONFIRMABLE -- fall back to the commit timeline, which is the honest
                // answer and, for most of this corpus, a good one: the gadget CSV went
                // untouched from 2026-07-09 to 2026-08-24, so every game in that seven-week
                // window has exactly one possible version and no ambiguity to resolve.
                //
                // WHY CONFIRMATION USUALLY FAILS, AND WHY IT IS NOT THE CSV'S FAULT. Only 4
                // of 353 games replay cleanly under ANY historical balance file. The engine
                // itself has moved: unit charges (2026-09-01) refuse the spam a pre-charge
                // opponent recorded, the opening squad and $10 start (2026-08-26) change
                // every economy from tick 0, map effects (2026-08-27) change combat outright.
                // No balance file can undo a code change, so a failed rebuild here says
                // nothing about which CSV was loaded. The detail string records what was
                // actually seen so the claim can be checked rather than taken on faith.
                var best = all.OrderBy(x => x.t.Cost).First();
                var byDate = ByDate(gadgetVersions, rosterVersions, cache, j.row?.PlayedAt ?? 0);
                string why = recorded < MinActionsToDiscriminate
                    ? $"only {recorded} recorded actions, too few to test"
                    : AllFailAlike(all)
                        ? $"no version replays this game under the current engine " +
                          $"(every version refuses ~{100.0 * best.t.Failed / Math.Max(1, best.t.Attempted):F0}% of actions)"
                        : $"no version replays it cleanly; closest was {best.cand.Label} ({best.t.Why})";
                results[j.rf.GameId] = (byDate.Gadgets.Hash, byDate.Roster.Hash, "dated",
                                        byDate.Label + " -- " + why);
            }

            int exact2 = results.Values.Count(v => v.method == "verified");
            int amb = results.Values.Count(v => v.method == "ambiguous");
            int dated = results.Values.Count(v => v.method == "dated");
            Console.WriteLine($"\n  VERIFIED  {exact2,4}   one version reproduces the recorded game exactly");
            Console.WriteLine($"  AMBIGUOUS {amb,4}   reproduces, but so would another version");
            Console.WriteLine($"  DATED     {dated,4}   from the commit timeline; the rebuild could not confirm it");
            Console.WriteLine($"  elapsed {sw.Elapsed:mm\\:ss}");

            // The era breakdown is the part worth reading. Where a CSV went untouched for
            // weeks, every game inside that window has exactly one possible version and the
            // timeline is not a guess at all.
            Console.WriteLine("\n  assigned gadget CSV by era:");
            foreach (var grp in results.Values.GroupBy(v => v.g).OrderByDescending(g => g.Count()))
            {
                var ver = gadgetVersions.FirstOrDefault(v => v.Hash == grp.Key);
                Console.WriteLine($"    {grp.Count(),4} games  {grp.Key}  " +
                                  $"{(ver == null ? "?" : ver.Sha + " " + ver.Date.ToString("yyyy-MM-dd"))}");
            }

            foreach (var kv in results)
                db.SetInferredBalance(kv.Key, kv.Value.g, kv.Value.r, kv.Value.method, kv.Value.detail);
            Console.WriteLine($"\n  wrote {results.Count} rows to game_records.db");
        }

        /// <summary>
        /// EXACT reproduction, or nothing. Returns "verified" when the game has enough recorded
        /// actions for the test to mean something, "ambiguous" when it reproduces but the game
        /// is too thin to distinguish versions, and null when it does not reproduce at all.
        ///
        /// THE ACTION-COUNT THRESHOLD IS THE WHOLE HONESTY OF THIS TOOL. Game 12B538 has seven
        /// recorded actions; every version of the CSV replays all seven, so "it reproduced" says
        /// nothing about which one was used. Calling that verified would put a confident wrong
        /// answer in the database.
        /// </summary>
        /// <summary>How well one candidate CSV pair reproduced one recorded game.</summary>
        private sealed class Trial
        {
            public int Failed, Attempted;
            public long TickDelta;
            public bool WinnerMatch;
            public bool Exact;
            public bool Threw;
            public string Why = "";
            /// <summary>Lower is better. Failed actions dominate; ties break on end-tick error.</summary>
            public double Cost => Threw ? double.MaxValue
                                        : Failed * 1000.0 + Math.Abs(TickDelta) + (WinnerMatch ? 0 : 1e6);
        }

        private static Trial Score(ReplayFile rf, GameDatabase.AnalysisRow row)
        {
            var t = new Trial();
            ReplayAnalysis.Result res;
            try { res = ReplayAnalysis.Run(rf, new ReplayAnalysis.Options { SampleEverySeconds = 1000 }); }
            catch (Exception ex) { t.Threw = true; t.Why = ex.GetType().Name; return t; }

            t.Failed = res.Fidelity.Failed[1] + res.Fidelity.Failed[2];
            t.Attempted = res.Fidelity.Attempted[1] + res.Fidelity.Attempted[2];
            t.TickDelta = res.RebuiltTicks - res.RecordedTicks;
            t.WinnerMatch = res.RebuiltWinner == res.RecordedWinner;

            bool moneyOk = row?.P1FinalMoney == null ||
                           (Math.Abs(res.FinalMoney[1] - row.P1FinalMoney.Value) < 1.0 &&
                            Math.Abs(res.FinalMoney[2] - row.P2FinalMoney.Value) < 1.0);
            t.Exact = t.Failed == 0 && t.TickDelta == 0 && t.WinnerMatch && moneyOk;

            if (t.Failed > 0) t.Why = $"{t.Failed}/{t.Attempted} actions failed";
            else if (t.TickDelta != 0) t.Why = $"ends {t.TickDelta:+#;-#;0} ticks off";
            else if (!t.WinnerMatch) t.Why = $"winner P{res.RebuiltWinner} not P{res.RecordedWinner}";
            else if (!moneyOk) t.Why = $"money {res.FinalMoney[1]:F0}/{res.FinalMoney[2]:F0} " +
                                       $"vs {row.P1FinalMoney:F0}/{row.P2FinalMoney:F0}";
            else t.Why = "exact";
            return t;
        }

        /// <summary>
        /// Below this many recorded actions a clean rebuild is not evidence about WHICH CSV was
        /// used -- a game that never casts a gadget cannot notice a gadget repricing. 40 is
        /// deliberately conservative: at that volume a game has spent, invested and cast enough
        /// that a changed row would almost certainly have been touched.
        /// </summary>
        private const int MinActionsToDiscriminate = 40;

        /// <summary>
        /// Everything at or after the cut, PLUS the newest one before it -- that last one is
        /// the version actually in effect when the corpus begins, and dropping it would leave
        /// the oldest games with no correct candidate at all.
        /// </summary>
        private static List<Version> KeepRelevant(List<Version> all, DateTime cut)
        {
            var after = all.Where(v => v.Date >= cut).ToList();
            var lastBefore = all.Where(v => v.Date < cut).OrderByDescending(v => v.Date).FirstOrDefault();
            if (lastBefore != null) after.Add(lastBefore);
            return after.OrderBy(v => v.Date).ToList();
        }

        /// <summary>
        /// True when every candidate refused about the same number of actions -- the signature
        /// of a game the CURRENT ENGINE cannot replay at all, as opposed to one whose CSV is
        /// merely missing. Judged on the spread relative to the best: if swapping the entire
        /// balance file moves the failure count by less than a fifth, the balance file is not
        /// what is breaking it.
        /// </summary>
        private static bool AllFailAlike(List<(Candidate cand, Trial t, string g, string r)> all)
        {
            var live = all.Where(x => !x.t.Threw && x.t.Attempted > 0).Select(x => x.t).ToList();
            if (live.Count < 2) return false;
            if (live.Any(t => t.Failed == 0)) return false;   // something replayed cleanly; not this

            // COMPARE RATES, NOT COUNTS. A rebuild that starts refusing actions kills a castle
            // early and so ATTEMPTS far fewer of them -- game 408D85 fails 716 of 721 under one
            // version and 469 of 472 under another, which looks like a 250-action spread and is
            // actually 99.3% against 99.4%. Counting absolutes reads that as "the CSV matters
            // here", which is exactly backwards.
            double lo = live.Min(t => (double)t.Failed / t.Attempted);
            double hi = live.Max(t => (double)t.Failed / t.Attempted);
            return (hi - lo) <= 0.10;
        }

        private static int RankFor(GameDatabase.AnalysisRow row, Candidate cand)
        {
            long played = row?.PlayedAt ?? 0;
            if (played == 0) return 0;
            var when = DateTimeOffset.FromUnixTimeSeconds(played).LocalDateTime;
            // Distance in days from the version being in effect. Negative distances (a version
            // committed AFTER the game) are allowed but ranked worse, because that is the
            // "played on uncommitted values" case and it is real.
            double dg = (when - cand.Gadgets.Date).TotalDays;
            double dr = (when - cand.Roster.Date).TotalDays;
            double score = (dg >= 0 ? dg : -dg * 4) + (dr >= 0 ? dr : -dr * 4);
            return (int)Math.Min(int.MaxValue, score);
        }

        private static List<Candidate> OrderedCandidates(List<Version> g, List<Version> r, string cache)
        {
            var outp = new List<Candidate>();
            foreach (var gv in g.OrderByDescending(v => v.Date))
                foreach (var rv in r.OrderByDescending(v => v.Date))
                    outp.Add(Materialise(gv, rv, cache));
            return outp;
        }

        private static Candidate ByDate(List<Version> g, List<Version> r, string cache, long played)
        {
            var when = played == 0 ? DateTime.Now : DateTimeOffset.FromUnixTimeSeconds(played).LocalDateTime;
            var gv = g.Where(v => v.Date <= when).OrderByDescending(v => v.Date).FirstOrDefault()
                     ?? g.OrderBy(v => v.Date).First();
            var rv = r.Where(v => v.Date <= when).OrderByDescending(v => v.Date).FirstOrDefault()
                     ?? r.OrderBy(v => v.Date).First();
            return Materialise(gv, rv, cache);
        }

        private static readonly Dictionary<string, Candidate> _pairs = new();

        private static Candidate Materialise(Version g, Version r, string cache)
        {
            string key = g.Hash + "_" + r.Hash;
            if (_pairs.TryGetValue(key, out var hit)) return hit;
            string dir = Path.Combine(cache, "pair_" + key);
            Directory.CreateDirectory(dir);
            File.Copy(Path.Combine(g.Dir, "master_gadgets.csv"), Path.Combine(dir, "master_gadgets.csv"), true);
            File.Copy(Path.Combine(r.Dir, "master_roster.csv"), Path.Combine(dir, "master_roster.csv"), true);
            var c = new Candidate { Gadgets = g, Roster = r, Dir = dir };
            _pairs[key] = c;
            return c;
        }

        /// <summary>
        /// Every distinct CONTENT of one CSV across history, materialised into the cache.
        /// Deduplicated by content: a commit that touched the file without changing it is not a
        /// new candidate, and re-testing it would just cost time.
        /// </summary>
        private static List<Version> HistoryOf(string repo, string path, string kind, string cache)
        {
            var outp = new List<Version>();
            var seen = new HashSet<string>();
            string log = Git(repo, "log", "--follow", "--format=%H|%ct", "--", path);
            var lines = log.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                           .Select(l => l.Trim().Split('|'))
                           .Where(p => p.Length == 2)
                           .Select(p => (sha: p[0], when: long.Parse(p[1])))
                           .OrderBy(x => x.when);
            foreach (var (sha, when) in lines)
            {
                var bytes = GitBytes(repo, sha + ":" + path);
                if (bytes == null || bytes.Length == 0) continue;
                string dir = Path.Combine(cache, kind + "_" + sha[..8]);
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, Path.GetFileName(path));
                File.WriteAllBytes(file, bytes);
                string hash = GameDataManager.HashDataFile(file);
                if (!seen.Add(hash)) { continue; }
                outp.Add(new Version
                {
                    Kind = kind, Hash = hash, Sha = sha[..8], Dir = dir,
                    Date = DateTimeOffset.FromUnixTimeSeconds(when).LocalDateTime
                });
            }
            return outp;
        }

        /// <summary>
        /// The working tree counts as a candidate too, and it is the one the most recent games
        /// were most likely played under -- uncommitted edits are exactly the case the commit
        /// timeline cannot see.
        /// </summary>
        private static void AddWorkingTree(string repo, string path, string kind, string cache, List<Version> into)
        {
            string src = Path.Combine(repo, path);
            if (!File.Exists(src)) return;
            string hash = GameDataManager.HashDataFile(src);
            if (into.Any(v => v.Hash == hash)) return;
            string dir = Path.Combine(cache, kind + "_worktree");
            Directory.CreateDirectory(dir);
            File.Copy(src, Path.Combine(dir, Path.GetFileName(path)), true);
            into.Add(new Version
            {
                Kind = kind, Hash = hash, Sha = "worktree", Dir = dir,
                Date = File.GetLastWriteTime(src)
            });
        }

        private static string Git(string repo, params string[] a) => RunGit(repo, a, out _);

        private static byte[] GitBytes(string repo, string spec)
        {
            var psi = new ProcessStartInfo("git", $"show {spec}")
            {
                WorkingDirectory = repo, RedirectStandardOutput = true,
                RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            using var ms = new MemoryStream();
            p.StandardOutput.BaseStream.CopyTo(ms);
            p.WaitForExit();
            return p.ExitCode == 0 ? ms.ToArray() : null;
        }

        private static string RunGit(string repo, string[] a, out int code)
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = repo, RedirectStandardOutput = true,
                RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            };
            foreach (var x in a) psi.ArgumentList.Add(x);
            using var p = Process.Start(psi);
            string outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            code = p.ExitCode;
            return outp;
        }

        private static string FindRepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 8 && d != null; i++, d = d.Parent)
                if (Directory.Exists(Path.Combine(d.FullName, ".git"))) return d.FullName;
            return null;
        }
    }
}
