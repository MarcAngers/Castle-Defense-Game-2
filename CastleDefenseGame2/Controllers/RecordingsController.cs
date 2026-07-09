using CastleDefense.Api.Data;
using Microsoft.AspNetCore.Mvc;

namespace CastleDefense.Api.Controllers;

[ApiController]
[Route("api/recordings")]
public class RecordingsController : ControllerBase
{
    private readonly GameDatabase _db;
    private readonly string _replayDir;

    public RecordingsController(GameDatabase db)
    {
        _db = db;
        _replayDir = Path.Combine(AppContext.BaseDirectory, "recordings");
    }

    [HttpGet]
    public IActionResult List() => Ok(_db.GetAllGames());

    [HttpDelete("{gameId}")]
    public IActionResult Delete(string gameId)
    {
        bool found = _db.DeleteGame(gameId);
        if (!found) return NotFound();

        var replayPath = Path.Combine(_replayDir, gameId + ".replay");
        if (System.IO.File.Exists(replayPath))
            System.IO.File.Delete(replayPath);

        return NoContent();
    }
}
