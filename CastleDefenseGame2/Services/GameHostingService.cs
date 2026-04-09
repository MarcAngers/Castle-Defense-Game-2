using CastleDefense.Api.Hubs;
using CastleDefense.Engine;
using CastleDefense.Engine.Models;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace CastleDefense.Api.Services
{
    public class GameHostingService : BackgroundService
    {
        private readonly ConcurrentDictionary<string, GameEngine> _activeGames = new();
        private readonly ConcurrentDictionary<string, GameEngine> _lobbyGames = new();
        private readonly IHubContext<GameHub> _hubContext;
        private readonly AIBrain _aiBrain;

        public GameHostingService(IHubContext<GameHub> hubContext, AIBrain aiBrain)
        {
            _hubContext = hubContext;
            _aiBrain = aiBrain;
        }

        public GameEngine GetGame(string gameId)
        {
            if (_activeGames.TryGetValue(gameId, out var activeEngine)) return activeEngine;
            if (_lobbyGames.TryGetValue(gameId, out var lobbyEngine)) return lobbyEngine;
            return null;
        }

        public GameListResult GetAllGameIds()
        {
            return new GameListResult
            {
                ActiveGames = _activeGames.Keys.ToList(),
                LobbyGames = _lobbyGames.Keys.ToList()
            };
        }

        public string CreateGame(string gameMode)
        {
            var gameId = Guid.NewGuid().ToString("N").Substring(0, 6).ToUpper();
            var state = new GameState();
            state.GameMode = gameMode;
            var engine = new GameEngine(state);

            engine.OnGadgetAnimation += (gadgetId, side, position, targetId) =>
            {
                _hubContext.Clients.Group(gameId).SendAsync("PlayGadgetAnimation", gadgetId, side, position, targetId);
            };
            engine.OnGadgetUpgraded += (side, newGadgetDef) =>
            {
                _hubContext.Clients.Group(gameId).SendAsync("GadgetUpgraded", side, newGadgetDef);
            };

            _lobbyGames.TryAdd(gameId, engine);
            
            return gameId;
        }

        public string StartGame(string gameId)
        {
            if (_lobbyGames.TryRemove(gameId, out var engine))
            {
                // Move it to the active list (The loop picks it up instantly)
                _activeGames.TryAdd(gameId, engine);
                _hubContext.Clients.Group(gameId).SendAsync("GameStarted");
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

                    // 1. Run Game Logic
                    lock (engine)
                    {
                        if (engine._state.GameMode == "sp" || engine._state.GameMode == "vai")
                        {
                            if (engine._state.CurrentTick % 3 == 0) // Replace this number with the AI's decision speed
                            {
                                // Get the board from Player 2's perspective
                                float[] aiState = engine._state.GetStateVector(2);
                                int[] aiActionMask = engine._state.GetActionMask(2);
                                // 1. Check for values > 1.0 (The Python Clipping Bug)
                                var outOfBoundsItems = aiState
                                    .Select((val, index) => new { Index = index, Value = val })
                                    .Where(x => x.Value > 1.01f || x.Value < -0.01f)
                                    .ToList();

                                if (outOfBoundsItems.Any())
                                {
                                    Console.ForegroundColor = ConsoleColor.Red;
                                    Console.WriteLine($"\n[WARNING] Found {outOfBoundsItems.Count} value(s) outside the 0.0 - 1.0 range!");
                                    foreach (var item in outOfBoundsItems)
                                    {
                                        Console.WriteLine($"   -> Index [{item.Index}] is out of bounds with value: {item.Value}");
                                    }
                                    Console.ResetColor();
                                }

                                // Ask the neural network for its best move
                                int aiAction = _aiBrain.GetBestAction(aiState, aiActionMask);

                                if (aiAction != 0)
                                {
                                    engine.ApplyAction(2, aiAction);
                                }
                            }                           
                        }

                        engine.Tick();
                    }

                    // 2. Check for Game Over BEFORE broadcasting the normal state
                    if (engine._state.IsGameOver)
                    {
                        // Send a dedicated GameOver event with just the winning side
                        await _hubContext.Clients.Group(gameId).SendAsync("GameOver", engine._state);

                        // Remove the game from the active dictionary so it stops ticking forever!
                        _activeGames.TryRemove(gameId, out _);
                    }
                    else
                    {
                        // 3. Game is still running, broadcast the normal state
                        await _hubContext.Clients.Group(gameId).SendAsync("GameStateUpdate", engine._state);
                    }
                }

                // Maintain 30 FPS
                var elapsed = (DateTime.UtcNow - start).TotalMilliseconds;
                var targetDelay = (1000 / GameEngine.TICKS_PER_SECOND) - (int)elapsed;
                if (targetDelay > 0) await Task.Delay(targetDelay, stoppingToken);
            }
        }
    }
}