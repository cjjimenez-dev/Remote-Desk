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
    var turnHost = Environment.GetEnvironmentVariable("TURN_HOST") ?? "turn.remotedesk.net";
    var turnSecret = Environment.GetEnvironmentVariable("TURN_SECRET") ?? "RemoteDesk_Turn_Secret_Key_Change_In_Production_2026!";
    var udpPort = int.TryParse(Environment.GetEnvironmentVariable("TURN_UDP_PORT"), out var u) ? u : 3478;
    var tlsPort = int.TryParse(Environment.GetEnvironmentVariable("TURN_TLS_PORT"), out var t) ? t : 5349;
    var fallbackPort = int.TryParse(Environment.GetEnvironmentVariable("TURN_FALLBACK_PORT"), out var f) ? f : 443;

    var id = string.IsNullOrWhiteSpace(deviceId) ? Guid.NewGuid().ToString("N")[..8] : deviceId;
    var servers = TurnCredentialGenerator.CreateIceServers(
        turnHost,
        turnSecret,
        id,
        ttl: TimeSpan.FromHours(24),
        udpPort: udpPort,
        tcpPort: udpPort,
        tlsPort: tlsPort,
        fallbackTlsPort: fallbackPort);

    return Results.Ok(servers);
});

app.MapHub<SignalingHub>("/signaling");

Log.Information("RemoteDesk Signaling & TURN Broker running on http://0.0.0.0:{Port}", port);

app.Run();
