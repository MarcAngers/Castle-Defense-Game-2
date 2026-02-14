using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using CastleDefense.Engine;
using CastleDefense.Api.Hubs;
using CastleDefense.Engine.Models;

namespace CastleDefense.Api.Services
{
    public class GameHostingService : BackgroundService
    {
        private readonly ConcurrentDictionary<string, GameEngine> _activeGames = new();
        private readonly ConcurrentDictionary<string, GameEngine> _lobbyGames = new();
        private readonly IHubContext<GameHub> _hubContext;

        public GameHostingService(IHubContext<GameHub> hubContext)
        {
            _hubContext = hubContext;
        }

        public GameEngine GetGame(string gameId)
        {
            if (_activeGames.TryGetValue(gameId, out var activeEngine)) return activeEngine;
            if (_lobbyGames.TryGetValue(gameId, out var lobbyEngine)) return lobbyEngine;
            return null;
        }

        public string CreateGame()
        {
            var gameId = Guid.NewGuid().ToString("N").Substring(0, 6).ToUpper();
            var state = new GameState();
            var engine = new GameEngine(state);

            _lobbyGames.TryAdd(gameId, engine);
            
            return gameId;
        }

        public string StartGame(string gameId)
        {
            if (_lobbyGames.TryRemove(gameId, out var engine))
            {
                // Move it to the active list (The loop picks it up instantly)
                _activeGames.TryAdd(gameId, engine);
                return gameId;
            }
            return gameId;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var start = DateTime.UtcNow;

                foreach (var kvp in _activeGames)
                {
                    var gameId = kvp.Key;
                    var engine = kvp.Value;

                    // 1. Run Game Logic (process queue -> ticks -> physics)
                    // We lock to ensure we don't send state while it's half-updated
                    lock (engine)
                    {
                        engine.Tick();
                    }

                    // 2. Broadcast State
                    // We send the entire object. SignalR handles serialization.
                    // Optimization: In the future, only send deltas.
                    await _hubContext.Clients.Group(gameId).SendAsync("GameStateUpdate", engine._state);
                }

                // Maintain 30 FPS
                var elapsed = (DateTime.UtcNow - start).TotalMilliseconds;
                var targetDelay = (1000 / GameEngine.TICKS_PER_SECOND) - (int)elapsed;
                if (targetDelay > 0) await Task.Delay(targetDelay, stoppingToken);
            }
        }
    }
}