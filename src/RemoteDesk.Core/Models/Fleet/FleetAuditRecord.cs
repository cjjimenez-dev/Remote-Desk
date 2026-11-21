using MessagePack;

namespace RemoteDesk.Core.Models;

[MessagePackObject]
public sealed class FleetAuditRecord
{
    [Key(0)] public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    [Key(1)] public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    [Key(2)] public string EventType { get; set; } = string.Empty;
    [Key(3)] public string DeviceId { get; set; } = string.Empty;
    [Key(4)] public string Initiator { get; set; } = string.Empty;
    [Key(5)] public string Details { get; set; } = string.Empty;
    [Key(6)] public bool Success { get; set; } = true;
}
