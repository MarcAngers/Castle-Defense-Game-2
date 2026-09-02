using CastleDefense.Api.Data;
using CastleDefense.Api.Hubs;
using CastleDefense.Api.Services;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Models;

var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddControllers();
builder.Services.AddSignalR();
// Rejoin tokens and the disconnect grace window. Singleton because it outlives any one
// socket by design -- that is the entire point of it.
builder.Services.AddSingleton<ReconnectService>();
builder.Services.AddSingleton<GameHostingService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GameHostingService>());

string modelPath = Path.Combine(AppContext.BaseDirectory, "AI_Models", "castle_defense_bot.onnx");
builder.Services.AddSingleton<AIBrain>(new AIBrain(modelPath));

// Recordings used to live under AppContext.BaseDirectory (bin/<Config>/net10.0/recordings),
// which is git-ignored, disposable build output -- a routine bin/obj cleanup permanently
// destroyed ~144 recorded games with no way to recover them. ContentRootPath (the project
// source directory) survives build cleanup, so recordings now live there instead.
// Interim fix only: this is still just loose files/SQLite next to the repo, not a real
// datastore -- recorded game data should eventually move to a proper database.
// The recordings ROOT is configurable so an agent testing a feature does not write games
// into Marc's human play record -- see RecordingPaths for the full reasoning. The replay
// files and this database must move together, which is why both read the same helper.
string recordingsRoot = RecordingPaths.Root(builder.Configuration, builder.Environment.ContentRootPath);
string dbPath = Path.Combine(recordingsRoot, "game_records.db");
Directory.CreateDirectory(recordingsRoot);
if (RecordingPaths.IsRedirected(builder.Configuration))
    Console.WriteLine($"[Recordings] REDIRECTED to {recordingsRoot} -- these games stay out of the human corpus.");
builder.Services.AddSingleton(new GameDatabase(dbPath));

// Add CORS (Crucial for WebGL/Web clients)
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll",
        builder => builder
            .WithOrigins("http://localhost:5168") // Your frontend URL
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials()); // Required for SignalR
});

var app = builder.Build();

app.UseDefaultFiles();
// wwwroot (index.html, script.js, and every static/views/*.html + *.js the SPA fetches
// at runtime) is served with no Cache-Control by default, so browsers apply heuristic
// caching and can silently keep running old JS/HTML for days after a deploy -- this is
// what made the Practice mode feature look "broken" (a stale browser was still running
// pre-Practice-mode code, not a real bug). Forcing revalidation on every request fixes
// that: browsers still cache the bytes, but always check via ETag first, so an unchanged
// file costs a cheap 304 while a changed one is never silently missed.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers.CacheControl = "no-cache";
    }
});
app.UseCors("AllowAll");
app.MapControllers();
app.MapHub<GameHub>("/gameHub");

// Initialize game data
GameDataManager.Initialize();

// Singleplayer counter-picking. TopK = 1 plays the single best measured answer to the
// human's loadout (maximum win rate, fully predictable); raising it samples uniformly
// among the top K so the response cannot be memorised. Set Enabled false to restore the
// uniform random opponent loadout.
CounterPicker.Enabled = builder.Configuration.GetValue("CounterPick:Enabled", true);
CounterPicker.TopK = builder.Configuration.GetValue("CounterPick:TopK", 1);

// ForcedLoadout overrides both of the above: the bot plays this every singleplayer game
// regardless of what the human picked. Set to run MIRROR matches, where both sides share a
// loadout so a difference in outcome is attributable to play rather than to loadout. Clear
// it (empty string) to go back to counter-picking.
CounterPicker.ForcedLoadout = builder.Configuration.GetValue("CounterPick:ForcedLoadout", "");
if (!string.IsNullOrWhiteSpace(CounterPicker.ForcedLoadout))
{
    // Printed for the same reason the forced-map line below is: this is gameplay-affecting,
    // it silently overrides the counter-picker, and nothing else on screen says it is on.
    // Leaving it set by accident makes every singleplayer game -- and every recording of one
    // -- incomparable to games played without it.
    Console.WriteLine($"[counter-pick] FORCED loadout active: the bot plays " +
                      $"{CounterPicker.ForcedLoadout} every singleplayer game, ignoring the " +
                      $"counter table and the random roll. Clear CounterPick:ForcedLoadout to disable.");
}
else if (CounterPicker.Enabled)
{
    Console.WriteLine($"[counter-pick] enabled, TopK={CounterPicker.TopK} -- the bot answers " +
                      $"your loadout from counter_table.csv.");
}

// TEMPORARY (2026-08-27): pins the map for EVERY hosted game rather than rolling one, so a
// map's ambient animation can be looked at on demand while it is being built. An unknown or
// empty value simply leaves the roll alone. Clear it when the atmosphere work is done --
// see CLEANUP_BACKLOG.md. Note this is gameplay-affecting now that maps carry effects.
var forcedMapName = builder.Configuration.GetValue("Map:ForcedMap", "");
if (!string.IsNullOrWhiteSpace(forcedMapName))
{
    if (Enum.TryParse<TeamColour>(forcedMapName, ignoreCase: true, out var forcedMap))
    {
        GameHostingService.ForcedMap = forcedMap;
        Console.WriteLine($"[map] FORCED map active: every game is played on {forcedMap}. Temporary -- see CLEANUP_BACKLOG.md.");
    }
    else
    {
        Console.WriteLine($"[map] Map:ForcedMap '{forcedMapName}' is not a known map -- ignoring, maps stay random.");
    }
}

// Which bot singleplayer faces: "search" (flagship, the default) or "heuristic". Set to
// "heuristic" to record human games against the SAME opponent the defence-only bot is
// benchmarked against -- otherwise a recorded game is not comparable to those numbers.
// Does not affect the Acceptance Test, which always measures the shipped search bot.
GameHostingService.SingleplayerOpponent =
    builder.Configuration.GetValue("Singleplayer:Opponent", "search");

// Repair fixes on top of the flagship. False is the flagship exactly.
GameHostingService.SingleplayerRepairFix =
    builder.Configuration.GetValue("Singleplayer:RepairFix", false);
GameHostingService.SingleplayerHazardFix =
    builder.Configuration.GetValue("Singleplayer:HazardFix", false);
GameHostingService.SingleplayerEconomyBrake =
    builder.Configuration.GetValue("Singleplayer:EconomyBrake", false);

app.Run();