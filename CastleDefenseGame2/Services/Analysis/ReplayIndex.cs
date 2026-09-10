using CastleDefense.Api.Data;
using CastleDefense.Engine.Data;
using CastleDefense.Engine;
using CastleDefense.Engine.Models;
using CastleDefense.Engine.Replays;

namespace CastleDefense.Api.Services.Analysis;

/// <summary>
/// THE REPLAY LIST -- one row per recorded game, cheap enough to rebuild on every page load.
///
/// Each row is a JOIN of two sources with different standing, and the distinction matters:
///
///   * game_records.db carries the result, the mode, the opponent and the FINAL money /
///     income / castle HP, all captured live at game over with no resimulation.
///   * the .replay header carries the map, the START loadout and (from v4) whether either
///     side bought ARMAGEDDON plus the balance-data fingerprint.
///
/// NOTHING HERE RESIMULATES ANYTHING. Reading 300 headers is a few milliseconds; rebuilding
/// 300 games is minutes. The per-game report is where the expensive work happens.
///
/// CACHED BY FILE MTIME, so a newly finished game appears on the next load and everything
/// else is read once per server lifetime.
///
/// ── ON WINS BY DEFAULT ───────────────────────────────────────────────────────────────────
/// A game whose loser disconnected was not decided by play. This list SHOWS them, because
/// Marc asked to browse by category and "abandoned" is one of his categories -- but it
/// labels them, and it computes no aggregate win rate at all. The rule that they are
/// excluded from analysis lives in ReplayCorpus.IsRealResult and is not restated here;
/// anything that starts averaging over these rows has to go and read it.
/// </summary>
public sealed class ReplayIndex
{
    private readonly GameDatabase _db;
    private readonly AnalysisPaths _paths;
    private readonly Dictionary<string, (long stamp, ReplayHeader hdr)> _cache = new();
    private readonly object _lock = new();

    public ReplayIndex(GameDatabase db, AnalysisPaths paths) { _db = db; _paths = paths; }

    private sealed record ReplayHeader(string Id, byte Version, string Map, bool ShadowMap,
        string P1Team, string P1Off, string P1Def, string P1Sig,
        string P2Team, string P2Off, string P2Def, string P2Sig,
        byte Winner, long Ticks, bool P1Arma, bool P2Arma,
        string GadgetsHash, string RosterHash, bool NoP1Actions);

    public sealed record Row(
        string Id, string Folder, long PlayedAt, string Mode, string OpponentType,
        string Category, string EndReason,
        string P1Team, string P1Loadout, string P2Team, string P2Loadout,
        string Map, bool ShadowMap, int Winner, string Result, long DurationTicks, double DurationSeconds,
        string Armageddon, string BalanceStatus, string BalanceSource, byte FormatVersion,
        double? P1FinalMoney, double? P2FinalMoney, double? P1FinalIncome, double? P2FinalIncome,
        double? P1FinalHpPct, double? P2FinalHpPct);

    public List<Row> List()
    {
        var db = SafeRows();
        var rows = new List<Row>();

        foreach (var folder in _paths.ReplayFolders())
        {
            string folderName = Path.GetFileName(folder);
            foreach (var file in Directory.EnumerateFiles(folder, "*.replay"))
            {
                var hdr = HeaderFor(file);
                if (hdr == null) continue;
                db.TryGetValue(hdr.Id, out var r);

                string mode = r?.GameMode ?? "?";
                string end = r?.EndReason;
                int winner = r?.Winner ?? hdr.Winner;
                long ticks = r?.DurationTicks ?? hdr.Ticks;

                rows.Add(new Row(
                    Id: hdr.Id,
                    Folder: folderName,
                    PlayedAt: r?.PlayedAt ?? 0,
                    Mode: mode,
                    OpponentType: r?.OpponentType,
                    Category: Categorise(mode, end, hdr),
                    EndReason: end,
                    P1Team: hdr.P1Team,
                    P1Loadout: Loadout(hdr.P1Off, hdr.P1Def, hdr.P1Sig),
                    P2Team: hdr.P2Team,
                    P2Loadout: Loadout(hdr.P2Off, hdr.P2Def, hdr.P2Sig),
                    Map: hdr.Version >= 3 ? hdr.Map : null,
                    ShadowMap: hdr.ShadowMap,
                    Winner: winner,
                    Result: winner == 0 ? "draw" : $"P{winner}",
                    DurationTicks: ticks,
                    DurationSeconds: ticks / (double)GameEngine.TICKS_PER_SECOND,
                    Armageddon: hdr.Version >= 4
                        ? (hdr.P1Arma && hdr.P2Arma ? "both" : hdr.P1Arma ? "P1" : hdr.P2Arma ? "P2" : "no")
                        : null,
                    BalanceStatus: BalanceLabel(hdr, r),
                    BalanceSource: hdr.Version >= 4 && !string.IsNullOrEmpty(hdr.GadgetsHash)
                                   ? "recorded" : r?.BalanceInferenceMethod,
                    FormatVersion: hdr.Version,
                    P1FinalMoney: r?.P1FinalMoney, P2FinalMoney: r?.P2FinalMoney,
                    P1FinalIncome: r?.P1FinalIncome, P2FinalIncome: r?.P2FinalIncome,
                    P1FinalHpPct: r?.P1FinalHpPct, P2FinalHpPct: r?.P2FinalHpPct));
            }
        }

        return rows.OrderByDescending(r => r.PlayedAt).ThenBy(r => r.Id).ToList();
    }

    private Dictionary<string, GameDatabase.AnalysisRow> SafeRows()
    {
        // A missing or locked DB must degrade to "replay files only" rather than 500 the
        // page: the files are the durable artefact and are worth browsing on their own.
        try { return _db.GetAnalysisRows(); }
        catch { return new Dictionary<string, GameDatabase.AnalysisRow>(StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>
    /// The bucket a game belongs in. Ordering matters: a disconnected or abandoned game is
    /// filed as such WHATEVER its mode, because "was this decided by play" outranks "which
    /// screen was it played on" for every use this list has.
    /// </summary>
    private static string Categorise(string mode, string endReason, ReplayHeader hdr)
    {
        if (endReason == "abandoned") return "Abandoned";
        if (endReason == "disconnect") return "Disconnected";
        // A re-roll: seat 1 never acted at all. Marc re-rolls until he gets the matchup he
        // wants and the discarded attempts still record.
        if (hdr.NoP1Actions && mode is "sp" or "practice") return "Re-rolled";
        return mode switch
        {
            "sp" => "Singleplayer",
            "mp" => "Multiplayer",
            "practice" => "Practice",
            "accept" => "Acceptance test",
            "league" or "leaguewatch" => "Spectated",
            "watch" or "defwatch" => "Spectated",
            _ => "Other"
        };
    }

    private static string Loadout(string off, string def, string sig)
    {
        var parts = new[] { off, def, sig }.Where(s => !string.IsNullOrEmpty(s)).ToArray();
        return parts.Length == 0 ? "—" : string.Join(" / ", parts);
    }

    /// <summary>
    /// Whether the balance data this game was played under still matches what is loaded now.
    ///
    /// TWO SOURCES, AND THEY ARE NOT EQUALLY GOOD. A v4 replay RECORDED its fingerprint and is
    /// simply true. Everything older gets the value `--backfill-balance` worked out from git
    /// history, which is an inference -- the row's BalanceSource says which, so the UI can
    /// label them differently rather than presenting a derivation as a record.
    /// </summary>
    private static string BalanceLabel(ReplayHeader h, GameDatabase.AnalysisRow row)
    {
        string gHash = h.Version >= 4 && !string.IsNullOrEmpty(h.GadgetsHash)
                       ? h.GadgetsHash : row?.InferredGadgetsHash;
        string rHash = h.Version >= 4 && !string.IsNullOrEmpty(h.GadgetsHash)
                       ? h.RosterHash : row?.InferredRosterHash;
        if (string.IsNullOrEmpty(gHash)) return "unknown";
        bool g = gHash != GameDataManager.GadgetsFileHash;
        bool r = !string.IsNullOrEmpty(rHash) && rHash != GameDataManager.RosterFileHash;
        return !g && !r ? "match" : g && r ? "both changed" : g ? "gadgets changed" : "roster changed";
    }

    private ReplayHeader HeaderFor(string file)
    {
        long stamp;
        try { stamp = new FileInfo(file).LastWriteTimeUtc.Ticks; }
        catch { return null; }

        lock (_lock)
            if (_cache.TryGetValue(file, out var hit) && hit.stamp == stamp)
                return hit.hdr;

        ReplayHeader hdr;
        try
        {
            var rf = ReplayFile.Read(file);
            hdr = new ReplayHeader(rf.GameId, rf.Version,
                rf.HasV3 ? rf.Map.ToString() : null, rf.ShadowMap,
                rf.P1Team, rf.HasV3 ? rf.P1StartOff : rf.P1Off,
                rf.HasV3 ? rf.P1StartDef : rf.P1Def, rf.HasV3 ? rf.P1StartSig : rf.P1Sig,
                rf.P2Team, rf.HasV3 ? rf.P2StartOff : rf.P2Off,
                rf.HasV3 ? rf.P2StartDef : rf.P2Def, rf.HasV3 ? rf.P2StartSig : rf.P2Sig,
                rf.Winner, rf.TickCount, rf.P1Armageddon, rf.P2Armageddon,
                rf.GadgetsHash, rf.RosterHash, rf.IsAbandoned);
        }
        catch
        {
            return null;   // a truncated or half-written file is skipped, not fatal
        }

        lock (_lock) _cache[file] = (stamp, hdr);
        return hdr;
    }
}
