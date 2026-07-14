using Microsoft.Data.Sqlite;

namespace CastleDefense.Api.Data;

public class GameDatabase
{
    private readonly string _connectionString;

    public GameDatabase(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = $"Data Source={dbPath}";
        Initialize();
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var fk = conn.CreateCommand();
        fk.CommandText = "PRAGMA foreign_keys = ON";
        fk.ExecuteNonQuery();
        return conn;
    }

    private void Initialize()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS games (
                id               TEXT    PRIMARY KEY,
                game_version     TEXT    NOT NULL,
                played_at        INTEGER NOT NULL,
                p1_team          TEXT    NOT NULL,
                p2_team          TEXT    NOT NULL,
                p1_gadget_off    TEXT,
                p1_gadget_def    TEXT,
                p1_gadget_sig    TEXT,
                p2_gadget_off    TEXT,
                p2_gadget_def    TEXT,
                p2_gadget_sig    TEXT,
                winner           INTEGER NOT NULL,
                duration_ticks   INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS gadget_uses (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                game_id   TEXT    NOT NULL REFERENCES games(id) ON DELETE CASCADE,
                tick      INTEGER NOT NULL,
                player    INTEGER NOT NULL,
                gadget_id TEXT    NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();

        // Added after the fact -- older DB files won't have these. SQLite has no
        // "ADD COLUMN IF NOT EXISTS", so probe pragma table_info first. Without this,
        // there was no way to tell what a recorded league/practice game's opponent
        // actually was (spam tier vs which ONNX model vs random dummy) -- that
        // selection only ever lived in an in-memory dictionary during the game and
        // was discarded at game-end, making old recordings impossible to mine for
        // "did the human already beat this specific hard matchup" (see
        // [[project_ai_opponent_heuristic]] for the session this was found dead-ended).
        AddColumnIfMissing(conn, "games", "game_mode", "TEXT");
        AddColumnIfMissing(conn, "games", "opponent_type", "TEXT");
    }

    private static void AddColumnIfMissing(SqliteConnection conn, string table, string column, string type)
    {
        using (var check = conn.CreateCommand())
        {
            check.CommandText = $"PRAGMA table_info({table})";
            using var reader = check.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    return; // already present
            }
        }
        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
        alter.ExecuteNonQuery();
    }

    public void InsertGame(string id, string gameVersion, long playedAt,
        string p1Team, string p1Off, string p1Def, string p1Sig,
        string p2Team, string p2Off, string p2Def, string p2Sig,
        int winner, long durationTicks, string gameMode = null, string opponentType = null)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO games (id, game_version, played_at,
                p1_team, p1_gadget_off, p1_gadget_def, p1_gadget_sig,
                p2_team, p2_gadget_off, p2_gadget_def, p2_gadget_sig,
                winner, duration_ticks, game_mode, opponent_type)
            VALUES ($id, $ver, $at,
                $p1t, $p1o, $p1d, $p1s,
                $p2t, $p2o, $p2d, $p2s,
                $win, $dur, $mode, $opp)
            """;
        cmd.Parameters.AddWithValue("$id",  id);
        cmd.Parameters.AddWithValue("$ver", gameVersion);
        cmd.Parameters.AddWithValue("$at",  playedAt);
        cmd.Parameters.AddWithValue("$p1t", p1Team);
        cmd.Parameters.AddWithValue("$p1o", (object)p1Off ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$p1d", (object)p1Def ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$p1s", (object)p1Sig ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$p2t", p2Team);
        cmd.Parameters.AddWithValue("$p2o", (object)p2Off ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$p2d", (object)p2Def ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$p2s", (object)p2Sig ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$win", winner);
        cmd.Parameters.AddWithValue("$dur", durationTicks);
        cmd.Parameters.AddWithValue("$mode", (object)gameMode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$opp", (object)opponentType ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void InsertGadgetUses(string gameId, IReadOnlyList<(int tick, int player, string gadgetId)> uses)
    {
        if (uses.Count == 0) return;
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        foreach (var (tick, player, gadgetId) in uses)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO gadget_uses (game_id, tick, player, gadget_id) VALUES ($g, $t, $p, $gid)";
            cmd.Parameters.AddWithValue("$g",   gameId);
            cmd.Parameters.AddWithValue("$t",   tick);
            cmd.Parameters.AddWithValue("$p",   player);
            cmd.Parameters.AddWithValue("$gid", gadgetId);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public List<GameSummary> GetAllGames()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, game_version, played_at, p1_team, p2_team, winner, duration_ticks, game_mode, opponent_type FROM games ORDER BY played_at DESC";
        using var reader = cmd.ExecuteReader();
        var results = new List<GameSummary>();
        while (reader.Read())
        {
            results.Add(new GameSummary(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }
        return results;
    }

    public bool DeleteGame(string id)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM games WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public record GameSummary(string Id, string GameVersion, long PlayedAt,
        string P1Team, string P2Team, int Winner, long DurationTicks,
        string GameMode = null, string OpponentType = null);
}
