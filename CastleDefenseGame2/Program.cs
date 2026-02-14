using CastleDefense.Api.Services;
using CastleDefense.Api.Hubs;
using CastleDefense.Engine.Data;

var builder = WebApplication.CreateBuilder(args);

// Initialize game data
GameDataManager.Initialize();

// Add services
builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddSingleton<GameHostingService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GameHostingService>());

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

app.Run();