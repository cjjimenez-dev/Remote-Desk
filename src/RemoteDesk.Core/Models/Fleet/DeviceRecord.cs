using MessagePack;

namespace RemoteDesk.Core.Models;

[MessagePackObject]
public sealed class DeviceRecord
{
    [Key(0)] public string DeviceId { get; set; } = string.Empty;
    [Key(1)] public string Hostname { get; set; } = string.Empty;
    [Key(2)] public string OperatingSystem { get; set; } = string.Empty;
    [Key(3)] public string AgentVersion { get; set; } = "1.0.0";
    [Key(4)] public string IpAddress { get; set; } = string.Empty;
    [Key(5)] public long TotalMemoryBytes { get; set; }
    [Key(6)] public string CpuModel { get; set; } = string.Empty;
    [Key(7)] public DeviceStatus Status { get; set; } = DeviceStatus.Offline;
    [Key(8)] public List<string> Tags { get; set; } = [];
    [Key(9)] public DateTime LastHeartbeatUtc { get; set; } = DateTime.UtcNow;
    [Key(10)] public DateTime RegisteredAtUtc { get; set; } = DateTime.UtcNow;
    [Key(11)] public byte[]? PublicKey { get; set; }
    [Key(12)] public string? CurrentSessionViewer { get; set; }

    [IgnoreMember]
    public string MemoryDisplay
    {
        get
        {
            if (TotalMemoryBytes <= 0) return "Unknown";
            double gb = (double)TotalMemoryBytes / (1024 * 1024 * 1024);
            return $"{gb:F1} GB";
        }
    }

    [IgnoreMember]
    public string StatusBadge => Status switch
    {
        DeviceStatus.Online => "🟢 Online",
        DeviceStatus.InSession => "🔵 In Session",
        _ => "⚪ Offline"
    };

    [IgnoreMember]
    public string StatusColor => Status switch
    {
        DeviceStatus.Online => "#10B981",
        DeviceStatus.InSession => "#3B82F6",
        _ => "#6B7280"
    };
}
