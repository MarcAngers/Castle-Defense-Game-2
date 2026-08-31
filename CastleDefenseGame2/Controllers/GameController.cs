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
            public string GameMode { get; set; }
        }

        [HttpPost]
        public IActionResult CreateGame([FromBody] CreateGameRequest request)
        {
            string gameId = _gameService.CreateGame(request?.GameMode ?? "mp");

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

            // Same wire shape the SignalR clients get. Nothing in wwwroot calls this
            // endpoint -- it is a debug view -- but returning the raw engine state handed
            // anyone who knew a game id both players' ConnectionIds. See GameStateWire.
            return Ok(GameStateWire.From(engine._state));
        }

        [HttpGet("all")]
        public IActionResult GetAllGames()
        {
            var games = _gameService.GetAllGameIds();
            return Ok(games);
        }

        [HttpGet("practice-opponents")]
        public IActionResult GetPracticeOpponents()
        {
            var (spamTiers, antiSpamAvailable, modelNames) = _gameService.GetPracticeOpponentOptions();
            return Ok(new { spamTiers, antiSpamAvailable, modelNames });
        }
    }
}