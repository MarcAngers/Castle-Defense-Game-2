using CastleDefense.Api.Data;
using CastleDefense.Api.Hubs;
using CastleDefense.Api.Services;
using CastleDefense.Engine.Data;
using CastleDefense.Engine.Models;

var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddControllers();
builder.Services.AddSignalR();
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
string dbPath = Path.Combine(builder.Environment.ContentRootPath, "recordings", "game_records.db");
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

app.Run();