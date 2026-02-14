using CastleDefense.Api.Services;
using CastleDefense.Engine;  // From your Engine project
using CastleDefense.Engine.Models;
using Microsoft.AspNetCore.Mvc;

namespace CastleDefense.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class GamesController : ControllerBase
    {
        private readonly GameHostingService _gameService;

        public GamesController(GameHostingService gameService)
        {
            _gameService = gameService;
        }

        public class CreateGameRequest
        {
            public TeamColour Team { get; set; }
        }

        [HttpPost]
        public IActionResult CreateGame()
        {
            // 2. Pass the colour into your Service
            // (You will need to update your _gameService.CreateGame signature too!)
            string gameId = _gameService.CreateGame();

            return Ok(new { gameId = gameId });
        }

        // GET: api/games/{id}
        // Useful for the client to check if a game exists and get initial state
        [HttpGet("{id}")]
        public IActionResult GetGame(string id)
        {
            var engine = _gameService.GetGame(id);

            if (engine == null)
            {
                return NotFound("Game not found");
            }

            // We return the .State (Data), not the Engine (Logic)
            // This gives the client the full initial board setup
            return Ok(engine._state);
        }
    }
}