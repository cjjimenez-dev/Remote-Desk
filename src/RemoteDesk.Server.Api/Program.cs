using System.Text.Json;
using RemoteDesk.Core.Models;
using RemoteDesk.Server.Api.Data;
using RemoteDesk.Updater.Security;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [Server.Api] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();

var port = Environment.GetEnvironmentVariable("PORT") ?? "5001";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.AddSingleton<IFleetRepository, FileBackedFleetRepository>();

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

// Fleet Summary
app.MapGet("/api/fleet/summary", async (IFleetRepository repo, CancellationToken ct) =>
{
    var summary = await repo.GetSummaryAsync(ct);
    return Results.Ok(summary);
});

// List Devices (filter by tag or search)
app.MapGet("/api/fleet/devices", async (string? tag, string? search, IFleetRepository repo, CancellationToken ct) =>
{
    var list = await repo.GetDevicesAsync(tag, search, ct);
    return Results.Ok(list);
});

// Get Single Device
app.MapGet("/api/fleet/devices/{deviceId}", async (string deviceId, IFleetRepository repo, CancellationToken ct) =>
{
    var dev = await repo.GetDeviceAsync(deviceId, ct);
    return dev != null ? Results.Ok(dev) : Results.NotFound(new { Message = $"Device {deviceId} not found." });
});

// Register Device
app.MapPost("/api/fleet/devices/register", async (DeviceRecord device, IFleetRepository repo, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(device.DeviceId))
    {
        return Results.BadRequest(new { Message = "DeviceId is required." });
    }
    var saved = await repo.RegisterOrUpdateDeviceAsync(device, ct);
    await repo.RecordAuditAsync(new FleetAuditRecord
    {
        EventType = "DeviceRegistered",
        DeviceId = device.DeviceId,
        Initiator = device.Hostname,
        Details = $"Registered {device.OperatingSystem} (IP: {device.IpAddress})"
    }, ct);
    return Results.Ok(saved);
});

// Heartbeat
app.MapPost("/api/fleet/devices/{deviceId}/heartbeat", async (string deviceId, HeartbeatRequest req, IFleetRepository repo, CancellationToken ct) =>
{
    var updated = await repo.UpdateHeartbeatAsync(deviceId, req.Status, req.CurrentSessionViewer, ct);
    return updated ? Results.Ok() : Results.NotFound();
});

// Update Tags
app.MapPost("/api/fleet/devices/{deviceId}/tags", async (string deviceId, List<string> tags, IFleetRepository repo, CancellationToken ct) =>
{
    var updated = await repo.UpdateTagsAsync(deviceId, tags, ct);
    return updated ? Results.Ok() : Results.NotFound();
});

// Queue Command
app.MapPost("/api/fleet/devices/{deviceId}/commands", async (string deviceId, FleetCommand cmd, IFleetRepository repo, CancellationToken ct) =>
{
    cmd.DeviceId = deviceId;
    var queued = await repo.QueueCommandAsync(cmd, ct);
    await repo.RecordAuditAsync(new FleetAuditRecord
    {
        EventType = "CommandDispatched",
        DeviceId = deviceId,
        Initiator = "Administrator",
        Details = $"Command: {cmd.CommandType}"
    }, ct);
    return Results.Ok(queued);
});

// Poll Pending Commands
app.MapGet("/api/fleet/devices/{deviceId}/commands/pending", async (string deviceId, IFleetRepository repo, CancellationToken ct) =>
{
    var pending = await repo.GetPendingCommandsAsync(deviceId, ct);
    return Results.Ok(pending);
});

// Report Command Result
app.MapPost("/api/fleet/commands/{commandId}/result", async (string commandId, CommandResultRequest req, IFleetRepository repo, CancellationToken ct) =>
{
    var updated = await repo.UpdateCommandResultAsync(commandId, req.Status, req.ResultOutput, ct);
    return updated ? Results.Ok() : Results.NotFound();
});

// Audit Logs
app.MapGet("/api/fleet/audit", async (string? deviceId, int? limit, IFleetRepository repo, CancellationToken ct) =>
{
    var logs = await repo.GetAuditLogsAsync(deviceId, limit ?? 50, ct);
    return Results.Ok(logs);
});

app.MapPost("/api/fleet/audit", async (FleetAuditRecord audit, IFleetRepository repo, CancellationToken ct) =>
{
    await repo.RecordAuditAsync(audit, ct);
    return Results.Ok();
});

// --- Auto-Update Endpoints ---
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

app.MapGet("/api/updates/public-key", () => Results.Ok(new { PublicKey = Convert.ToBase64String(releasePubKey) }));

app.MapGet("/api/updates/check", (string? version) =>
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

app.MapGet("/api/updates/releases", () => Results.Ok(new[] { latestManifest }));

// Web Dashboard UI
app.MapGet("/", () => Results.Content(DashboardHtml, "text/html"));
app.MapGet("/dashboard", () => Results.Content(DashboardHtml, "text/html"));

Log.Information("RemoteDesk Fleet Management Server running on http://0.0.0.0:{Port}", port);
app.Run();

public record HeartbeatRequest(DeviceStatus Status, string? CurrentSessionViewer);
public record CommandResultRequest(FleetCommandStatus Status, string? ResultOutput);

public partial class Program
{
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
            <!-- Header -->
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

            <!-- Stats Metric Cards -->
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

            <!-- Search & Filter Controls -->
            <div class="flex flex-col sm:flex-row gap-3 items-center justify-between bg-slate-800/50 p-3 rounded-lg border border-slate-700">
                <div class="w-full sm:w-80">
                    <input type="text" id="searchInput" placeholder="Search by Hostname, ID or IP..." 
                           oninput="filterDevices()" 
                           class="w-full bg-slate-900 border border-slate-700 rounded px-3 py-1.5 text-xs text-white placeholder-slate-500 focus:outline-none focus:border-blue-500"/>
                </div>
                <div class="flex gap-2">
                    <button onclick="setFilter('all')" class="filter-btn px-2.5 py-1 rounded text-xs font-semibold bg-blue-600 text-white" data-status="all">All</button>
                    <button onclick="setFilter('online')" class="filter-btn px-2.5 py-1 rounded text-xs font-semibold bg-slate-700 text-slate-300 hover:bg-slate-600" data-status="online">Online</button>
                    <button onclick="setFilter('insession')" class="filter-btn px-2.5 py-1 rounded text-xs font-semibold bg-slate-700 text-slate-300 hover:bg-slate-600" data-status="insession">In Session</button>
                    <button onclick="setFilter('offline')" class="filter-btn px-2.5 py-1 rounded text-xs font-semibold bg-slate-700 text-slate-300 hover:bg-slate-600" data-status="offline">Offline</button>
                </div>
            </div>

            <!-- Devices Table -->
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
                                <th class="py-3 px-4">Tags</th>
                                <th class="py-3 px-4">Last Seen</th>
                                <th class="py-3 px-4 text-right">Actions</th>
                            </tr>
                        </thead>
                        <tbody id="deviceTableBody" class="divide-y divide-slate-700/60 font-mono">
                            <tr><td colspan="8" class="text-center py-8 text-slate-500">Loading fleet inventory...</td></tr>
                        </tbody>
                    </table>
                </div>
            </div>

            <!-- Audit Trail -->
            <div class="bg-slate-800/80 rounded-lg border border-slate-700 p-4">
                <h2 class="text-sm font-bold text-white mb-3 flex items-center justify-between">
                    <span>📋 Fleet Audit & Session History</span>
                    <button onclick="fetchAuditLogs()" class="text-xs text-blue-400 hover:underline font-normal">Refresh Logs</button>
                </h2>
                <div id="auditLogList" class="space-y-2 max-h-48 overflow-y-auto text-xs font-mono">
                    <div class="text-slate-500">Loading audit history...</div>
                </div>
            </div>
        </div>

        <script>
            let allDevices = [];
            let currentFilter = 'all';

            async function fetchFleetData() {
                try {
                    const [summaryRes, devicesRes] = await Promise.all([
                        fetch('/api/fleet/summary'),
                        fetch('/api/fleet/devices')
                    ]);
                    const summary = await summaryRes.json();
                    allDevices = await devicesRes.json();

                    document.getElementById('statTotal').innerText = summary.totalDevices;
                    document.getElementById('statOnline').innerText = summary.onlineDevices;
                    document.getElementById('statInSession').innerText = summary.inSessionDevices;
                    document.getElementById('statOffline').innerText = summary.offlineDevices;
                    document.getElementById('lastUpdated').innerText = 'Updated ' + new Date().toLocaleTimeString();

                    renderTable();
                } catch (e) {
                    console.error('Failed to load fleet data', e);
                }
            }

            function setFilter(status) {
                currentFilter = status;
                document.querySelectorAll('.filter-btn').forEach(btn => {
                    if (btn.getAttribute('data-status') === status) {
                        btn.className = 'filter-btn px-2.5 py-1 rounded text-xs font-semibold bg-blue-600 text-white';
                    } else {
                        btn.className = 'filter-btn px-2.5 py-1 rounded text-xs font-semibold bg-slate-700 text-slate-300 hover:bg-slate-600';
                    }
                });
                renderTable();
            }

            function filterDevices() {
                renderTable();
            }

            function renderTable() {
                const search = (document.getElementById('searchInput').value || '').toLowerCase();
                const filtered = allDevices.filter(d => {
                    if (currentFilter === 'online' && d.status !== 1) return false;
                    if (currentFilter === 'insession' && d.status !== 2) return false;
                    if (currentFilter === 'offline' && d.status !== 0) return false;

                    if (search) {
                        const match = (d.hostname || '').toLowerCase().includes(search) ||
                                      (d.deviceId || '').toLowerCase().includes(search) ||
                                      (d.ipAddress || '').toLowerCase().includes(search);
                        if (!match) return false;
                    }
                    return true;
                });

                const tbody = document.getElementById('deviceTableBody');
                if (filtered.length === 0) {
                    tbody.innerHTML = `<tr><td colspan="8" class="text-center py-8 text-slate-500 font-sans">No matching devices found in fleet.</td></tr>`;
                    return;
                }

                tbody.innerHTML = filtered.map(d => {
                    let badgeClass = 'badge-offline';
                    let statusText = 'Offline';
                    if (d.status === 1) { badgeClass = 'badge-online'; statusText = 'Online'; }
                    if (d.status === 2) { badgeClass = 'badge-insession'; statusText = 'In Session'; }

                    const tags = (d.tags || []).map(t => `<span class="px-1.5 py-0.5 rounded text-[10px] bg-slate-700 text-slate-300 mr-1">${t}</span>`).join('');
                    const mem = d.totalMemoryBytes > 0 ? (d.totalMemoryBytes / (1024*1024*1024)).toFixed(1) + ' GB' : '';

                    return `
                        <tr class="hover:bg-slate-700/30 transition">
                            <td class="py-3 px-4">
                                <span class="px-2 py-0.5 rounded text-[11px] font-semibold ${badgeClass}">${statusText}</span>
                            </td>
                            <td class="py-3 px-4 font-bold text-sky-400">${d.deviceId}</td>
                            <td class="py-3 px-4 font-sans font-semibold text-white">${d.hostname}</td>
                            <td class="py-3 px-4 font-sans text-slate-400">
                                <div>${d.operatingSystem || 'Windows'}</div>
                                <div class="text-[10px] text-slate-500 font-mono">${mem ? mem + ' RAM • ' : ''}${d.cpuModel || ''}</div>
                            </td>
                            <td class="py-3 px-4">${d.ipAddress || '—'}</td>
                            <td class="py-3 px-4 font-sans">${tags || '—'}</td>
                            <td class="py-3 px-4 text-slate-400 text-[11px]">${new Date(d.lastHeartbeatUtc).toLocaleTimeString()}</td>
                            <td class="py-3 px-4 text-right space-x-1 font-sans">
                                <button onclick="sendCommand('${d.deviceId}', 0)" title="Lock Workstation" class="px-2 py-1 bg-slate-700 hover:bg-slate-600 rounded text-slate-200 text-[11px] font-semibold">🔒 Lock</button>
                                <button onclick="sendCommand('${d.deviceId}', 1)" title="Reboot Device" class="px-2 py-1 bg-rose-900/60 hover:bg-rose-800 text-rose-300 rounded text-[11px] font-semibold">🔄 Reboot</button>
                            </td>
                        </tr>
                    `;
                }).join('');
            }

            async function sendCommand(deviceId, commandType) {
                const names = ['LockWorkstation', 'Reboot', 'RestartAgent', 'GetDiagnostics'];
                const name = names[commandType] || 'Command';
                if (!confirm(`Are you sure you want to dispatch [${name}] to device ${deviceId}?`)) return;

                try {
                    const res = await fetch(`/api/fleet/devices/${deviceId}/commands`, {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ commandType: commandType })
                    });
                    if (res.ok) {
                        alert(`Command [${name}] successfully queued for ${deviceId}!`);
                        fetchAuditLogs();
                    } else {
                        alert(`Failed to dispatch command.`);
                    }
                } catch (e) {
                    alert('Error dispatching command: ' + e);
                }
            }

            async function fetchAuditLogs() {
                try {
                    const res = await fetch('/api/fleet/audit?limit=25');
                    const logs = await res.json();
                    const container = document.getElementById('auditLogList');
                    if (logs.length === 0) {
                        container.innerHTML = `<div class="text-slate-500">No audit events recorded yet.</div>`;
                        return;
                    }
                    container.innerHTML = logs.map(l => `
                        <div class="flex items-center justify-between p-2 rounded bg-slate-900/60 border border-slate-700/50">
                            <div>
                                <span class="text-blue-400 font-semibold">[${l.eventType}]</span>
                                <span class="text-slate-300 ml-2">${l.details}</span>
                                <span class="text-slate-500 text-[10px] ml-2">Device: ${l.deviceId}</span>
                            </div>
                            <span class="text-slate-500 text-[10px]">${new Date(l.timestampUtc).toLocaleTimeString()}</span>
                        </div>
                    `).join('');
                } catch (e) {
                    console.error('Failed to fetch audit logs', e);
                }
            }

            fetchFleetData();
            fetchAuditLogs();
            setInterval(fetchFleetData, 5000);
            setInterval(fetchAuditLogs, 10000);
        </script>
    </body>
    </html>
    """;
}
