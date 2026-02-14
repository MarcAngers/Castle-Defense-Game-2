using Microsoft.AspNetCore.SignalR;
using CastleDefense.Api.Services;
using CastleDefense.Engine.Models;

namespace CastleDefense.Api.Hubs
{
    public class GameHub : Hub
    {
        private readonly GameHostingService _gameService;

        public GameHub(GameHostingService gameService)
        {
            _gameService = gameService;
        }

        public async Task JoinGame(string gameId, string teamName)
        {
            if (!Enum.TryParse(teamName, true, out TeamColour team))
            {
                await Clients.Caller.SendAsync("Error", "Invalid team colour.");
                return;
            }

            var game = _gameService.GetGame(gameId);
            if (game == null)
            {
                await Clients.Caller.SendAsync("Error", "Game not found.");
                return;
            }

            int side = 0;

            lock (game)
            {
                if (string.IsNullOrEmpty(game._state.Player1.ConnectionId))
                {
                    side = 1;
                    game._state.Player1.ConnectionId = Context.ConnectionId;
                    game._state.Player1.Team = team;
                }
                else if (string.IsNullOrEmpty(game._state.Player2.ConnectionId))
                {
                    side = 2;
                    game._state.Player2.ConnectionId = Context.ConnectionId;
                    game._state.Player2.Team = team;
                    _gameService.StartGame(gameId);
                }
            }

            // Add to SignalR Group so we can broadcast to them later
            await Groups.AddToGroupAsync(Context.ConnectionId, gameId);

            // Send initial State
            await Clients.Caller.SendAsync("GameJoined", side, game._state);
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
    }
}