using FluentAssertions;
using RemoteDesk.Core.Models;
using RemoteDesk.Core.Protocol;
using Xunit;

namespace RemoteDesk.Core.Tests.Fleet;

public class FleetProtocolAndServiceTests
{
    [Fact]
    public void DeviceRecord_SerializationRoundTrip_Succeeds()
    {
        var device = new DeviceRecord
        {
            DeviceId = "host-test-01",
            Hostname = "FINANCE-PC01",
            OperatingSystem = "Windows 11 Pro 23H2",
            AgentVersion = "1.0.0",
            IpAddress = "192.168.1.150",
            TotalMemoryBytes = 16L * 1024 * 1024 * 1024,
            CpuModel = "Intel Core i7-13700",
            Status = DeviceStatus.Online,
            Tags = ["Finance", "Floor-2"],
            LastHeartbeatUtc = DateTime.UtcNow,
            RegisteredAtUtc = DateTime.UtcNow,
            PublicKey = [1, 2, 3, 4]
        };

        var bytes = ProtocolSerializer.Serialize(device);
        var deser = ProtocolSerializer.Deserialize<DeviceRecord>(bytes);

        deser.DeviceId.Should().Be("host-test-01");
        deser.Hostname.Should().Be("FINANCE-PC01");
        deser.OperatingSystem.Should().Be("Windows 11 Pro 23H2");
        deser.IpAddress.Should().Be("192.168.1.150");
        deser.TotalMemoryBytes.Should().Be(16L * 1024 * 1024 * 1024);
        deser.MemoryDisplay.Should().Be("16.0 GB");
        deser.Status.Should().Be(DeviceStatus.Online);
        deser.StatusBadge.Should().Be("🟢 Online");
        deser.Tags.Should().Contain("Finance");
        deser.PublicKey.Should().Equal(new byte[] { 1, 2, 3, 4 });
    }

    [Fact]
    public void FleetCommand_SerializationRoundTrip_Succeeds()
    {
        var cmd = new FleetCommand
        {
            CommandId = "cmd-001",
            DeviceId = "host-test-01",
            CommandType = FleetCommandType.LockWorkstation,
            Payload = "immediate",
            Status = FleetCommandStatus.Pending
        };

        var bytes = ProtocolSerializer.Serialize(cmd);
        var deser = ProtocolSerializer.Deserialize<FleetCommand>(bytes);

        deser.CommandId.Should().Be("cmd-001");
        deser.DeviceId.Should().Be("host-test-01");
        deser.CommandType.Should().Be(FleetCommandType.LockWorkstation);
        deser.Payload.Should().Be("immediate");
        deser.Status.Should().Be(FleetCommandStatus.Pending);
    }

    [Fact]
    public void FleetAuditRecord_SerializationRoundTrip_Succeeds()
    {
        var audit = new FleetAuditRecord
        {
            Id = "aud-001",
            EventType = "SessionConnected",
            DeviceId = "host-test-01",
            Initiator = "AdminUser",
            Details = "Session 8a9b started",
            Success = true
        };

        var bytes = ProtocolSerializer.Serialize(audit);
        var deser = ProtocolSerializer.Deserialize<FleetAuditRecord>(bytes);

        deser.Id.Should().Be("aud-001");
        deser.EventType.Should().Be("SessionConnected");
        deser.DeviceId.Should().Be("host-test-01");
        deser.Initiator.Should().Be("AdminUser");
        deser.Success.Should().BeTrue();
    }

    [Fact]
    public void FleetSummary_Calculations_AreAccurate()
    {
        var summary = new FleetSummary
        {
            TotalDevices = 10,
            OnlineDevices = 6,
            InSessionDevices = 2,
            OfflineDevices = 2
        };

        var bytes = ProtocolSerializer.Serialize(summary);
        var deser = ProtocolSerializer.Deserialize<FleetSummary>(bytes);

        deser.TotalDevices.Should().Be(10);
        deser.OnlineDevices.Should().Be(6);
        deser.InSessionDevices.Should().Be(2);
        deser.OfflineDevices.Should().Be(2);
    }
}
