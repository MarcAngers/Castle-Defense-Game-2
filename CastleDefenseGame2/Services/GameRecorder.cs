using CastleDefense.Api.Data;
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
/// </summary>
public class GameRecorder
{
    private readonly string _gameId;
    private readonly long _startedAt;
    private readonly long _startingTick;
    private readonly double _p1StartMoney;
    private readonly double _p2StartMoney;
    private readonly List<(byte p1, byte p2)> _ticks = new();
    private readonly List<(int tick, int player, string gadgetId)> _gadgetUses = new();

    public GameRecorder(string gameId, long startingTick = 0, double p1StartMoney = 0, double p2StartMoney = 0)
    {
        _gameId = gameId;
        _startedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _startingTick = startingTick;
        _p1StartMoney = p1StartMoney;
        _p2StartMoney = p2StartMoney;
    }

    public void RecordTick(int p1Action, int p2Action)
        => _ticks.Add(((byte)p1Action, (byte)p2Action));

    public void RecordGadgetUse(int tick, int player, string gadgetId)
        => _gadgetUses.Add((tick, player, gadgetId));

    public void Save(string replayDir, PlayerState p1, PlayerState p2,
        int winner, long durationTicks, string gameVersion, GameDatabase db,
        string gameMode = null, string opponentType = null)
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
                p1.Income, p2.Income, p1.Money, p2.Money, p1HpPct, p2HpPct);
            db.InsertGadgetUses(_gameId, _gadgetUses);
            Console.WriteLine($"[Recorder] Saved game {_gameId}: {_ticks.Count} ticks, winner={winner}, mode={gameMode}, opponent={opponentType}");
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
        w.Write((byte)2);
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
    }

    private static void WriteStr(BinaryWriter w, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        w.Write((byte)bytes.Length);
        w.Write(bytes);
    }
}
