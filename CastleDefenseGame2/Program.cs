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

string dbPath = Path.Combine(AppContext.BaseDirectory, "recordings", "game_records.db");
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
app.UseStaticFiles();
app.UseCors("AllowAll");
app.MapControllers();
app.MapHub<GameHub>("/gameHub");

// Initialize game data
GameDataManager.Initialize();

app.Run();