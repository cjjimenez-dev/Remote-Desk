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
    Service = "RemoteDesk Signaling Server",
    Status = "Online",
    Version = "1.0",
    Timestamp = DateTime.UtcNow
}));

app.MapHub<SignalingHub>("/signaling");

Log.Information("RemoteDesk Signaling Server running on http://0.0.0.0:{Port}/signaling", port);

app.Run();
