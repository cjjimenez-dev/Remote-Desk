using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RemoteDesk.Core.Models;
using RemoteDesk.Core.Security;
using RemoteDesk.Server.Api.Data;
using RemoteDesk.Server.Signaling;
using RemoteDesk.Updater.Security;
using Serilog;

namespace RemoteDesk.Viewer.Servers;

public sealed class IntegratedServerManager : IAsyncDisposable
{
    private static readonly Serilog.ILogger Logger = Log.ForContext<IntegratedServerManager>();

    private WebApplication? _signalingApp;
    private WebApplication? _fleetApp;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public bool IsSignalingHostedLocally => _signalingApp != null;
    public bool IsFleetApiHostedLocally => _fleetApp != null;

    public async Task StartServersAsync()
    {
        await StartSignalingServerAsync().ConfigureAwait(false);
        await StartFleetApiServerAsync().ConfigureAwait(false);
    }

    private async Task StartSignalingServerAsync()
    {
        if (IsPortInUse(5000))
        {
            Logger.Information("Port 5000 is already in use by an active signaling server; using existing instance.");
            return;
        }

        try
        {
            Logger.Information("Starting integrated Signaling & TURN broker on port 5000...");
            var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
            {
                Args = []
            });

            builder.WebHost.UseKestrel(k =>
            {
                k.ListenAnyIP(5000);
            });

            builder.Services.AddSignalR(options =>
            {
                options.EnableDetailedErrors = true;
                options.MaximumReceiveMessageSize = 10 * 1024 * 1024;
            });

            builder.Services.AddCors(options =>
            {
                options.AddDefaultPolicy(p => p.AllowAnyHeader().AllowAnyMethod().SetIsOriginAllowed(_ => true).AllowCredentials());
            });

            builder.Services.AddRouting();

            _signalingApp = builder.Build();
            _signalingApp.UseCors();
            _signalingApp.UseRouting();

            _signalingApp.MapGet("/", () => Results.Ok(new
            {
                Service = "RemoteDesk Integrated Signaling & TURN Broker",
                Status = "Online",
                Version = "1.0",
                Timestamp = DateTime.UtcNow
            }));

            _signalingApp.MapGet("/api/ice-servers", (string? deviceId) =>
            {
                var turnHost = Environment.GetEnvironmentVariable("TURN_HOST") ?? "turn.remotedesk.net";
                var turnSecret = Environment.GetEnvironmentVariable("TURN_SECRET") ?? "RemoteDesk_Turn_Secret_Key_Change_In_Production_2026!";
                var udpPort = int.TryParse(Environment.GetEnvironmentVariable("TURN_UDP_PORT"), out var u) ? u : 3478;
                var tlsPort = int.TryParse(Environment.GetEnvironmentVariable("TURN_TLS_PORT"), out var t) ? t : 5349;
                var fallbackPort = int.TryParse(Environment.GetEnvironmentVariable("TURN_FALLBACK_PORT"), out var f) ? f : 443;

                var id = string.IsNullOrWhiteSpace(deviceId) ? Guid.NewGuid().ToString("N")[..8] : deviceId;
                var servers = TurnCredentialGenerator.CreateIceServers(
                    turnHost, turnSecret, id, TimeSpan.FromHours(24), udpPort, udpPort, tlsPort, fallbackPort);

                return Results.Ok(servers);
            });

            _signalingApp.MapHub<SignalingHub>("/signaling");

            _ = _signalingApp.RunAsync(_cts.Token);
            Logger.Information("Integrated Signaling server started successfully on http://0.0.0.0:5000/signaling");
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Could not start integrated signaling server on port 5000. Will use external signaling endpoint.");
            _signalingApp = null;
        }
    }

    private async Task StartFleetApiServerAsync()
    {
        if (IsPortInUse(5001))
        {
            Logger.Information("Port 5001 is already in use by an active fleet manager; using existing instance.");
            return;
        }

        try
        {
            Logger.Information("Starting integrated Fleet API & Web Dashboard on port 5001...");
            var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
            {
                Args = []
            });

            builder.WebHost.UseKestrel(k =>
            {
                k.ListenAnyIP(5001);
            });

            builder.Services.AddSingleton<IFleetRepository, FileBackedFleetRepository>();
            builder.Services.AddCors(options =>
            {
                options.AddDefaultPolicy(p => p.AllowAnyHeader().AllowAnyMethod().SetIsOriginAllowed(_ => true).AllowCredentials());
            });
            builder.Services.AddRouting();

            _fleetApp = builder.Build();
            _fleetApp.UseCors();
            _fleetApp.UseRouting();

            // Fleet API Endpoints
            _fleetApp.MapGet("/api/fleet/summary", async (IFleetRepository repo, CancellationToken ct) =>
                Results.Ok(await repo.GetSummaryAsync(ct)));

            _fleetApp.MapGet("/api/fleet/devices", async (string? tag, string? search, IFleetRepository repo, CancellationToken ct) =>
                Results.Ok(await repo.GetDevicesAsync(tag, search, ct)));

            _fleetApp.MapGet("/api/fleet/devices/{deviceId}", async (string deviceId, IFleetRepository repo, CancellationToken ct) =>
            {
                var dev = await repo.GetDeviceAsync(deviceId, ct);
                return dev != null ? Results.Ok(dev) : Results.NotFound();
            });

            _fleetApp.MapPost("/api/fleet/devices/register", async (DeviceRecord device, IFleetRepository repo, CancellationToken ct) =>
            {
                var saved = await repo.RegisterOrUpdateDeviceAsync(device, ct);
                return Results.Ok(saved);
            });

            _fleetApp.MapPost("/api/fleet/devices/{deviceId}/heartbeat", async (string deviceId, HeartbeatRequest req, IFleetRepository repo, CancellationToken ct) =>
            {
                var ok = await repo.UpdateHeartbeatAsync(deviceId, req.Status, req.CurrentSessionViewer, ct);
                return ok ? Results.Ok() : Results.NotFound();
            });

            _fleetApp.MapPost("/api/fleet/devices/{deviceId}/tags", async (string deviceId, List<string> tags, IFleetRepository repo, CancellationToken ct) =>
            {
                var ok = await repo.UpdateTagsAsync(deviceId, tags, ct);
                return ok ? Results.Ok() : Results.NotFound();
            });

            _fleetApp.MapPost("/api/fleet/devices/{deviceId}/commands", async (string deviceId, FleetCommand cmd, IFleetRepository repo, CancellationToken ct) =>
            {
                cmd.DeviceId = deviceId;
                var queued = await repo.QueueCommandAsync(cmd, ct);
                return Results.Ok(queued);
            });

            _fleetApp.MapGet("/api/fleet/devices/{deviceId}/commands/pending", async (string deviceId, IFleetRepository repo, CancellationToken ct) =>
                Results.Ok(await repo.GetPendingCommandsAsync(deviceId, ct)));

            _fleetApp.MapPost("/api/fleet/commands/{commandId}/result", async (string commandId, CommandResultRequest req, IFleetRepository repo, CancellationToken ct) =>
            {
                var ok = await repo.UpdateCommandResultAsync(commandId, req.Status, req.ResultOutput, ct);
                return ok ? Results.Ok() : Results.NotFound();
            });

            _fleetApp.MapGet("/api/fleet/audit", async (string? deviceId, int? limit, IFleetRepository repo, CancellationToken ct) =>
                Results.Ok(await repo.GetAuditLogsAsync(deviceId, limit ?? 50, ct)));

            _fleetApp.MapPost("/api/fleet/audit", async (FleetAuditRecord audit, IFleetRepository repo, CancellationToken ct) =>
            {
                await repo.RecordAuditAsync(audit, ct);
                return Results.Ok();
            });

            // Update Endpoints
            var (releasePubKey, releasePrivKey) = UpdateSigner.GenerateSigningKeyPair();
            var latestManifest = new UpdateReleaseManifest
            {
                Version = "1.1.0",
                ReleaseDateUtc = DateTime.UtcNow,
                DownloadUrl = "http://localhost:5001/api/updates/download/RemoteDesk-Setup-1.1.0.exe",
                Sha256Checksum = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                IsMandatory = false,
                ReleaseNotes = "RemoteDesk 1.1.0: Full Fleet Management, ConPTY Remote Terminal, Multi-Monitor & WASAPI Audio Loopback support.",
                PackageSizeBytes = 15 * 1024 * 1024,
                MinSupportedVersion = "1.0.0"
            };
            latestManifest.Signature = UpdateSigner.SignManifest(latestManifest.Version, latestManifest.Sha256Checksum, releasePrivKey);

            _fleetApp.MapGet("/api/updates/public-key", () => Results.Ok(new { PublicKey = Convert.ToBase64String(releasePubKey) }));
            _fleetApp.MapGet("/api/updates/check", (string? version) =>
            {
                if (string.IsNullOrWhiteSpace(version)) version = "1.0.0";
                bool updateAvailable = false;
                if (Version.TryParse(version, out var clientVer) && Version.TryParse(latestManifest.Version, out var latestVer))
                {
                    updateAvailable = latestVer > clientVer;
                }
                return Results.Ok(new UpdateCheckResult
                {
                    IsUpdateAvailable = updateAvailable,
                    LatestRelease = updateAvailable ? latestManifest : null,
                    CurrentVersion = version
                });
            });

            // Web Dashboard
            _fleetApp.MapGet("/", () => Results.Content(DashboardHtml, "text/html"));
            _fleetApp.MapGet("/dashboard", () => Results.Content(DashboardHtml, "text/html"));

            _ = _fleetApp.RunAsync(_cts.Token);
            Logger.Information("Integrated Fleet API & Web Dashboard started successfully on http://0.0.0.0:5001/dashboard");
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Could not start integrated fleet API on port 5001. Will use external endpoint.");
            _fleetApp = null;
        }
    }

    private static bool IsPortInUse(int port)
    {
        try
        {
            using var client = new TcpClient();
            var result = client.BeginConnect("127.0.0.1", port, null, null);
            bool success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(200));
            if (success && client.Connected)
            {
                client.EndConnect(result);
                return true;
            }
        }
        catch { }
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();

        if (_signalingApp != null)
        {
            try { await _signalingApp.StopAsync().ConfigureAwait(false); } catch { }
            try { await _signalingApp.DisposeAsync().ConfigureAwait(false); } catch { }
        }

        if (_fleetApp != null)
        {
            try { await _fleetApp.StopAsync().ConfigureAwait(false); } catch { }
            try { await _fleetApp.DisposeAsync().ConfigureAwait(false); } catch { }
        }

        _cts.Dispose();
        Logger.Information("Integrated servers stopped cleanly.");
    }

    private const string DashboardHtml = """
    <!DOCTYPE html>
    <html lang="en">
    <head>
        <meta charset="UTF-8">
        <meta name="viewport" content="width=device-width, initial-scale=1.0">
        <title>RemoteDesk - Fleet Dashboard</title>
        <script src="https://cdn.tailwindcss.com"></script>
        <style>
            body { background-color: #0F172A; color: #F8FAFC; font-family: ui-sans-serif, system-ui, sans-serif; }
            .badge-online { background-color: rgba(16, 185, 129, 0.2); color: #34D399; border: 1px solid rgba(16, 185, 129, 0.4); }
            .badge-insession { background-color: rgba(59, 130, 246, 0.2); color: #60A5FA; border: 1px solid rgba(59, 130, 246, 0.4); }
            .badge-offline { background-color: rgba(107, 114, 128, 0.2); color: #9CA3AF; border: 1px solid rgba(107, 114, 128, 0.4); }
        </style>
    </head>
    <body class="p-6">
        <div class="max-w-7xl mx-auto space-y-6">
            <div class="flex items-center justify-between border-b border-slate-700 pb-4">
                <div>
                    <h1 class="text-2xl font-bold tracking-tight text-white flex items-center gap-3">
                        <span class="text-blue-400">🌐 RemoteDesk</span>
                        <span class="text-xs px-2.5 py-1 rounded bg-blue-900/60 text-blue-300 font-semibold border border-blue-700">Fleet Management</span>
                    </h1>
                    <p class="text-xs text-slate-400 mt-1">Real-time inventory, telemetry, remote diagnostics and fleet control</p>
                </div>
                <div class="flex items-center gap-3">
                    <button onclick="fetchFleetData()" class="px-3 py-1.5 bg-slate-800 hover:bg-slate-700 text-slate-200 text-xs font-semibold rounded border border-slate-600 transition">
                        🔄 Refresh
                    </button>
                    <span id="lastUpdated" class="text-xs text-slate-500 font-mono">Updated just now</span>
                </div>
            </div>

            <div class="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
                <div class="bg-slate-800/80 p-4 rounded-lg border border-slate-700">
                    <div class="text-xs text-slate-400 font-medium">Total Hosts</div>
                    <div id="statTotal" class="text-2xl font-bold text-white mt-1">0</div>
                </div>
                <div class="bg-slate-800/80 p-4 rounded-lg border border-slate-700">
                    <div class="text-xs text-emerald-400 font-medium">🟢 Online & Ready</div>
                    <div id="statOnline" class="text-2xl font-bold text-emerald-400 mt-1">0</div>
                </div>
                <div class="bg-slate-800/80 p-4 rounded-lg border border-slate-700">
                    <div class="text-xs text-blue-400 font-medium">🔵 In Active Session</div>
                    <div id="statInSession" class="text-2xl font-bold text-blue-400 mt-1">0</div>
                </div>
                <div class="bg-slate-800/80 p-4 rounded-lg border border-slate-700">
                    <div class="text-xs text-slate-400 font-medium">⚪ Offline</div>
                    <div id="statOffline" class="text-2xl font-bold text-slate-400 mt-1">0</div>
                </div>
            </div>

            <div class="bg-slate-800/80 rounded-lg border border-slate-700 overflow-hidden">
                <div class="overflow-x-auto">
                    <table class="w-full text-left text-xs text-slate-300">
                        <thead class="bg-slate-900/80 text-slate-400 uppercase font-semibold text-[10px] tracking-wider border-b border-slate-700">
                            <tr>
                                <th class="py-3 px-4">Status</th>
                                <th class="py-3 px-4">Device ID</th>
                                <th class="py-3 px-4">Hostname</th>
                                <th class="py-3 px-4">OS / Specs</th>
                                <th class="py-3 px-4">IP Address</th>
                                <th class="py-3 px-4">Last Seen</th>
                            </tr>
                        </thead>
                        <tbody id="deviceTableBody" class="divide-y divide-slate-700/60 font-mono">
                            <tr><td colspan="6" class="text-center py-8 text-slate-500">Loading fleet inventory...</td></tr>
                        </tbody>
                    </table>
                </div>
            </div>
        </div>

        <script>
            async function fetchFleetData() {
                try {
                    const [summaryRes, devicesRes] = await Promise.all([
                        fetch('/api/fleet/summary'),
                        fetch('/api/fleet/devices')
                    ]);
                    const summary = await summaryRes.json();
                    const devices = await devicesRes.json();

                    document.getElementById('statTotal').innerText = summary.totalDevices;
                    document.getElementById('statOnline').innerText = summary.onlineDevices;
                    document.getElementById('statInSession').innerText = summary.inSessionDevices;
                    document.getElementById('statOffline').innerText = summary.offlineDevices;
                    document.getElementById('lastUpdated').innerText = 'Updated ' + new Date().toLocaleTimeString();

                    const tbody = document.getElementById('deviceTableBody');
                    if (devices.length === 0) {
                        tbody.innerHTML = `<tr><td colspan="6" class="text-center py-8 text-slate-500 font-sans">No matching devices found in fleet.</td></tr>`;
                        return;
                    }
                    tbody.innerHTML = devices.map(d => {
                        let badge = d.status === 1 ? 'badge-online' : (d.status === 2 ? 'badge-insession' : 'badge-offline');
                        let txt = d.status === 1 ? 'Online' : (d.status === 2 ? 'In Session' : 'Offline');
                        return `
                            <tr class="hover:bg-slate-700/30">
                                <td class="py-3 px-4"><span class="px-2 py-0.5 rounded text-[11px] font-semibold ${badge}">${txt}</span></td>
                                <td class="py-3 px-4 font-bold text-sky-400">${d.deviceId}</td>
                                <td class="py-3 px-4 font-semibold text-white">${d.hostname}</td>
                                <td class="py-3 px-4 text-slate-400">${d.operatingSystem || 'Windows'}</td>
                                <td class="py-3 px-4">${d.ipAddress || '—'}</td>
                                <td class="py-3 px-4 text-slate-400">${new Date(d.lastHeartbeatUtc).toLocaleTimeString()}</td>
                            </tr>
                        `;
                    }).join('');
                } catch (e) {
                    console.error('Failed to load fleet data', e);
                }
            }
            fetchFleetData();
            setInterval(fetchFleetData, 5000);
        </script>
    </body>
    </html>
    """;
}

public record HeartbeatRequest(DeviceStatus Status, string? CurrentSessionViewer);
public record CommandResultRequest(FleetCommandStatus Status, string? ResultOutput);
