using MessagePack;

namespace RemoteDesk.Core.Models;

public enum FleetCommandType
{
    LockWorkstation = 0,
    Reboot = 1,
    RestartAgent = 2,
    GetDiagnostics = 3,
    CustomScript = 4
}

public enum FleetCommandStatus
{
    Pending = 0,
    Dispatched = 1,
    Completed = 2,
    Failed = 3
}

[MessagePackObject]
public sealed class FleetCommand
{
    [Key(0)] public string CommandId { get; set; } = Guid.NewGuid().ToString("N")[..8];
    [Key(1)] public string DeviceId { get; set; } = string.Empty;
    [Key(2)] public FleetCommandType CommandType { get; set; }
    [Key(3)] public string? Payload { get; set; }
    [Key(4)] public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    [Key(5)] public DateTime? ExecutedAtUtc { get; set; }
    [Key(6)] public FleetCommandStatus Status { get; set; } = FleetCommandStatus.Pending;
    [Key(7)] public string? ResultOutput { get; set; }
}
