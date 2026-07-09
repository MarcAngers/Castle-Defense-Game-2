using Microsoft.AspNetCore.SignalR;
using CastleDefense.Api.Services;
using CastleDefense.Engine.Models;
using CastleDefense.Engine.Data;

namespace CastleDefense.Api.Hubs
{
    public class GameHub : Hub
    {
        private readonly GameHostingService _gameService;

        public GameHub(GameHostingService gameService)
        {
            _gameService = gameService;
        }

        private static readonly Random _leagueRng = new();

        public async Task JoinGame(string gameId, string teamName, string[] loadout)
        {
            var game = _gameService.GetGame(gameId);
            if (game == null)
            {
                await Clients.Caller.SendAsync("Error", "Game not found.");
                return;
            }

            // Training League: server assigns everything — skip client team/loadout entirely
            if (game._state.GameMode == "league")
            {
                lock (game)
                {
                    if (!string.IsNullOrEmpty(game._state.Player1.ConnectionId)) return;

                    int timeSkip = Math.Max(_leagueRng.Next(-8, 9), 0);
                    string upg   = timeSkip > 5 ? "_3" : timeSkip > 3 ? "_2" : "";

                    game._state.Player1 = new PlayerState(timeSkip);
                    game._state.Player1.Side         = 1;
                    game._state.Player1.ConnectionId = Context.ConnectionId;
                    game._state.Player1.Team         = GameDataManager.GetRandomTeam();
                    game._state.Player1.SetLoadout(new[] {
                        GameDataManager.GetRandomOGadgetId()  + upg,
                        GameDataManager.GetRandomDGadgetId()  + upg,
                        GameDataManager.GetSignatureGadgetIdForTeam(game._state.Player1.Team) + upg });
                    game._state.Player2 = new PlayerState(timeSkip);
                    game._state.Player2.Side         = 2;
                    game._state.Player2.ConnectionId = "AI_BOT";
                    game._state.Player2.Team         = GameDataManager.GetRandomTeam();
                    game._state.Player2.SetLoadout(new[] {
                        GameDataManager.GetRandomOGadgetId()  + upg,
                        GameDataManager.GetRandomDGadgetId()  + upg,
                        GameDataManager.GetSignatureGadgetIdForTeam(game._state.Player2.Team) + upg });

                    // Single roll — both players receive starting money or neither does
                    if (_leagueRng.Next(5) == 0)
                    {
                        game._state.Player1.Money = game._state.Player1.InvestmentPrice + game._state.Player1.Income;
                        game._state.Player2.Money = game._state.Player2.InvestmentPrice + game._state.Player2.Income;
                    }

                    game._state.CurrentTick = 30 * 30 * timeSkip;
                    game.RewirePlayerEvents();
                    _gameService.SetupLeagueOpponent(gameId, timeSkip);
                }

                await Groups.AddToGroupAsync(Context.ConnectionId, gameId);
                await Clients.Caller.SendAsync("GameJoined", 1, game._state);
                _gameService.StartGame(gameId);
                return;
            }

            if (!Enum.TryParse(teamName, true, out TeamColour team))
            {
                await Clients.Caller.SendAsync("Error", "Invalid team colour.");
                return;
            }

            int side = 0;

            lock (game)
            {
                if (string.IsNullOrEmpty(game._state.Player1.ConnectionId))
                {
                    side = 1;
                    game._state.Player1.Side = side;
                    game._state.Player1.ConnectionId = Context.ConnectionId;
                    game._state.Player1.Team = team;
                    game._state.Player1.SetLoadout(loadout);
                }
                else if (string.IsNullOrEmpty(game._state.Player2.ConnectionId))
                {
                    side = 2;
                    game._state.Player2.Side = side;
                    game._state.Player2.ConnectionId = Context.ConnectionId;
                    game._state.Player2.Team = team;
                    game._state.Player2.SetLoadout(loadout);
                    _gameService.StartGame(gameId);
                }
            }

            // Add to SignalR Group so we can broadcast to them later
            await Groups.AddToGroupAsync(Context.ConnectionId, gameId);

            // Send initial State
            await Clients.Caller.SendAsync("GameJoined", side, game._state);

            if (game._state.GameMode == "sp" || game._state.GameMode == "watch")
            {
                game._state.Player2.Side = 2;
                game._state.Player2.ConnectionId = "AI_BOT";
                game._state.Player2.Team = GameDataManager.GetRandomTeam();
                game._state.Player2.SetLoadout(new string[] { GameDataManager.GetRandomOGadgetId(), GameDataManager.GetRandomDGadgetId(), GameDataManager.GetSignatureGadgetIdForTeam(game._state.Player2.Team) });

                _gameService.StartGame(gameId);
            }
        }

        public void SpawnUnit(string gameId, string unitId)
        {
            var game = _gameService.GetGame(gameId);
            if (game == null) return;
           
            // Identify which player is calling
            int side = 0;
            if (game._state.Player1.ConnectionId == Context.ConnectionId) side = 1;
            else if (game._state.Player2.ConnectionId == Context.ConnectionId) side = 2;

            if (side == 0) return; // Spectators can't spawn

            // THREAD SAFETY: We do NOT modify State here. We queue it.
            game.EnqueueAction(() =>
            {
                game.SpawnUnit(side, unitId);
            });
        }

        public void Invest(string gameId)
        {
            var game = _gameService.GetGame(gameId);
            if (game == null) return;

            // Identify which player is calling
            int side = 0;
            if (game._state.Player1.ConnectionId == Context.ConnectionId) side = 1;
            else if (game._state.Player2.ConnectionId == Context.ConnectionId) side = 2;

            if (side == 0) return; // Spectators can't invest

            // THREAD SAFETY: We do NOT modify State here. We queue it.
            game.EnqueueAction(() =>
            {
                game.Invest(side);
            });
        }

        public void Repair(string gameId)
        {
            var game = _gameService.GetGame(gameId);
            if (game == null) return;

            // Identify which player is calling
            int side = 0;
            if (game._state.Player1.ConnectionId == Context.ConnectionId) side = 1;
            else if (game._state.Player2.ConnectionId == Context.ConnectionId) side = 2;

            if (side == 0) return; // Spectators can't repair

            // THREAD SAFETY: We do NOT modify State here. We queue it.
            game.EnqueueAction(() =>
            {
                game.Repair(side);
            });
        }

        public void UseGadget(string gameId, string gadgetId, float position)
        {
            var game = _gameService.GetGame(gameId);
            if (game == null) return;

            // Identify which player is calling
            int side = 0;
            if (game._state.Player1.ConnectionId == Context.ConnectionId) side = 1;
            else if (game._state.Player2.ConnectionId == Context.ConnectionId) side = 2;

            if (side == 0) return; // Spectators can't use gadgets

            // THREAD SAFETY: We do NOT modify State here. We queue it.
            game.EnqueueAction(() =>
            {
                game.UseGadget(side, gadgetId, (int)position);
            });
        }
    }
}