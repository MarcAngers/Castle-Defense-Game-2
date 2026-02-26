using CastleDefense.Api.Services;
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
            string gameId = _gameService.CreateGame();

            return Ok(new { gameId = gameId });
        }

        [HttpGet("{id}")]
        public IActionResult GetGame(string id)
        {
            var engine = _gameService.GetGame(id);

            if (engine == null)
            {
                return NotFound("Game not found");
            }

            return Ok(engine._state);
        }

        [HttpGet("all")]
        public IActionResult GetAllGames()
        {
            var games = _gameService.GetAllGameIds();
            return Ok(games);
        }
    }
}