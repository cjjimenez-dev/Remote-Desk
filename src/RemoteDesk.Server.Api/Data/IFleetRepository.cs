using RemoteDesk.Core.Models;

namespace RemoteDesk.Server.Api.Data;

public interface IFleetRepository
{
    Task<DeviceRecord> RegisterOrUpdateDeviceAsync(DeviceRecord device, CancellationToken ct = default);
    Task<bool> UpdateHeartbeatAsync(string deviceId, DeviceStatus status, string? currentViewer = null, CancellationToken ct = default);
    Task<FleetSummary> GetSummaryAsync(CancellationToken ct = default);
    Task<List<DeviceRecord>> GetDevicesAsync(string? tag = null, string? search = null, CancellationToken ct = default);
    Task<DeviceRecord?> GetDeviceAsync(string deviceId, CancellationToken ct = default);
    Task<bool> UpdateTagsAsync(string deviceId, List<string> tags, CancellationToken ct = default);
    Task<FleetCommand> QueueCommandAsync(FleetCommand command, CancellationToken ct = default);
    Task<List<FleetCommand>> GetPendingCommandsAsync(string deviceId, CancellationToken ct = default);
    Task<bool> UpdateCommandResultAsync(string commandId, FleetCommandStatus status, string? resultOutput, CancellationToken ct = default);
    Task<List<FleetAuditRecord>> GetAuditLogsAsync(string? deviceId = null, int limit = 50, CancellationToken ct = default);
    Task RecordAuditAsync(FleetAuditRecord audit, CancellationToken ct = default);
}
