using System.Linq;
﻿using CastleDefense.Api.Data;
using CastleDefense.Engine.Models;
using System.Text;

namespace CastleDefense.Api.Services;

/// <summary>
/// Accumulates per-tick action pairs and gadget use events for a single game,
/// then writes a compact binary .replay file and inserts a row into the DB.
///
/// Replay file format (CDRP v2):
///   [4]  magic "CDRP"
///   [1]  format version = 2
///   [6]  game ID (ASCII, always 6 uppercase hex chars)
///   [8]  unix timestamp (int64 LE)
///   [1+N] game_version (uint8 length + UTF8 bytes)
///   [1+N] p1_team, p1_off_gadget, p1_def_gadget, p1_sig_gadget (each length-prefixed)
///   [1+N] p2_team, p2_off_gadget, p2_def_gadget, p2_sig_gadget
///   [1]  winner (0/1/2)
///   [8]  starting_tick (int64 LE) — CurrentTick at game start; 30*30*timeSkip for league games
///   [8]  p1_start_money (float64 LE) — Player 1 money at game start (captures time-machine + bonus)
///   [8]  p2_start_money (float64 LE) — Player 2 money at game start
///   [4]  tick count (uint32 LE)
///   [N*2] action pairs: (p1_action byte, p2_action byte) per tick
///
/// v1 legacy format is identical but omits the three fields above tick count.
///
/// ── CDRP v3 (2026-08-20) ────────────────────────────────────────────────────────
/// v3 appends the four things a v2 replay could NOT reconstruct. Everything through the
/// v2 tick payload is byte-identical, so a v3 reader handles v2 by skipping the tail and
/// a v2 file stays readable forever.
///
///   [1]  map (TeamColour byte)
///   [1]  shadow_map (0/1)
///   [4]  engine_seed (int32 LE)
///   [1+N] p1_start_off, p1_start_def, p1_start_sig  (each length-prefixed)
///   [1+N] p2_start_off, p2_start_def, p2_start_sig
///   [4]  gadget cast count (uint32 LE)
///   [N*7] casts: (tick int32 LE, side byte, position int16 LE)
///
/// WHY EACH ONE EXISTS -- all four were measured as real reconstruction gaps, not guessed:
///
///  * MAP. CreateGame used `new GameState()`, which rolls a random map and coin-flips Black
///    into a shadow map. The map is gameplay-affecting and v2 stored nothing about it, so a
///    rebuild rolled a DIFFERENT one.
///  * ENGINE SEED. CreateGame used `new GameEngine(state)` with seed null, i.e. an UNSEEDED
///    Random. That stream drives unit y-position on spawn, which changes combat targeting.
///    A live game was therefore not reproducible even in principle. CreateGame now draws an
///    explicit seed and records it.
///  * START LOADOUT. v2 wrote the loadout from the live PlayerState at GAME OVER, so any
///    gadget that upgraded mid-game was recorded at its FINAL tier and a rebuild equipped
///    that tier from tick 0. For FC1462 that meant starting on a $4000 nuke_3 in a game
///    that began on a $20 nuke: every cast failed the money check and the economy diverged
///    immediately. The END loadout is still written (it is what the DB row uses, and the
///    diff against the start tells you what upgraded); the START is what BuildStart needs.
///  * GADGET TARGETS. v2 stored only the discrete action id, so every reconstruction
///    re-aimed casts with the engine auto-targeter. Marc's gadget doctrine -- freeze and
///    blackhole at the enemy's end -- was invisible to every tool that read replays.
///
/// COST, measured over the 189 recordings in recordings/singleplayer before this change
/// (mean 13.0 KB, 34.6 casts/game): the sparse cast list adds ~242 B and the fixed header
/// fields ~40 B, so about +2.2% per replay. A dense per-tick encoding would have been
/// +198%, which is why the cast list is sparse. There is no storage case for making this
/// optional, and an opt-in flag would guarantee the interesting game is the one recorded
/// without it.
/// </summary>
public class GameRecorder
{
    private readonly string _gameId;
    private readonly long _startedAt;
    private readonly long _startingTick;
    private readonly double _p1StartMoney;
    private readonly double _p2StartMoney;
    private readonly List<(byte p1, byte p2)> _ticks = new();
    private readonly List<(int tick, int player, string gadgetId, int position)> _gadgetUses = new();

    // v3 reconstruction fields, all captured at game START rather than at save time.
    private readonly byte _map;
    private readonly bool _shadowMap;
    private readonly int _engineSeed;
    private readonly string[] _p1StartLoadout;
    private readonly string[] _p2StartLoadout;

    public GameRecorder(string gameId, long startingTick = 0, double p1StartMoney = 0, double p2StartMoney = 0,
                        byte map = 0, bool shadowMap = false, int engineSeed = 0,
                        string[] p1StartLoadout = null, string[] p2StartLoadout = null)
    {
        _gameId = gameId;
        _startedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _startingTick = startingTick;
        _p1StartMoney = p1StartMoney;
        _p2StartMoney = p2StartMoney;
        _map = map;
        _shadowMap = shadowMap;
        _engineSeed = engineSeed;
        _p1StartLoadout = p1StartLoadout ?? new[] { "", "", "" };
        _p2StartLoadout = p2StartLoadout ?? new[] { "", "", "" };
    }

    public void RecordTick(int p1Action, int p2Action)
        => _ticks.Add(((byte)p1Action, (byte)p2Action));

    /// <summary>
    /// position is the RESOLVED target the engine actually used -- OnGadgetAnimation fires
    /// from inside UseGadget after the -1 auto-target branch has run, so a bot cast records
    /// where the blast really landed, not the sentinel.
    /// </summary>
    public void RecordGadgetUse(int tick, int player, string gadgetId, int position = 0)
        => _gadgetUses.Add((tick, player, gadgetId, position));

    /// <param name="endReason">
    /// Null or "normal" for a game that was actually played out. "disconnect" means the
    /// winner was awarded the game because the loser's browser never came back inside the
    /// 60-second grace window, and "abandoned" means nobody was left to award it to. The
    /// replay itself cannot express this -- its winner byte looks exactly like an earned
    /// one -- so the DB column is the only thing standing between a default win and every
    /// win-rate number computed from this corpus. See GameDatabase's schema comment.
    /// </param>
    public void Save(string replayDir, PlayerState p1, PlayerState p2,
        int winner, long durationTicks, string gameVersion, GameDatabase db,
        string gameMode = null, string opponentType = null, string endReason = null)
    {
        try
        {
            WriteReplay(replayDir, p1, p2, winner, gameVersion);
            // Captured directly from the true live PlayerState at game-over -- no
            // resimulation involved, so unlike a replayed reconstruction this is exact.
            // See GameDatabase's schema comment for why this was added.
            double? p1HpPct = p1.CastleMaxHealth > 0 ? 100.0 * p1.CastleHealth / p1.CastleMaxHealth : (double?)null;
            double? p2HpPct = p2.CastleMaxHealth > 0 ? 100.0 * p2.CastleHealth / p2.CastleMaxHealth : (double?)null;
            db.InsertGame(
                _gameId, gameVersion, _startedAt,
                p1.Team.ToString(), p1.OffensiveGadget?.Id, p1.DefensiveGadget?.Id, p1.SignatureGadget?.Id,
                p2.Team.ToString(), p2.OffensiveGadget?.Id, p2.DefensiveGadget?.Id, p2.SignatureGadget?.Id,
                winner, durationTicks, gameMode, opponentType,
                p1.Income, p2.Income, p1.Money, p2.Money, p1HpPct, p2HpPct, endReason);
            // The DB schema has no position column; the replay carries targets from v3 onward.
            db.InsertGadgetUses(_gameId, _gadgetUses.Select(g => (g.tick, g.player, g.gadgetId)).ToList());
            Console.WriteLine($"[Recorder] Saved game {_gameId}: {_ticks.Count} ticks, winner={winner}, mode={gameMode}, opponent={opponentType}"
                            + (string.IsNullOrEmpty(endReason) ? "" : $", end={endReason} (NOT a real result)"));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Recorder] Failed to save game {_gameId}: {ex.Message}");
        }
    }

    private void WriteReplay(string replayDir, PlayerState p1, PlayerState p2, int winner, string gameVersion)
    {
        Directory.CreateDirectory(replayDir);
        string path = Path.Combine(replayDir, _gameId + ".replay");

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);

        w.Write((byte)'C'); w.Write((byte)'D'); w.Write((byte)'R'); w.Write((byte)'P');
        w.Write((byte)3);
        w.Write(Encoding.ASCII.GetBytes(_gameId)); // always 6 bytes
        w.Write(_startedAt);

        WriteStr(w, gameVersion);
        WriteStr(w, p1.Team.ToString());
        WriteStr(w, p1.OffensiveGadget?.Id ?? "");
        WriteStr(w, p1.DefensiveGadget?.Id ?? "");
        WriteStr(w, p1.SignatureGadget?.Id ?? "");
        WriteStr(w, p2.Team.ToString());
        WriteStr(w, p2.OffensiveGadget?.Id ?? "");
        WriteStr(w, p2.DefensiveGadget?.Id ?? "");
        WriteStr(w, p2.SignatureGadget?.Id ?? "");

        w.Write((byte)winner);
        w.Write(_startingTick);   // v2: starting tick (int64)
        w.Write(_p1StartMoney);   // v2: player 1 money at game start (float64)
        w.Write(_p2StartMoney);   // v2: player 2 money at game start (float64)
        w.Write((uint)_ticks.Count);
        foreach (var (p1a, p2a) in _ticks)
        {
            w.Write(p1a);
            w.Write(p2a);
        }

        // ── v3 tail ─────────────────────────────────────────────────────────────
        w.Write(_map);
        w.Write((byte)(_shadowMap ? 1 : 0));
        w.Write(_engineSeed);
        for (int i = 0; i < 3; i++) WriteStr(w, _p1StartLoadout[i] ?? "");
        for (int i = 0; i < 3; i++) WriteStr(w, _p2StartLoadout[i] ?? "");
        w.Write((uint)_gadgetUses.Count);
        foreach (var (tick, player, _, position) in _gadgetUses)
        {
            w.Write(tick);
            w.Write((byte)player);
            // Positions are clamped to [300, MAP_WIDTH-300] by UseGadget and the map is
            // 2000 wide, so short is always sufficient.
            w.Write((short)position);
        }
    }

    private static void WriteStr(BinaryWriter w, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        w.Write((byte)bytes.Length);
        w.Write(bytes);
    }
}
