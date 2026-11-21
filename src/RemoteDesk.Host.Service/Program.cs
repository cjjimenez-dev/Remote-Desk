using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RemoteDesk.Host.Service;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [Host.Service] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

var serverUrl = "http://localhost:5000/signaling";
var forceRelay = false;

for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--server" && i + 1 < args.Length)
    {
        serverUrl = args[++i];
    }
    else if (args[i] == "--force-relay")
    {
        forceRelay = true;
    }
}

var host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options =>
    {
        options.ServiceName = "RemoteDesk.Host.Service";
    })
    .ConfigureServices(services =>
    {
        services.AddHostedService(_ => new HostBackgroundService(serverUrl, forceRelay));
    })
    .Build();

await host.RunAsync();
await Log.CloseAndFlushAsync();
