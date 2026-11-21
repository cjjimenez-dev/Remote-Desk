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
    <html lang="en" class="dark">
    <head>
        <meta charset="UTF-8">
        <meta name="viewport" content="width=device-width, initial-scale=1.0">
        <title>RemoteDesk — Fleet Operations</title>
        <link rel="icon" type="image/png" href="data:image/svg+xml,<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 32 32'><rect width='32' height='32' rx='8' fill='%236366F1'/><path d='M8 12h16v10H8z' fill='%2309090B'/><path d='M14 9l4 4-4 4' stroke='%2338BDF8' stroke-width='2' fill='none'/></svg>">
        <script src="https://cdn.tailwindcss.com"></script>
        <script>
            tailwind.config = {
                darkMode: 'class',
                theme: {
                    extend: {
                        colors: {
                            zinc: {
                                950: '#09090B',
                                900: '#121215',
                                850: '#18181D',
                                800: '#22222A',
                                700: '#2E2E38'
                            }
                        }
                    }
                }
            }
        </script>
        <style>
            body { 
                background-color: #09090B; 
                color: #F4F4F5; 
                font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, "Helvetica Neue", sans-serif;
                -webkit-font-smoothing: antialiased;
            }
            .emil-card {
                background: #121215;
                border: 1px solid rgba(255, 255, 255, 0.07);
                border-radius: 12px;
                transition: border-color 0.15s ease;
            }
            .emil-card:hover {
                border-color: rgba(255, 255, 255, 0.12);
            }
            .badge-online { background-color: rgba(16, 185, 129, 0.12); color: #34D399; border: 1px solid rgba(16, 185, 129, 0.25); }
            .badge-insession { background-color: rgba(59, 130, 246, 0.12); color: #60A5FA; border: 1px solid rgba(59, 130, 246, 0.25); }
            .badge-offline { background-color: rgba(113, 113, 122, 0.12); color: #A1A1AA; border: 1px solid rgba(113, 113, 122, 0.25); }
        </style>
    </head>
    <body class="p-8 max-w-7xl mx-auto space-y-8">
        <!-- Header -->
        <header class="flex items-center justify-between pb-6 border-b border-zinc-800">
            <div class="flex items-center gap-4">
                <div class="w-10 h-10 rounded-xl bg-gradient-to-br from-indigo-500 to-cyan-500 p-[1px] shadow-lg shadow-indigo-500/20">
                    <div class="w-full h-full bg-zinc-950 rounded-[11px] flex items-center justify-center">
                        <svg class="w-5 h-5 text-cyan-400" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                            <rect x="2" y="3" width="20" height="14" rx="2"/>
                            <line x1="8" y1="21" x2="16" y2="21"/>
                            <line x1="12" y1="17" x2="12" y2="21"/>
                        </svg>
                    </div>
                </div>
                <div>
                    <div class="flex items-center gap-2">
                        <h1 class="text-xl font-bold tracking-tight text-white">RemoteDesk</h1>
                        <span class="px-2 py-0.5 text-[10px] font-bold rounded-full bg-zinc-800 text-zinc-400 border border-zinc-700">FLEET PRO</span>
                    </div>
                    <p class="text-xs text-zinc-400 mt-0.5">Real-time device inventory, telemetry, and distributed management</p>
                </div>
            </div>

            <div class="flex items-center gap-3">
                <button onclick="fetchFleetData()" class="px-3 py-1.5 bg-zinc-900 hover:bg-zinc-800 text-zinc-300 text-xs font-semibold rounded-lg border border-zinc-800 hover:border-zinc-700 transition flex items-center gap-2 shadow-sm">
                    <span>🔄 Refresh</span>
                    <kbd class="px-1.5 py-0.5 text-[10px] bg-zinc-950 text-zinc-500 rounded border border-zinc-800">F5</kbd>
                </button>
                <div class="flex items-center gap-2 px-3 py-1.5 bg-zinc-900/60 rounded-lg border border-zinc-800/80">
                    <span class="relative flex h-2 w-2">
                        <span class="animate-ping absolute inline-flex h-full w-full rounded-full bg-emerald-400 opacity-75"></span>
                        <span class="relative inline-flex rounded-full h-2 w-2 bg-emerald-500"></span>
                    </span>
                    <span id="lastUpdated" class="text-xs text-zinc-400 font-mono">Live</span>
                </div>
            </div>
        </header>

        <!-- KPI Cards -->
        <section class="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
            <div class="emil-card p-5">
                <div class="text-xs text-zinc-400 font-medium">Total Managed Hosts</div>
                <div id="statTotal" class="text-3xl font-extrabold text-white mt-2 tracking-tight">0</div>
            </div>
            <div class="emil-card p-5">
                <div class="flex items-center justify-between">
                    <div class="text-xs text-emerald-400 font-medium">Online & Ready</div>
                    <span class="w-2 h-2 rounded-full bg-emerald-500"></span>
                </div>
                <div id="statOnline" class="text-3xl font-extrabold text-emerald-400 mt-2 tracking-tight">0</div>
            </div>
            <div class="emil-card p-5">
                <div class="flex items-center justify-between">
                    <div class="text-xs text-blue-400 font-medium">In Active Session</div>
                    <span class="w-2 h-2 rounded-full bg-blue-500"></span>
                </div>
                <div id="statInSession" class="text-3xl font-extrabold text-blue-400 mt-2 tracking-tight">0</div>
            </div>
            <div class="emil-card p-5">
                <div class="flex items-center justify-between">
                    <div class="text-xs text-zinc-500 font-medium">Offline</div>
                    <span class="w-2 h-2 rounded-full bg-zinc-600"></span>
                </div>
                <div id="statOffline" class="text-3xl font-extrabold text-zinc-400 mt-2 tracking-tight">0</div>
            </div>
        </section>

        <!-- Inventory Table -->
        <section class="emil-card overflow-hidden">
            <div class="px-6 py-4 border-b border-zinc-800/80 flex items-center justify-between">
                <h2 class="text-sm font-semibold text-white">Managed Endpoints</h2>
                <span class="text-xs text-zinc-500">Auto-refreshes every 5s</span>
            </div>
            <div class="overflow-x-auto">
                <table class="w-full text-left text-xs text-zinc-300">
                    <thead class="bg-zinc-950/60 text-zinc-400 uppercase font-semibold text-[10px] tracking-wider border-b border-zinc-800">
                        <tr>
                            <th class="py-3.5 px-6">Status</th>
                            <th class="py-3.5 px-6">Device ID</th>
                            <th class="py-3.5 px-6">Hostname</th>
                            <th class="py-3.5 px-6">Operating System</th>
                            <th class="py-3.5 px-6">IP Address</th>
                            <th class="py-3.5 px-6">Last Heartbeat</th>
                        </tr>
                    </thead>
                    <tbody id="deviceTableBody" class="divide-y divide-zinc-800/60 font-mono">
                        <tr><td colspan="6" class="text-center py-10 text-zinc-500 font-sans">Connecting to Fleet telemetry service...</td></tr>
                    </tbody>
                </table>
            </div>
        </section>

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
                        tbody.innerHTML = `<tr><td colspan="6" class="text-center py-12 text-zinc-500 font-sans">No host devices registered in fleet yet.</td></tr>`;
                        return;
                    }
                    tbody.innerHTML = devices.map(d => {
                        let badge = d.status === 1 ? 'badge-online' : (d.status === 2 ? 'badge-insession' : 'badge-offline');
                        let txt = d.status === 1 ? 'Online' : (d.status === 2 ? 'In Session' : 'Offline');
                        return `
                            <tr class="hover:bg-zinc-800/20 transition">
                                <td class="py-3.5 px-6"><span class="px-2.5 py-1 rounded-full text-[11px] font-semibold ${badge}">${txt}</span></td>
                                <td class="py-3.5 px-6 font-bold text-sky-400">${d.deviceId}</td>
                                <td class="py-3.5 px-6 font-semibold text-white font-sans">${d.hostname}</td>
                                <td class="py-3.5 px-6 text-zinc-400 font-sans">${d.operatingSystem || 'Windows'}</td>
                                <td class="py-3.5 px-6">${d.ipAddress || '—'}</td>
                                <td class="py-3.5 px-6 text-zinc-400">${new Date(d.lastHeartbeatUtc).toLocaleTimeString()}</td>
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
