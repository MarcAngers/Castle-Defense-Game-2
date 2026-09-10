using System.Net;
using CastleDefense.Api.Data;
using CastleDefense.Api.Services.Analysis;
using CastleDefense.Engine.Bot;
using CastleDefense.Engine.Replays;
using Microsoft.AspNetCore.Mvc;

namespace CastleDefense.Api.Controllers;

/// <summary>
/// Backend for the /analysis pages: the replay list, the per-game report, and the
/// dashboard-update button.
///
/// REPORTS ARE GENERATED ON DEMAND AND NOTHING IS STORED. A report is a full re-simulation
/// of the game -- a few hundred milliseconds for a five-minute match -- and caching it would
/// mean caching something that goes stale the moment the balance CSVs change, which is
/// exactly the failure this whole feature exists to surface. Marc's call: slow page loads
/// are fine, a stale report is not.
/// </summary>
[ApiController]
[Route("api/analysis")]
public class AnalysisController : ControllerBase
{
    private readonly ReplayIndex _index;
    private readonly AnalysisPaths _paths;
    private readonly DashboardRunner _runner;
    private readonly GameDatabase _db;

    public AnalysisController(ReplayIndex index, AnalysisPaths paths, DashboardRunner runner, GameDatabase db)
    {
        _index = index; _paths = paths; _runner = runner; _db = db;
    }

    [HttpGet("replays")]
    public IActionResult Replays() => Ok(new
    {
        recordingsRoot = _paths.RecordingsRoot,
        redirected = !_paths.RecordingsRoot.EndsWith("recordings", StringComparison.OrdinalIgnoreCase),
        rows = _index.List()
    });

    /// <summary>
    /// The full report for one game. Everything expensive happens here.
    /// </summary>
    [HttpGet("replay/{gameId}")]
    public IActionResult Replay(string gameId)
    {
        var path = _paths.ReplayPath(gameId);
        if (path == null) return NotFound(new { error = $"No replay found for {gameId}." });

        ReplayFile rf;
        try { rf = ReplayFile.Read(path); }
        catch (Exception ex) { return BadRequest(new { error = "Could not read the replay: " + ex.Message }); }

        _db.GetAnalysisRows().TryGetValue(gameId, out var row);

        // THE BOT'S PROFILE IS READ FROM THE RECORDED opponent_type, not assumed. Those
        // strings are written by GameHostingService precisely so a recorded game can be told
        // apart later; picking the wrong profile would re-derive the wrong thresholds and
        // the rationale would quietly describe a bot that never played.
        var (botSide, settings) = BotFor(row?.GameMode, row?.OpponentType);

        var res = ReplayAnalysis.Run(rf, new ReplayAnalysis.Options
        {
            BotSide = botSide,
            BotSettings = settings
        });

        return Ok(new
        {
            id = res.GameId,
            formatVersion = res.FormatVersion,
            map = res.Map.ToString(),
            shadowMap = res.ShadowMap,
            mode = row?.GameMode,
            opponentType = row?.OpponentType,
            endReason = row?.EndReason,
            playedAt = row?.PlayedAt,
            botSide,
            botProfile = ProfileName(row?.OpponentType),
            teams = new
            {
                p1 = new { team = rf.P1Team, start = New(rf.P1StartOff, rf.P1StartDef, rf.P1StartSig), end = New(rf.P1Off, rf.P1Def, rf.P1Sig) },
                p2 = new { team = rf.P2Team, start = New(rf.P2StartOff, rf.P2StartDef, rf.P2StartSig), end = New(rf.P2Off, rf.P2Def, rf.P2Sig) }
            },
            recorded = new
            {
                winner = res.RecordedWinner,
                ticks = res.RecordedTicks,
                p1Money = row?.P1FinalMoney, p2Money = row?.P2FinalMoney,
                p1Income = row?.P1FinalIncome, p2Income = row?.P2FinalIncome,
                p1HpPct = row?.P1FinalHpPct, p2HpPct = row?.P2FinalHpPct
            },
            rebuilt = new
            {
                winner = res.RebuiltWinner,
                ticks = res.RebuiltTicks,
                p1Money = res.FinalMoney[1], p2Money = res.FinalMoney[2],
                p1Income = res.FinalIncome[1], p2Income = res.FinalIncome[2]
            },
            // DOES THE REBUILD ACTUALLY LAND WHERE THE REAL GAME DID?
            //
            // The failed-action count is the SHARP test but it is only as strong as the
            // number of actions there were to fail: game 12B538 has seven actions in the
            // whole match, replays all seven, and still ends 161 ticks late on different
            // money. Reporting that as "exact" would be a lie told confidently. So the
            // banner also compares against the row the recorder captured live at game over,
            // which is ground truth and is not a resimulation of anything.
            groundTruth = row?.P1FinalMoney == null ? null : new
            {
                ticksMatch = res.RebuiltTicks == res.RecordedTicks,
                winnerMatch = res.RebuiltWinner == res.RecordedWinner,
                moneyMatch = Near(res.FinalMoney[1], row.P1FinalMoney) && Near(res.FinalMoney[2], row.P2FinalMoney),
                exact = res.RebuiltTicks == res.RecordedTicks
                        && res.RebuiltWinner == res.RecordedWinner
                        && Near(res.FinalMoney[1], row.P1FinalMoney)
                        && Near(res.FinalMoney[2], row.P2FinalMoney)
            },
            fidelity = new
            {
                perfect = res.Fidelity.Perfect,
                p1Attempted = res.Fidelity.Attempted[1], p1Failed = res.Fidelity.Failed[1],
                p2Attempted = res.Fidelity.Attempted[2], p2Failed = res.Fidelity.Failed[2],
                firstFailureTick = res.Fidelity.FirstFailureTick,
                firstFailureSide = res.Fidelity.FirstFailureSide,
                firstFailureAction = res.Fidelity.FirstFailureAction,
                trustedUntilSecond = res.Fidelity.TrustedUntilSecond
            },
            // RECORDED beats INFERRED, and the page is told which it is looking at. A v4
            // replay wrote its own fingerprint down; anything older carries whatever
            // `--backfill-balance` recovered from git history, which is a derivation and is
            // labelled as one. Never let the two look alike.
            balance = new
            {
                status = res.Balance != ReplayAnalysis.BalanceStatus.Unknown
                         ? res.Balance.ToString()
                         : InferredStatus(row, res.CurrentGadgetsHash, res.CurrentRosterHash),
                source = res.Balance != ReplayAnalysis.BalanceStatus.Unknown
                         ? "recorded" : row?.BalanceInferenceMethod,
                recordedGadgets = res.RecordedGadgetsHash,
                recordedRoster = res.RecordedRosterHash,
                inferredGadgets = row?.InferredGadgetsHash,
                inferredRoster = row?.InferredRosterHash,
                inferenceDetail = row?.BalanceInferenceDetail,
                currentGadgets = res.CurrentGadgetsHash,
                currentRoster = res.CurrentRosterHash
            },
            armageddon = new
            {
                p1 = res.Armageddon[1], p2 = res.Armageddon[2],
                p1Second = res.ArmageddonSecond[1], p2Second = res.ArmageddonSecond[2]
            },
            upgrades = res.Upgrades.Select(u => new { second = u.second, side = u.side, gadget = u.gadgetId }),
            samples = res.Samples.Select(s => new
            {
                s.Second,
                p1Money = s.Money[1], p1Income = s.Income[1], p1Invest = s.Invest[1], p1Hp = s.HpPct[1],
                p1Units = s.UnitSpend[1], p1Gadgets = s.GadgetSpend[1], p1Repairs = s.RepairSpend[1],
                p1Alive = s.UnitsAlive[1], p1Auto = s.AutoSpawnLevel[1],
                p2Money = s.Money[2], p2Income = s.Income[2], p2Invest = s.Invest[2], p2Hp = s.HpPct[2],
                p2Units = s.UnitSpend[2], p2Gadgets = s.GadgetSpend[2], p2Repairs = s.RepairSpend[2],
                p2Alive = s.UnitsAlive[2], p2Auto = s.AutoSpawnLevel[2]
            }),
            highlights = res.Highlights.Select(h => new
            {
                h.Second, h.Leader, h.PreviousLeader, h.Basis, h.Headline,
                p1Money = h.Money[1], p2Money = h.Money[2],
                p1Income = h.Income[1], p2Income = h.Income[2],
                p1Invest = h.Invest[1], p2Invest = h.Invest[2],
                p1Actions = h.P1Actions, p2Actions = h.P2Actions,
                bot = h.BotThinking == null ? null : new
                {
                    h.BotThinking.Summary,
                    h.BotThinking.Did,
                    h.BotThinking.Note,
                    gates = h.BotThinking.Gates.Select(g => new { g.Name, g.Open, g.Detail, g.Source })
                }
            })
        });
    }

    /// <summary>
    /// Serves the balance dashboard from CastleDefense.BotArena/dashboard/, which is outside
    /// wwwroot on purpose -- putting it under wwwroot would publish the whole sweep folder,
    /// CSVs included, and the arena writes there while the server is running.
    /// </summary>
    [HttpGet("/analysis/dashboard")]
    public IActionResult Dashboard()
    {
        if (_paths.DashboardHtml == null || !System.IO.File.Exists(_paths.DashboardHtml))
            return NotFound("dashboard.html has not been generated yet — run an update first.");
        // No-cache so the page shows the sweep that just finished rather than the one before.
        Response.Headers.CacheControl = "no-cache, no-store";
        return PhysicalFile(_paths.DashboardHtml, "text/html");
    }

    [HttpGet("dashboard/status")]
    public IActionResult DashboardStatus()
    {
        var s = _runner.Current();
        string generated = null;
        if (_paths.DashboardHtml != null && System.IO.File.Exists(_paths.DashboardHtml))
            generated = System.IO.File.GetLastWriteTime(_paths.DashboardHtml).ToString("yyyy-MM-dd HH:mm:ss");
        return Ok(new { s.Running, s.Available, s.StartedAt, s.FinishedAt, s.ExitCode, s.ElapsedSeconds, s.Log, generated, allowed = IsLocal() });
    }

    public sealed class RunRequest { public string Args { get; set; } }

    [HttpPost("dashboard/run")]
    public IActionResult DashboardRun([FromBody] RunRequest req)
    {
        // See DashboardRunner's class comment. The endpoint IS the capability.
        if (!IsLocal())
            return StatusCode(403, new { error = "The dashboard update can only be started from this machine (localhost)." });

        var refused = _runner.Start(req?.Args);
        if (refused != null) return Conflict(new { error = refused });
        return Ok(new { started = true });
    }

    private bool IsLocal()
    {
        var ip = HttpContext.Connection.RemoteIpAddress;
        return ip != null && IPAddress.IsLoopback(ip);
    }

    private static object New(string off, string def, string sig) => new { off, def, sig };

    /// <summary>Same comparison ReplayAnalysis makes, but against the backfilled fingerprint
    /// for replays that predate v4 and so recorded none of their own.</summary>
    private static string InferredStatus(GameDatabase.AnalysisRow row, string curG, string curR)
    {
        if (string.IsNullOrEmpty(row?.InferredGadgetsHash)) return "Unknown";
        bool g = row.InferredGadgetsHash != curG;
        bool r = !string.IsNullOrEmpty(row.InferredRosterHash) && row.InferredRosterHash != curR;
        return !g && !r ? "Match" : g && r ? "BothChanged" : g ? "GadgetsChanged" : "RosterChanged";
    }

    /// <summary>Money is a double that accumulates fractional income all game, so compare
    /// to the dollar rather than for equality.</summary>
    private static bool Near(double rebuilt, double? recorded)
        => recorded.HasValue && Math.Abs(rebuilt - recorded.Value) < 1.0;

    private static string ProfileName(string opponentType) => opponentType switch
    {
        "heuristic_autospawn8" => "EconomyBrakeAutoSpawnProfile",
        "heuristic_brake" => "EconomyBrakeProfile",
        "heuristic_repair_hazard" => "RepairFixPlusHazardProfile",
        "heuristic_repairfix" => "RepairFixProfile",
        "heuristic" => "Default (flagship)",
        _ => null
    };

    /// <summary>
    /// Which seat the HeuristicBot occupied, and under which settings profile. Returns
    /// (0, null) for anything that was not a HeuristicBot game -- multiplayer, an ONNX
    /// league opponent, the search bot -- so no rationale is produced rather than one
    /// derived from the wrong bot's thresholds.
    /// </summary>
    private static (int side, HeuristicBotSettings settings) BotFor(string mode, string opponentType)
    {
        if (mode is not ("sp" or "practice" or "accept")) return (0, null);
        return opponentType switch
        {
            "heuristic_autospawn8" => (2, HeuristicBotSettings.EconomyBrakeAutoSpawnProfile),
            "heuristic_brake" => (2, HeuristicBotSettings.EconomyBrakeProfile),
            "heuristic_repair_hazard" => (2, HeuristicBotSettings.RepairFixPlusHazardProfile),
            "heuristic_repairfix" => (2, HeuristicBotSettings.RepairFixProfile),
            "heuristic" => (2, new HeuristicBotSettings()),
            _ => (0, null)
        };
    }
}
