using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CastleDefense.Engine.Models;

namespace CastleDefense.Engine.Data
{
    /// <summary>
    /// Chooses the singleplayer bot's team and gadgets as a BEST RESPONSE to the loadout
    /// the human just picked, replacing the uniform random roll GameHub used before.
    ///
    /// The table is produced by `CastleDefense.BotArena.exe counter-matrix`, which plays the
    /// full 128x128 loadout cross-tab with the human seat on P1 and the bot seat on P2 --
    /// the same fixed seating singleplayer uses. THE TABLE IS DIRECTIONAL: row = what the
    /// human picked, column = what the bot should answer with, both anchored to their seats.
    /// Feeding it a transposed pair silently returns a wrong answer rather than failing,
    /// because every (row, column) pair is populated.
    ///
    /// WHAT IT IS NOT. The table is fitted from HeuristicBot-vs-HeuristicBot games, so it
    /// encodes which loadout beats which under HeuristicBot's play, not under Marc's. The
    /// absolute win rates in it will not transfer to a human opponent; only the ordering has
    /// any claim to. It is also fitted for the P2 seat specifically, and the engine's seat
    /// asymmetry is large enough that the P1 answer could differ entirely.
    ///
    /// Degrades to a random loadout if the table is missing or unreadable. This runs in the
    /// live web game's join path, where a hard failure would take singleplayer down
    /// altogether; a silently-random opponent is merely the old behaviour.
    /// </summary>
    public static class CounterPicker
    {
        /// <summary>
        /// How many of the top-ranked answers to sample from. 1 = always play the single
        /// best counter, which maximises measured win rate but is fully predictable: the
        /// human picks first and can learn the table, then steer toward loadouts whose best
        /// counter is weakest and prepare for that exact opponent. Raising this trades a
        /// little win rate for unpredictability.
        /// </summary>
        public static int TopK { get; set; } = 1;

        /// <summary>Set false to restore the pre-counter-pick uniform random roll.</summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>
        /// When set, the bot ALWAYS plays this loadout and both the counter table and the
        /// random roll are bypassed entirely. Format "Team,offense,defense", e.g.
        /// "White,nuke,reinforcements"; the signature gadget follows from the team.
        ///
        /// This exists to make MIRROR MATCHES possible on demand. Counter-picking makes the
        /// bot's loadout a function of the human's, which is exactly what you do not want
        /// when the question is "how does my play compare to the bot's" -- any difference in
        /// outcome then confounds play with loadout. Pinning both sides to the same loadout
        /// removes that variable, at the cost of the bot no longer answering what it faces.
        ///
        /// NOT a strength setting. Pinning the bot to one loadout reopens the deterministic
        /// holes counter-picking closed: a human who picks the loadout that hard-counters
        /// this one wins every game. Leave it unset for normal play.
        /// </summary>
        public static string ForcedLoadout { get; set; }

        public readonly struct Pick
        {
            public readonly TeamColour Team;
            public readonly string Offense;
            public readonly string Defense;
            public readonly double EstimatedWinRate;

            public Pick(TeamColour team, string offense, string defense, double est)
            {
                Team = team; Offense = offense; Defense = defense; EstimatedWinRate = est;
            }

            public string[] Loadout => new[]
            {
                Offense, Defense, GameDataManager.GetSignatureGadgetIdForTeam(Team)
            };
        }

        private static readonly object _loadLock = new object();
        private static Dictionary<string, List<Pick>> _table;
        private static bool _loadAttempted;

        private static string Key(TeamColour team, string offense, string defense)
            => team + "|" + offense + "|" + defense;

        /// <summary>
        /// Strips an upgrade suffix ("nuke_2" -> "nuke"). The table is keyed on the four base
        /// offensive and four base defensive gadgets; an upgraded id would miss the lookup and
        /// fall through to a random pick without any error.
        /// </summary>
        public static string BaseGadgetId(string id)
        {
            if (string.IsNullOrEmpty(id)) return id;
            int i = id.IndexOf('_');
            return i < 0 ? id : id.Substring(0, i);
        }

        private static void EnsureLoaded()
        {
            if (_loadAttempted) return;
            lock (_loadLock)
            {
                if (_loadAttempted) return;
                _loadAttempted = true;

                string path = Path.Combine(AppContext.BaseDirectory, "Data", "counter_table.csv");
                if (!File.Exists(path))
                {
                    Console.WriteLine($"[counter-pick] no table at {path} -- falling back to random loadouts. " +
                                      "Generate one with: CastleDefense.BotArena.exe counter-matrix");
                    return;
                }

                try
                {
                    var t = new Dictionary<string, List<Pick>>();
                    foreach (var line in File.ReadLines(path))
                    {
                        if (line.Length == 0 || line[0] == '#' || line.StartsWith("human_team")) continue;
                        var f = line.Split(',');
                        if (f.Length < 8) continue;
                        if (!Enum.TryParse<TeamColour>(f[0], true, out var hTeam)) continue;
                        if (!Enum.TryParse<TeamColour>(f[4], true, out var bTeam)) continue;

                        string k = Key(hTeam, f[1], f[2]);
                        if (!t.TryGetValue(k, out var list)) t[k] = list = new List<Pick>();
                        list.Add(new Pick(bTeam, f[5], f[6],
                                          double.Parse(f[7], CultureInfo.InvariantCulture)));
                    }

                    // The file is written in rank order, but sort defensively so a
                    // hand-edited table cannot quietly demote the best answer to slot 2.
                    foreach (var list in t.Values)
                        list.Sort((x, y) => y.EstimatedWinRate.CompareTo(x.EstimatedWinRate));

                    _table = t;
                    Console.WriteLine($"[counter-pick] loaded {t.Count} human loadouts from {path}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[counter-pick] failed to read {path}: {ex.Message} -- using random loadouts.");
                }
            }
        }

        /// <summary>
        /// The bot's answer to the human's loadout, or a uniform random loadout when the
        /// table is missing, disabled, or has no row for this combination.
        /// </summary>
        public static Pick PickCounter(TeamColour humanTeam, string humanOffense, string humanDefense, Random rng = null)
        {
            rng ??= Random.Shared;

            // Checked before EnsureLoaded so a forced loadout works with no table present.
            if (TryParseForced(out var forced)) return forced;

            EnsureLoaded();

            if (Enabled && _table != null &&
                _table.TryGetValue(Key(humanTeam, humanOffense, humanDefense), out var ranked) &&
                ranked.Count > 0)
            {
                int k = Math.Max(1, Math.Min(TopK, ranked.Count));
                return ranked[rng.Next(k)];
            }

            return new Pick(GameDataManager.GetRandomTeam(),
                            GameDataManager.GetRandomOGadgetId(),
                            GameDataManager.GetRandomDGadgetId(), double.NaN);
        }

        private static string _forcedParsedFrom;
        private static Pick? _forcedCache;

        /// <summary>
        /// Parses <see cref="ForcedLoadout"/>, caching the result so a malformed value is
        /// reported once rather than on every game. A bad value is ignored rather than
        /// thrown: this runs in the live join path.
        /// </summary>
        private static bool TryParseForced(out Pick pick)
        {
            pick = default;
            string spec = ForcedLoadout;
            if (string.IsNullOrWhiteSpace(spec)) return false;

            lock (_loadLock)
            {
                if (_forcedParsedFrom != spec)
                {
                    _forcedParsedFrom = spec;
                    _forcedCache = null;

                    var f = spec.Split(',');
                    if (f.Length != 3)
                        Console.WriteLine($"[counter-pick] ForcedLoadout '{spec}' is not 'Team,offense,defense' -- ignoring.");
                    else if (!Enum.TryParse<TeamColour>(f[0].Trim(), true, out var team))
                        Console.WriteLine($"[counter-pick] ForcedLoadout '{spec}' has unknown team '{f[0]}' -- ignoring.");
                    else
                    {
                        string off = BaseGadgetId(f[1].Trim());
                        string def = BaseGadgetId(f[2].Trim());
                        // Validate against the real gadget table rather than trusting the
                        // string: SetLoadout silently stores null for an id that matches
                        // nothing, which surfaces much later as a bot that never casts.
                        var offDef = GameDataManager.Gadgets.Find(g => g.Id == off);
                        var defDef = GameDataManager.Gadgets.Find(g => g.Id == def);
                        if (offDef == null || offDef.Slot != GadgetSlot.Offense)
                            Console.WriteLine($"[counter-pick] ForcedLoadout offense '{off}' is not an offensive gadget -- ignoring.");
                        else if (defDef == null || defDef.Slot != GadgetSlot.Defense)
                            Console.WriteLine($"[counter-pick] ForcedLoadout defense '{def}' is not a defensive gadget -- ignoring.");
                        else
                        {
                            _forcedCache = new Pick(team, off, def, double.NaN);
                            Console.WriteLine($"[counter-pick] FORCED loadout active: {team}/{off}/{def} " +
                                              "-- counter-picking is bypassed for every singleplayer game.");
                        }
                    }
                }

                if (_forcedCache.HasValue) { pick = _forcedCache.Value; return true; }
                return false;
            }
        }

        /// <summary>Diagnostics: the full ranked answer list for one human loadout.</summary>
        public static IReadOnlyList<Pick> Ranked(TeamColour humanTeam, string humanOffense, string humanDefense)
        {
            EnsureLoaded();
            if (_table != null && _table.TryGetValue(Key(humanTeam, humanOffense, humanDefense), out var r))
                return r;
            return Array.Empty<Pick>();
        }
    }
}
