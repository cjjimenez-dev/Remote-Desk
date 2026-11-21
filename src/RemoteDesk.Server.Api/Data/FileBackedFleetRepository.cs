using System.Collections.Concurrent;
using System.Text.Json;
using RemoteDesk.Core.Models;
using Serilog;

namespace RemoteDesk.Server.Api.Data;

public sealed class FileBackedFleetRepository : IFleetRepository
{
    private static readonly TimeSpan StaleHeartbeatThreshold = TimeSpan.FromSeconds(60);

    private readonly string _storageFilePath;
    private readonly ConcurrentDictionary<string, DeviceRecord> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FleetCommand> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FleetAuditRecord> _auditLogs = [];
    private readonly object _fileLock = new();
    private readonly Serilog.ILogger _logger = Log.ForContext<FileBackedFleetRepository>();

    public FileBackedFleetRepository(string? storageFilePath = null)
    {
        _storageFilePath = storageFilePath ?? Path.Combine(AppContext.BaseDirectory, "fleet_store.json");
        LoadFromDisk();
    }

    public Task<DeviceRecord> RegisterOrUpdateDeviceAsync(DeviceRecord device, CancellationToken ct = default)
    {
        _devices.AddOrUpdate(device.DeviceId,
            _ =>
            {
                device.RegisteredAtUtc = DateTime.UtcNow;
                device.LastHeartbeatUtc = DateTime.UtcNow;
                device.Status = DeviceStatus.Online;
                return device;
            },
            (_, existing) =>
            {
                existing.Hostname = device.Hostname;
                existing.OperatingSystem = device.OperatingSystem;
                existing.AgentVersion = device.AgentVersion;
                existing.IpAddress = device.IpAddress;
                existing.TotalMemoryBytes = device.TotalMemoryBytes;
                existing.CpuModel = device.CpuModel;
                existing.PublicKey = device.PublicKey ?? existing.PublicKey;
                existing.Status = DeviceStatus.Online;
                existing.LastHeartbeatUtc = DateTime.UtcNow;
                if (device.Tags.Count > 0)
                {
                    existing.Tags = device.Tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                }
                return existing;
            });

        _logger.Information("Registered/Updated Fleet Device [{DeviceId}] ({Hostname})", device.DeviceId, device.Hostname);
        SaveToDisk();
        return Task.FromResult(_devices[device.DeviceId]);
    }

    public Task<bool> UpdateHeartbeatAsync(string deviceId, DeviceStatus status, string? currentViewer = null, CancellationToken ct = default)
    {
        if (_devices.TryGetValue(deviceId, out var dev))
        {
            dev.LastHeartbeatUtc = DateTime.UtcNow;
            dev.Status = status;
            dev.CurrentSessionViewer = currentViewer;
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task<FleetSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        RefreshStaleStatuses();
        var all = _devices.Values.ToList();

        var summary = new FleetSummary
        {
            TotalDevices = all.Count,
            OnlineDevices = all.Count(d => d.Status == DeviceStatus.Online),
            InSessionDevices = all.Count(d => d.Status == DeviceStatus.InSession),
            OfflineDevices = all.Count(d => d.Status == DeviceStatus.Offline)
        };

        return Task.FromResult(summary);
    }

    public Task<List<DeviceRecord>> GetDevicesAsync(string? tag = null, string? search = null, CancellationToken ct = default)
    {
        RefreshStaleStatuses();
        var query = _devices.Values.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(tag))
        {
            query = query.Where(d => d.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(d =>
                d.DeviceId.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                d.Hostname.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                d.IpAddress.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        return Task.FromResult(query.OrderByDescending(d => d.Status).ThenBy(d => d.Hostname).ToList());
    }

    public Task<DeviceRecord?> GetDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        RefreshStaleStatuses();
        _devices.TryGetValue(deviceId, out var dev);
        return Task.FromResult(dev);
    }

    public Task<bool> UpdateTagsAsync(string deviceId, List<string> tags, CancellationToken ct = default)
    {
        if (_devices.TryGetValue(deviceId, out var dev))
        {
            dev.Tags = tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            SaveToDisk();
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task<FleetCommand> QueueCommandAsync(FleetCommand command, CancellationToken ct = default)
    {
        command.CreatedAtUtc = DateTime.UtcNow;
        command.Status = FleetCommandStatus.Pending;
        _commands[command.CommandId] = command;

        _logger.Information("Queued Fleet Command [{CmdId}] type {Type} for Device [{DeviceId}]",
            command.CommandId, command.CommandType, command.DeviceId);

        SaveToDisk();
        return Task.FromResult(command);
    }

    public Task<List<FleetCommand>> GetPendingCommandsAsync(string deviceId, CancellationToken ct = default)
    {
        var pending = _commands.Values
            .Where(c => string.Equals(c.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase) && c.Status == FleetCommandStatus.Pending)
            .OrderBy(c => c.CreatedAtUtc)
            .ToList();

        foreach (var cmd in pending)
        {
            cmd.Status = FleetCommandStatus.Dispatched;
        }

        return Task.FromResult(pending);
    }

    public Task<bool> UpdateCommandResultAsync(string commandId, FleetCommandStatus status, string? resultOutput, CancellationToken ct = default)
    {
        if (_commands.TryGetValue(commandId, out var cmd))
        {
            cmd.Status = status;
            cmd.ExecutedAtUtc = DateTime.UtcNow;
            cmd.ResultOutput = resultOutput;
            SaveToDisk();
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task<List<FleetAuditRecord>> GetAuditLogsAsync(string? deviceId = null, int limit = 50, CancellationToken ct = default)
    {
        lock (_fileLock)
        {
            var query = _auditLogs.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                query = query.Where(a => string.Equals(a.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
            }
            return Task.FromResult(query.OrderByDescending(a => a.TimestampUtc).Take(limit).ToList());
        }
    }

    public Task RecordAuditAsync(FleetAuditRecord audit, CancellationToken ct = default)
    {
        lock (_fileLock)
        {
            _auditLogs.Add(audit);
            if (_auditLogs.Count > 1000)
            {
                _auditLogs.RemoveRange(0, _auditLogs.Count - 1000);
            }
        }
        SaveToDisk();
        return Task.CompletedTask;
    }

    private void RefreshStaleStatuses()
    {
        var now = DateTime.UtcNow;
        foreach (var dev in _devices.Values)
        {
            if (dev.Status != DeviceStatus.Offline && (now - dev.LastHeartbeatUtc) > StaleHeartbeatThreshold)
            {
                dev.Status = DeviceStatus.Offline;
                dev.CurrentSessionViewer = null;
            }
        }
    }

    private void SaveToDisk()
    {
        try
        {
            lock (_fileLock)
            {
                var payload = new FleetStorageSnapshot
                {
                    Devices = _devices.Values.ToList(),
                    Commands = _commands.Values.ToList(),
                    AuditLogs = _auditLogs.ToList()
                };

                var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_storageFilePath, json);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to persist fleet data to disk.");
        }
    }

    private void LoadFromDisk()
    {
        try
        {
            lock (_fileLock)
            {
                if (!File.Exists(_storageFilePath)) return;

                var json = File.ReadAllText(_storageFilePath);
                var payload = JsonSerializer.Deserialize<FleetStorageSnapshot>(json);
                if (payload != null)
                {
                    foreach (var d in payload.Devices) _devices[d.DeviceId] = d;
                    foreach (var c in payload.Commands) _commands[c.CommandId] = c;
                    _auditLogs.Clear();
                    _auditLogs.AddRange(payload.AuditLogs);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to load fleet data from disk.");
        }
    }

    private sealed class FleetStorageSnapshot
    {
        public List<DeviceRecord> Devices { get; set; } = [];
        public List<FleetCommand> Commands { get; set; } = [];
        public List<FleetAuditRecord> AuditLogs { get; set; } = [];
    }
}
