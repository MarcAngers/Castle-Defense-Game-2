using System.Collections.Concurrent;

namespace CastleDefense.Api.Services;

/// <summary>
/// Survives a player losing their connection mid-game.
///
/// THE PROBLEM THIS SOLVES. A SignalR ConnectionId is per-socket, and the whole server
/// identified players by it: <see cref="CastleDefense.Engine.Models.PlayerState.ConnectionId"/>
/// is what every action handler in GameHub compares against. Reload the page and the new
/// socket has a new id, so the returning player matched neither seat and could no longer
/// act -- while the game kept ticking, so the opponent won against a frozen castle. There
/// was no path back into a game at all.
///
/// So identity is moved off the socket and onto a TOKEN. The token is minted when a human
/// takes a seat, handed to that browser only, and stored in its localStorage; presenting it
/// again re-points the seat at whatever socket is current. The socket becomes a delivery
/// address rather than an identity.
///
/// WHY THE TOKEN IS NOT IN PlayerState. The game loop broadcasts the whole GameState to the
/// group every tick, PlayerState.ConnectionId included -- so anything stored there is
/// visible to the OPPONENT'S browser. A rejoin secret kept in that object would be handed
/// straight to the person with the most reason to misuse it, who could then seize the other
/// seat. Tokens live here, server-side, and are never serialised into a state broadcast.
///
/// PAUSE, NOT FORFEIT. While any human seat is empty the game stops being stepped at all
/// (see GameHostingService.ExecuteAsync) rather than running on with one side frozen.
///
/// THE 60 SECONDS IS WHEN THE WIN BECOMES CLAIMABLE, NOT WHEN IT IS TAKEN. Ending the game
/// automatically at 60s would force a result on someone who would rather wait for their
/// friend to get their router working -- so at 60s the waiting player is OFFERED the win and
/// the game stays paused until they take it. Two exceptions, both because something has to
/// bound a paused game's lifetime: <see cref="MaxPauseSeconds"/> resolves it regardless, and
/// a pause with NO human left connected resolves as soon as it is claimable, since there is
/// nobody who could ever claim it (this is the singleplayer case).
/// </summary>
public class ReconnectService
{
    /// <summary>
    /// How long a disconnected player has before the other player may claim the win. The
    /// seat stays theirs past this point -- it only stops being *guaranteed*.
    /// </summary>
    public const int ClaimAfterSeconds = 60;

    /// <summary>
    /// Hard ceiling on a paused game, after which it resolves whether or not anyone claimed
    /// it. Exists so a game whose players both walked away cannot sit in _activeGames for
    /// the life of the process, holding a seat nobody will ever return to.
    /// </summary>
    public const int MaxPauseSeconds = 30 * 60;

    private sealed class Seat
    {
        public string Token;
        public string ConnectionId;
        public bool Occupied;      // a human took this seat at all
        public bool Connected;
    }

    private sealed class GameSeats
    {
        // Index 0 = player 1, index 1 = player 2.
        public readonly Seat[] Seats = { new Seat(), new Seat() };

        /// <summary>When the pause began. Null means the game is not paused.</summary>
        public DateTime? PausedAt;

        /// <summary>
        /// Stop waiting NOW: the waiting player claimed the win, or the disconnected player
        /// pressed Abandon. Resolved on the game loop's next pass rather than here, so every
        /// game-over -- fought, timed out, claimed -- goes down the one recording path.
        /// </summary>
        public bool ForceResolve;

        /// <summary>Which side dropped FIRST -- what the surviving player's overlay names.</summary>
        public int DroppedSide;

        /// <summary>Last whole second broadcast, so the countdown is sent once per second.</summary>
        public int LastSecondSent = -1;
    }

    private readonly ConcurrentDictionary<string, GameSeats> _games = new();

    // Reverse index so a disconnect -- which arrives with nothing but a socket id -- can
    // find its game in one lookup instead of scanning every live game's seats.
    private readonly ConcurrentDictionary<string, (string gameId, int side)> _byConnection = new();

    /// <summary>
    /// Give a human seat a rejoin token, or re-point an existing one at a new socket.
    /// Returns the token to hand to that browser. Only ever called for real humans --
    /// an "AI_BOT" seat is never registered, which is what makes
    /// <see cref="ConnectedHumanSides"/> able to tell a two-human game from a bot game.
    /// </summary>
    public string RegisterSeat(string gameId, int side, string connectionId)
    {
        var g = _games.GetOrAdd(gameId, _ => new GameSeats());
        var seat = g.Seats[side - 1];
        lock (g)
        {
            seat.Token ??= Guid.NewGuid().ToString("N");
            seat.ConnectionId = connectionId;
            seat.Occupied = true;
            seat.Connected = true;
        }
        _byConnection[connectionId] = (gameId, side);
        return seat.Token;
    }

    /// <summary>Which game and seat a socket belonged to, or null if it held neither.</summary>
    public (string gameId, int side)? SeatOf(string connectionId)
        => _byConnection.TryGetValue(connectionId, out var v) ? v : null;

    /// <summary>
    /// A socket went away. Returns true if this actually emptied a seat of a live game, in
    /// which case the game is now paused and <paramref name="secondsRemaining"/> is the
    /// grace window.
    ///
    /// A stale socket is ignored: after a rejoin the OLD connection's disconnect event can
    /// still arrive (and does, when the browser reloads faster than the server notices), and
    /// treating it as current would pause a game whose player is sitting right there.
    /// </summary>
    public bool MarkDisconnected(string connectionId, out string gameId, out int side,
                                 out int secondsRemaining)
    {
        gameId = null; side = 0; secondsRemaining = 0;
        if (!_byConnection.TryRemove(connectionId, out var v)) return false;
        if (!_games.TryGetValue(v.gameId, out var g)) return false;

        var seat = g.Seats[v.side - 1];
        lock (g)
        {
            if (seat.ConnectionId != connectionId) return false; // superseded by a rejoin
            seat.Connected = false;
            seat.ConnectionId = null;

            // Keep the ORIGINAL pause time if the game is already paused. A second player
            // dropping must not restart the first one's clock.
            if (g.PausedAt == null)
            {
                g.PausedAt = DateTime.UtcNow;
                g.DroppedSide = v.side;
                g.LastSecondSent = -1;
            }
            secondsRemaining = SecondsUntilClaimable(g);
        }
        gameId = v.gameId; side = v.side;
        return true;
    }

    public bool IsPaused(string gameId)
        => _games.TryGetValue(gameId, out var g) && g.PausedAt != null;

    /// <summary>Seconds until the waiting player may claim the win; 0 once they may.</summary>
    public int SecondsRemaining(string gameId)
        => _games.TryGetValue(gameId, out var g) ? SecondsUntilClaimable(g) : 0;

    /// <summary>How long the pause has lasted. Shown to the waiting player once the
    /// countdown has run out and the number that matters becomes "how long have I waited".</summary>
    public int WaitedSeconds(string gameId)
        => _games.TryGetValue(gameId, out var g) ? Waited(g) : 0;

    public bool IsClaimable(string gameId)
        => _games.TryGetValue(gameId, out var g) && g.PausedAt != null
        && Waited(g) >= ClaimAfterSeconds;

    public int DroppedSide(string gameId)
        => _games.TryGetValue(gameId, out var g) ? g.DroppedSide : 0;

    /// <summary>True once per whole second of the pause, so the loop can broadcast at 1Hz
    /// from a 30Hz tick without keeping a timer of its own. Keeps firing after the countdown
    /// reaches zero, because the waiting player's "waited for" display goes on moving.</summary>
    public bool ShouldSendCountdown(string gameId, out int secondsRemaining, out bool claimable,
                                    out int waitedSeconds)
    {
        secondsRemaining = 0; claimable = false; waitedSeconds = 0;
        if (!_games.TryGetValue(gameId, out var g) || g.PausedAt == null) return false;
        lock (g)
        {
            int waited = Waited(g);
            secondsRemaining = SecondsUntilClaimable(g);
            claimable = waited >= ClaimAfterSeconds;
            waitedSeconds = waited;
            if (waited == g.LastSecondSent) return false;
            g.LastSecondSent = waited;
            return true;
        }
    }

    /// <summary>
    /// Should the loop end this paused game now? Three ways, and only three:
    ///   * someone pressed a button -- the waiting player claimed, or the missing one abandoned;
    ///   * the 30-minute ceiling, so a doubly-abandoned game cannot live forever;
    ///   * it became claimable with NOBODY connected to claim it, which is every singleplayer
    ///     disconnect: waiting longer cannot produce a decision, only a stuck game.
    /// Otherwise the pause continues, however long that is -- that is the point.
    /// </summary>
    public bool ShouldResolve(string gameId)
    {
        if (!_games.TryGetValue(gameId, out var g) || g.PausedAt == null) return false;
        if (g.ForceResolve) return true;
        int waited = Waited(g);
        if (waited >= MaxPauseSeconds) return true;
        return waited >= ClaimAfterSeconds && ConnectedHumanSides(gameId).Count == 0;
    }

    /// <summary>
    /// Take the win rather than keep waiting. Only legal once the game is claimable and only
    /// from a seat that is actually still connected -- otherwise a stale client could end a
    /// game it is no longer part of.
    /// </summary>
    public bool ClaimNow(string gameId, string connectionId, out string refusal)
    {
        refusal = null;
        if (!_games.TryGetValue(gameId, out var g)) { refusal = "no such game"; return false; }
        if (g.PausedAt == null) { refusal = "game is not paused"; return false; }
        lock (g)
        {
            int waited = Waited(g);
            if (waited < ClaimAfterSeconds)
            {
                refusal = $"only {waited}s waited, need {ClaimAfterSeconds}";
                return false;
            }
            if (!g.Seats.Any(s => s.Occupied && s.Connected && s.ConnectionId == connectionId))
            {
                refusal = "caller holds no connected seat in this game "
                        + $"(seats: {string.Join(", ", g.Seats.Select((s, i) => $"P{i + 1} occupied={s.Occupied} connected={s.Connected}"))})";
                return false;
            }
            g.ForceResolve = true;
            return true;
        }
    }

    /// <summary>
    /// The human seats still connected. The count is what decides a timed-out game:
    /// exactly one means that player wins by default, none means nobody was there to win
    /// and the game is merely abandoned.
    /// </summary>
    public List<int> ConnectedHumanSides(string gameId)
    {
        var result = new List<int>();
        if (!_games.TryGetValue(gameId, out var g)) return result;
        for (int i = 0; i < 2; i++)
            if (g.Seats[i].Occupied && g.Seats[i].Connected) result.Add(i + 1);
        return result;
    }

    public int OccupiedHumanSeats(string gameId)
        => _games.TryGetValue(gameId, out var g) ? g.Seats.Count(s => s.Occupied) : 0;

    /// <summary>Does this token still name a seat in this game, and if so which one?</summary>
    public bool ValidateToken(string gameId, string token, out int side)
    {
        side = 0;
        if (string.IsNullOrEmpty(token) || gameId == null || !_games.TryGetValue(gameId, out var g))
            return false;
        for (int i = 0; i < 2; i++)
        {
            if (g.Seats[i].Occupied && g.Seats[i].Token == token) { side = i + 1; return true; }
        }
        return false;
    }

    /// <summary>
    /// Is the seat this token names currently OCCUPIED BY A LIVE SOCKET? Used to decide
    /// whether to OFFER a rejoin, and it is what stops a browser holding two seats (two tabs
    /// on one game) from being offered the seat that never went anywhere. A seat somebody is
    /// sitting in is not a seat there is anything to rejoin.
    ///
    /// Deliberately NOT enforced inside <see cref="Rejoin"/>: when a flapping socket
    /// reconnects, the server may not have noticed the old one die yet, and refusing there
    /// would strand the player who has the strongest possible claim to the seat.
    /// </summary>
    public bool IsSeatConnected(string gameId, string token)
    {
        if (!ValidateToken(gameId, token, out int side)) return false;
        if (!_games.TryGetValue(gameId, out var g)) return false;
        return g.Seats[side - 1].Connected;
    }

    /// <summary>
    /// Re-point a seat at a new socket. <paramref name="resumed"/> is true when this was the
    /// last empty human seat, i.e. the game may start ticking again -- a two-human game with
    /// both players gone stays paused until the second one is also back.
    /// </summary>
    public bool Rejoin(string gameId, string token, string connectionId, out int side, out bool resumed)
    {
        resumed = false;
        if (!ValidateToken(gameId, token, out side)) return false;
        // Not _games[gameId]: the game can be released between the check above and here
        // (its last tick resolving it), and that must read as "too late", not throw.
        if (!_games.TryGetValue(gameId, out var g)) return false;
        lock (g)
        {
            var seat = g.Seats[side - 1];
            if (seat.ConnectionId != null) _byConnection.TryRemove(seat.ConnectionId, out _);
            seat.ConnectionId = connectionId;
            seat.Connected = true;
            _byConnection[connectionId] = (gameId, side);

            if (g.Seats.All(s => !s.Occupied || s.Connected))
            {
                g.PausedAt = null;
                g.LastSecondSent = -1;
                resumed = true;
            }
        }
        return true;
    }

    /// <summary>Give up the grace window immediately -- the "Abandon" button on the rejoin
    /// prompt. Resolves on the next loop pass exactly as a timeout would.</summary>
    public bool Forfeit(string gameId, string token)
    {
        if (!ValidateToken(gameId, token, out int side)) return false;
        if (!_games.TryGetValue(gameId, out var g)) return false;
        lock (g)
        {
            var seat = g.Seats[side - 1];
            seat.Connected = false;
            if (seat.ConnectionId != null) _byConnection.TryRemove(seat.ConnectionId, out _);
            seat.ConnectionId = null;
            g.PausedAt ??= DateTime.UtcNow;
            g.ForceResolve = true;          // resolve on the loop's next pass
            g.DroppedSide = side;
        }
        return true;
    }

    /// <summary>Forget a finished game. Called from the game-over path so tokens for dead
    /// games cannot accumulate for the life of the process.</summary>
    public void Release(string gameId)
    {
        if (!_games.TryRemove(gameId, out var g)) return;
        foreach (var s in g.Seats)
            if (s.ConnectionId != null) _byConnection.TryRemove(s.ConnectionId, out _);
    }

    private static int Waited(GameSeats g)
        => g.PausedAt == null ? 0 : (int)(DateTime.UtcNow - g.PausedAt.Value).TotalSeconds;

    private static int SecondsUntilClaimable(GameSeats g)
    {
        if (g.PausedAt == null) return 0;
        int left = ClaimAfterSeconds - Waited(g);
        return left <= 0 ? 0 : left;
    }
}
