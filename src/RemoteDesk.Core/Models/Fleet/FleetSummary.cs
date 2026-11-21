using MessagePack;

namespace RemoteDesk.Core.Models;

[MessagePackObject]
public sealed class FleetSummary
{
    [Key(0)] public int TotalDevices { get; set; }
    [Key(1)] public int OnlineDevices { get; set; }
    [Key(2)] public int InSessionDevices { get; set; }
    [Key(3)] public int OfflineDevices { get; set; }
}
