using CastleDefense.Api.Data;
using CastleDefense.Api.Hubs;
using CastleDefense.Engine;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Models;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

namespace CastleDefense.Api.Services
{
    public class GameHostingService : BackgroundService
    {
        private readonly ConcurrentDictionary<string, GameEngine> _activeGames = new();
        private readonly ConcurrentDictionary<string, GameEngine> _lobbyGames = new();
        private readonly ConcurrentDictionary<string, GameRecorder> _recorders = new();
        private readonly ConcurrentDictionary<string, Func<GameState, int>> _leagueOpponents = new();
        private readonly IHubContext<GameHub> _hubContext;
        private readonly AIBrain _aiBrain;
        private readonly List<(string name, AIBrain brain)> _leagueModels = new();
        private readonly GameDatabase _db;
        private readonly Random _watchRng  = new();
        private readonly Random _leagueRng = new();
        private readonly string _replayDir;
        private readonly string _gameVersion;

        public GameHostingService(IHubContext<GameHub> hubContext, AIBrain aiBrain,
            GameDatabase db, IConfiguration config)
        {
            _hubContext   = hubContext;
            _aiBrain      = aiBrain;
            _db           = db;
            _replayDir    = Path.Combine(AppContext.BaseDirectory, "recordings");
            _gameVersion  = config["GameVersion"] ?? "v1.0";

            string leagueDir = Path.Combine(AppContext.BaseDirectory, "league_models");
            if (Directory.Exists(leagueDir))
            {
                foreach (var f in Directory.GetFiles(leagueDir, "*.onnx")
                                           .Where(x => !x.EndsWith(".data")))
                {
                    try
                    {
                        _leagueModels.Add((Path.GetFileNameWithoutExtension(f), new AIBrain(f)));
                        Console.WriteLine($"[League] Loaded: {Path.GetFileNameWithoutExtension(f)}");
                    }
                    catch (Exception ex) { Console.WriteLine($"[League] Skip {Path.GetFileName(f)}: {ex.Message}"); }
                }
            }
            Console.WriteLine($"[League] {_leagueModels.Count} model(s) available for Training League.");
        }

        // ── Opponent selection ─────────────────────────────────────────────────────

        public void SetupLeagueOpponent(string gameId, int timeSkip)
        {
            int botSel   = _leagueRng.Next(16);
            int spamTier = Math.Max(Math.Min(timeSkip + _leagueRng.Next(-2, 3), 8), 1);

            Func<GameState, int> opponent;

            if (botSel == 1)
            {
                opponent = state => AntiSpamAction(state.CurrentTick, state.Player2.Team);
            }
            else if (botSel >= 2 && botSel <= 5)
            {
                opponent = _ => spamTier;
            }
            else if (botSel > 5 && _leagueModels.Count > 0)
            {
                var chosen = _leagueModels[_leagueRng.Next(_leagueModels.Count)];
                opponent = state => chosen.brain.GetBestAction(
                    state.GetStateVector(2), state.GetActionMask(2));
            }
            else
            {
                // Random dummy
                opponent = state =>
                {
                    var mask  = state.GetActionMask(2);
                    var valid = Enumerable.Range(0, mask.Length).Where(i => mask[i] == 1).ToList();
                    return valid[_leagueRng.Next(valid.Count)];
                };
            }

            _leagueOpponents[gameId] = opponent;
        }

        private static int AntiSpamAction(long tick, TeamColour team)
        {
            if (tick <= 421)  return 10;
            if (tick <= 781)  return 9;
            if (tick <= 1050) return 3;
            if (tick <= 1380) return 10;
            if (tick <= 1590) return 3;
            if (tick <= 2100) return 9;
            return team == TeamColour.Orange ? 5
                 : (team == TeamColour.Purple || team == TeamColour.Blue) ? 3
                 : 4;
        }

        // ── Game lifecycle ─────────────────────────────────────────────────────────

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
                LobbyGames  = _lobbyGames.Keys.ToList()
            };
        }

        public string CreateGame(string gameMode)
        {
            var gameId = Guid.NewGuid().ToString("N").Substring(0, 6).ToUpper();
            var state  = new GameState();
            state.GameMode = gameMode;
            var engine = new GameEngine(state);

            engine.OnGadgetAnimation += (gadgetId, side, position, targetId) =>
            {
                _hubContext.Clients.Group(gameId).SendAsync("PlayGadgetAnimation", gadgetId, side, position, targetId);
                if (_recorders.TryGetValue(gameId, out var rec))
                    rec.RecordGadgetUse((int)engine._state.CurrentTick, side, gadgetId);
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
                _activeGames.TryAdd(gameId, engine);
                _recorders.TryAdd(gameId, new GameRecorder(gameId,
                    engine._state.CurrentTick,
                    engine._state.Player1.Money,
                    engine._state.Player2.Money));
                _hubContext.Clients.Group(gameId).SendAsync("GameStarted");
                return gameId;
            }
            return gameId;
        }

        // ── Main game loop ─────────────────────────────────────────────────────────

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var start = DateTime.UtcNow;

                foreach (var kvp in _activeGames)
                {
                    var gameId = kvp.Key;
                    var engine = kvp.Value;
                    _recorders.TryGetValue(gameId, out var recorder);

                    lock (engine)
                    {
                        engine.ResetLastActions();

                        if (engine._state.CurrentTick % 3 == 0)
                        {
                            // P1 random dummy (watch mode only)
                            if (engine._state.GameMode == "watch")
                            {
                                int[] p1Mask     = engine._state.GetActionMask(1);
                                var   validP1    = Enumerable.Range(0, p1Mask.Length)
                                                             .Where(i => p1Mask[i] == 1).ToList();
                                int   p1Action   = validP1[_watchRng.Next(validP1.Count)];
                                if (p1Action != 0) engine.ApplyAction(1, p1Action);
                            }

                            // P2 AI / opponent
                            if (engine._state.GameMode == "sp"  || engine._state.GameMode == "vai" ||
                                engine._state.GameMode == "watch")
                            {
                                // Standard ONNX singleplayer bot
                                float[] aiState     = engine._state.GetStateVector(2);
                                int[]   aiActionMask = engine._state.GetActionMask(2);

                                var oob = aiState.Select((v, i) => (v, i)).Where(x => x.v > 1.01f || x.v < -0.01f).ToList();
                                if (oob.Any())
                                {
                                    Console.ForegroundColor = ConsoleColor.Red;
                                    Console.WriteLine($"\n[WARNING] {oob.Count} obs value(s) out of [0,1] range!");
                                    foreach (var (v, i) in oob)
                                        Console.WriteLine($"   -> [{i}] = {v}");
                                    Console.ResetColor();
                                }

                                int aiAction = _aiBrain.GetBestAction(aiState, aiActionMask);
                                if (aiAction != 0) engine.ApplyAction(2, aiAction);
                            }
                            else if (engine._state.GameMode == "league")
                            {
                                // Training-league opponent (random dummy / spam bot / anti-spam / ONNX model)
                                if (_leagueOpponents.TryGetValue(gameId, out var oppFunc))
                                {
                                    int p2Action = oppFunc(engine._state);
                                    if (p2Action != 0) engine.ApplyAction(2, p2Action);
                                }
                            }
                        }

                        engine.Tick();
                        recorder?.RecordTick(engine.LastActionP1, engine.LastActionP2);
                    }

                    if (engine._state.IsGameOver)
                    {
                        _leagueOpponents.TryRemove(gameId, out _);

                        if (_recorders.TryRemove(gameId, out var finishedRecorder))
                        {
                            bool isSingleplayer = engine._state.GameMode == "sp"
                                               || engine._state.GameMode == "vai"
                                               || engine._state.GameMode == "league";
                            bool isWatch = engine._state.GameMode == "watch";
                            string subdir = isSingleplayer ? "singleplayer" : "multiplayer";
                            if (!isWatch) finishedRecorder.Save(Path.Combine(_replayDir, subdir),
                                engine._state.Player1, engine._state.Player2,
                                engine._state.WinnerSide, engine._state.CurrentTick, _gameVersion, _db);
                        }

                        await _hubContext.Clients.Group(gameId).SendAsync("GameOver", engine._state);
                        _activeGames.TryRemove(gameId, out _);
                    }
                    else
                    {
                        await _hubContext.Clients.Group(gameId).SendAsync("GameStateUpdate", engine._state);
                    }
                }

                var elapsed     = (DateTime.UtcNow - start).TotalMilliseconds;
                var targetDelay = (1000 / GameEngine.TICKS_PER_SECOND) - (int)elapsed;
                if (targetDelay > 0) await Task.Delay(targetDelay, stoppingToken);
            }
        }
    }
}
