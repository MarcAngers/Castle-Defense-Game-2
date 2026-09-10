using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CastleDefense.Api.Data;
using CastleDefense.Engine.Replays;

namespace CastleDefense.Simulation
{
    /// <summary>
    /// WHICH REPLAYS COUNT AS MARC'S GAMES -- the corpus-selection half of the replay
    /// tooling. Split out of ReplayFile on 2026-09-09 when the FORMAT moved to
    /// CastleDefense.Engine.Replays so the web project could read replays too (it cannot
    /// reference this assembly -- CastleDefense.Simulation references CastleDefenseGame2,
    /// so the arrow only points one way).
    ///
    /// This half stayed here BECAUSE it depends on GameDatabase, and because it is
    /// analysis policy rather than file format: the rule that a win by default is not a
    /// win has exactly one home, and this is it. See IsRealResult.
    /// </summary>
    public static class ReplayCorpus
    {
        // ── Which replays are human games ────────────────────────────────────────────

        /// <summary>
        /// Not every .replay in the folder is a human game. League-watch recordings are
        /// bot-vs-bot, so treating seat 1 as "the human" on them measures one bot against
        /// another. 12 of the 153 files in the live recordings folder are exactly that.
        /// </summary>
        public static bool IsHumanPlayed(string mode, string opponent)
        {
            if (mode == "watch" || mode == "league") return false;
            if (opponent != null && opponent.StartsWith("leaguewatch:", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        /// <summary>
        /// Was this game DECIDED BY PLAY? A game whose loser disconnected and never came
        /// back is awarded to the survivor after a 60-second grace window, and one where
        /// nobody came back is abandoned with no winner at all -- see ReconnectService.
        ///
        /// NOTHING IN THE REPLAY FILE MARKS EITHER. The winner byte of a default win is
        /// byte-identical to an earned one, the action stream just stops early, and a game
        /// that was paused and resumed looks exactly like one that never paused. So this is
        /// the ONLY place the distinction can be made, and it is made from the DB's
        /// end_reason column.
        ///
        /// Such a game says nothing about how well either side plays: it measures a network,
        /// and its short duration and un-fought ending would drag every aggregate --
        /// win rate, game length, earned investments -- toward whatever a disconnection
        /// happens to look like. They are excluded from every corpus selection by default
        /// and included only when `--all` is passed deliberately.
        /// </summary>
        public static bool IsRealResult(string endReason)
            => string.IsNullOrEmpty(endReason)
            || endReason.Equals("normal", StringComparison.OrdinalIgnoreCase);

        public static Dictionary<string, (string mode, string opponent, string endReason)>
            LoadGameModes(string replayDir, string tag)
        {
            var result = new Dictionary<string, (string, string, string)>();
            foreach (var candidate in new[]
                     {
                         Path.Combine(replayDir, "game_records.db"),
                         Path.Combine(replayDir, "..", "game_records.db"),
                     })
            {
                string full = Path.GetFullPath(candidate);
                if (!File.Exists(full)) continue;
                try
                {
                    var db = new GameDatabase(full);
                    foreach (var g in db.GetAllGames())
                        result[g.Id] = (g.GameMode, g.OpponentType, g.EndReason);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[{tag}] could not read {full}: {ex.Message}");
                }
                break;
            }
            if (result.Count == 0)
                Console.WriteLine($"[{tag}] no game_records.db found — cannot tell human games " +
                                  "from league-watch recordings, so NOTHING is filtered.");
            return result;
        }

        /// <summary>
        /// The shared game-selection pass both tools run, so they always fit and score on
        /// the same population.
        ///
        /// `half` splits the games for HOLDOUT validation. A behaviour clone fitted on the
        /// same games the similarity metric is scored on will look good by construction, so
        /// fitting on "a" and scoring on "b" is the only way that number means anything.
        /// The split is by sorted position, which is stable as long as the folder is, and
        /// interleaved (even/odd) rather than a prefix so both halves span the whole period
        /// Marc has been playing — his game has changed over those weeks, and a
        /// chronological split would measure that drift instead of the clone.
        /// </summary>
        public static List<string> SelectHumanGames(string replayDir, string tag, bool all, string filter,
                                                    string half = null)
        {
            var meta = LoadGameModes(replayDir, tag);
            var selected = new List<string>();
            int droppedNonHuman = 0, droppedFilter = 0, droppedDefault = 0;
            foreach (var f in Directory.GetFiles(replayDir, "*.replay").OrderBy(x => x))
            {
                string id = Path.GetFileNameWithoutExtension(f);
                meta.TryGetValue(id, out var m);
                if (!all && m.Item1 != null && !IsHumanPlayed(m.Item1, m.Item2)) { droppedNonHuman++; continue; }
                // A win by default is not a win. See IsRealResult.
                if (!all && !IsRealResult(m.Item3)) { droppedDefault++; continue; }
                if (filter != null &&
                    !($"{m.Item1} {m.Item2}").Contains(filter, StringComparison.OrdinalIgnoreCase))
                { droppedFilter++; continue; }
                selected.Add(f);
            }
            if (droppedNonHuman > 0)
                Console.WriteLine($"[{tag}] excluded {droppedNonHuman} non-human recording(s) " +
                                  "(league-watch / spectator); pass --all to include them");
            if (droppedDefault > 0)
                Console.WriteLine($"[{tag}] excluded {droppedDefault} recording(s) decided by " +
                                  "DISCONNECT rather than play; pass --all to include them");
            if (droppedFilter > 0) Console.WriteLine($"[{tag}] excluded {droppedFilter} by --filter {filter}");

            if (half == "a" || half == "b")
            {
                int want = half == "a" ? 0 : 1;
                selected = selected.Where((_, i) => i % 2 == want).ToList();
                Console.WriteLine($"[{tag}] HOLDOUT half '{half}': {selected.Count} games");
            }
            return selected;
        }
    }
}
