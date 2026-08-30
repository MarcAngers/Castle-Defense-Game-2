using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CastleDefense.Simulation
{
    /// <summary>
    /// Scans a recordings folder and reports per-side action counts, flagging recordings
    /// where a side never acted for the whole game.
    ///
    /// WHY IT EXISTS. ReplayFile.IsAbandoned already detects the P1 case and Divergence /
    /// PolicyTableExport already skip on it, but nothing ever LISTED them, so the folder
    /// accumulates duds that every future tool has to remember to filter. 11 were quarantined
    /// by hand on 2026-08-05 (recordings/quarantine_no_p1_actions_20260805) and the comment on
    /// IsAbandoned notes the pattern recurs.
    ///
    /// IT DOES NOT DELETE ANYTHING. It writes a quarantine plan and, with --move, relocates
    /// the duds into a dated quarantine folder alongside the existing one. This repository has
    /// already lost ~144 recordings permanently to a bin/ cleanup on 2026-07-14 and they were
    /// unrecoverable; a human-played game is not reproducible, so moving is the only safe
    /// operation here. Deleting the quarantine folder afterwards is a decision for its owner.
    ///
    /// Usage:
    ///   --replay-audit &lt;recordings/singleplayer&gt; [--move] [--include-bot-games]
    /// </summary>
    public static class ReplayAudit
    {
        public static void Run(string dir, string[] rest)
        {
            bool move = rest.Contains("--move");
            bool includeBots = rest.Contains("--include-bot-games");

            if (!Directory.Exists(dir)) { Console.WriteLine($"no such directory: {dir}"); return; }

            var files = Directory.GetFiles(dir, "*.replay").OrderBy(f => f).ToArray();
            var modes = ReplayFile.LoadGameModes(dir, "replay-audit");

            Console.WriteLine($"[replay-audit] {files.Length} replay files in {dir}");
            Console.WriteLine($"[replay-audit] {modes.Count} rows of game metadata available\n");

            var duds = new List<(string path, string id, string mode, string opp, string end, int ticks, int a1, int a2)>();
            int scanned = 0, unreadable = 0, botGames = 0;

            foreach (var f in files)
            {
                ReplayFile rf;
                try { rf = ReplayFile.Read(f); }
                catch (Exception e)
                {
                    unreadable++;
                    Console.WriteLine($"  UNREADABLE {Path.GetFileName(f)}: {e.Message}");
                    continue;
                }

                string id = Path.GetFileNameWithoutExtension(f);
                modes.TryGetValue(id, out var m);
                string mode = m.mode ?? "?", opp = m.opponent ?? "?";
                // A game the loser disconnected out of is EXPECTED to have a short, one-
                // sided action stream, so it is not evidence of an abandoned human game --
                // label it rather than letting it read as one.
                string end = ReplayFile.IsRealResult(m.endReason) ? "-" : m.endReason;

                // League-watch recordings are bot-vs-bot by design. A bot side that never
                // acted is a real dud there too, but "P1 never acted" is not evidence of an
                // abandoned human game, so they are reported separately and never moved
                // unless asked for.
                bool isHuman = m.mode == null || ReplayFile.IsHumanPlayed(m.mode, m.opponent);
                if (!isHuman) botGames++;

                scanned++;
                int a1 = rf.A1.Count(b => b != 0);
                int a2 = rf.A2.Count(b => b != 0);

                if (a1 == 0 || a2 == 0)
                {
                    if (!isHuman && !includeBots) continue;
                    duds.Add((f, id, mode, opp, end, rf.TickCount, a1, a2));
                }
            }

            Console.WriteLine($"[replay-audit] scanned {scanned}, unreadable {unreadable}, "
                            + $"bot-vs-bot {botGames}\n");

            if (duds.Count == 0)
            {
                Console.WriteLine("[replay-audit] no recordings found where a side never acted.");
                return;
            }

            Console.WriteLine($"[replay-audit] {duds.Count} DUD recording(s) -- a side never acted:\n");
            Console.WriteLine($"  {"id",-8} {"mode",-9} {"opponent",-12} {"ended",-10} {"ticks",6} {"P1 acts",8} {"P2 acts",8}  which");
            foreach (var d in duds.OrderBy(d => d.mode).ThenBy(d => d.id))
                Console.WriteLine($"  {d.id,-8} {d.mode,-9} {d.opp,-12} {d.end,-10} {d.ticks,6} {d.a1,8} {d.a2,8}  "
                                + (d.a1 == 0 && d.a2 == 0 ? "BOTH silent" : d.a1 == 0 ? "P1 silent" : "P2 silent"));

            string stamp = DateTime.Now.ToString("yyyyMMdd");
            string target = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dir)) ?? ".",
                                         $"quarantine_silent_side_{stamp}");

            if (!move)
            {
                Console.WriteLine($"\n[replay-audit] DRY RUN -- nothing moved. Re-run with --move to relocate");
                Console.WriteLine($"               these {duds.Count} file(s) to:");
                Console.WriteLine($"                 {target}");
                Console.WriteLine($"               Their rows stay in game_records.db, matching how the 11");
                Console.WriteLine($"               abandoned rerolls were handled on 2026-08-05.");
                return;
            }

            Directory.CreateDirectory(target);
            int moved = 0;
            foreach (var d in duds)
            {
                string dest = Path.Combine(target, Path.GetFileName(d.path));
                if (File.Exists(dest)) { Console.WriteLine($"  skip (already there): {d.id}"); continue; }
                File.Move(d.path, dest);
                moved++;
            }
            Console.WriteLine($"\n[replay-audit] moved {moved} file(s) to {target}");
            Console.WriteLine($"[replay-audit] DB rows left intact -- analysis tools filter on the replay files.");
        }
    }
}
