using RemoteDesk.Core.Security;
using RemoteDesk.Server.Signaling;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();

var port = Environment.GetEnvironmentVariable("PORT") ?? "5000";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = true;
    options.MaximumReceiveMessageSize = 10 * 1024 * 1024; // 10MB
});

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyHeader()
              .AllowAnyMethod()
              .SetIsOriginAllowed(_ => true)
              .AllowCredentials();
    });
});

var app = builder.Build();

app.UseCors();

app.MapGet("/", () => Results.Ok(new
{
    Service = "RemoteDesk Signaling & TURN Broker",
    Status = "Online",
    Version = "1.0",
    Timestamp = DateTime.UtcNow
}));

// REST endpoint to retrieve STUN & short-lived TURN credentials
app.MapGet("/api/ice-servers", (string? deviceId) =>
{
    var servers = TurnCredentialGenerator.GetConfiguredIceServers(deviceId);
    return Results.Ok(servers);
});

app.MapHub<SignalingHub>("/signaling");

Log.Information("RemoteDesk Signaling & TURN Broker running on http://0.0.0.0:{Port}", port);

app.Run();
