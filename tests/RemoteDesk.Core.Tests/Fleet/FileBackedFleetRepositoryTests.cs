using FluentAssertions;
using RemoteDesk.Core.Models;
using RemoteDesk.Server.Api.Data;
using Xunit;

namespace RemoteDesk.Core.Tests.Fleet;

public class FileBackedFleetRepositoryTests : IDisposable
{
    private readonly string _tempFile;
    private readonly FileBackedFleetRepository _repo;

    public FileBackedFleetRepositoryTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"fleet_test_{Guid.NewGuid():N}.json");
        _repo = new FileBackedFleetRepository(_tempFile);
    }

    [Fact]
    public async Task RegisterAndGetDevices_Succeeds()
    {
        var dev1 = new DeviceRecord
        {
            DeviceId = "host-01",
            Hostname = "FINANCE-PC",
            OperatingSystem = "Windows 11",
            IpAddress = "10.0.0.10",
            Tags = ["Finance", "HQ"]
        };
        var dev2 = new DeviceRecord
        {
            DeviceId = "host-02",
            Hostname = "DEV-PC",
            OperatingSystem = "Windows 11",
            IpAddress = "10.0.0.20",
            Tags = ["Engineering"]
        };

        await _repo.RegisterOrUpdateDeviceAsync(dev1);
        await _repo.RegisterOrUpdateDeviceAsync(dev2);

        var all = await _repo.GetDevicesAsync();
        all.Should().HaveCount(2);

        // Filter by tag
        var finance = await _repo.GetDevicesAsync(tag: "Finance");
        finance.Should().HaveCount(1);
        finance[0].DeviceId.Should().Be("host-01");

        // Filter by search
        var searchDev = await _repo.GetDevicesAsync(search: "DEV");
        searchDev.Should().HaveCount(1);
        searchDev[0].DeviceId.Should().Be("host-02");

        // Summary
        var summary = await _repo.GetSummaryAsync();
        summary.TotalDevices.Should().Be(2);
        summary.OnlineDevices.Should().Be(2);
    }

    [Fact]
    public async Task CommandQueue_Lifecycle_Succeeds()
    {
        var cmd = new FleetCommand
        {
            DeviceId = "host-01",
            CommandType = FleetCommandType.LockWorkstation
        };

        var queued = await _repo.QueueCommandAsync(cmd);
        queued.Status.Should().Be(FleetCommandStatus.Pending);

        // Fetch pending
        var pending = await _repo.GetPendingCommandsAsync("host-01");
        pending.Should().HaveCount(1);
        pending[0].Status.Should().Be(FleetCommandStatus.Dispatched);

        // Report result
        var updated = await _repo.UpdateCommandResultAsync(queued.CommandId, FleetCommandStatus.Completed, "Locked successfully");
        updated.Should().BeTrue();

        // No more pending
        var pendingAfter = await _repo.GetPendingCommandsAsync("host-01");
        pendingAfter.Should().BeEmpty();
    }

    [Fact]
    public async Task AuditLogging_RecordsAndFilters_Succeeds()
    {
        await _repo.RecordAuditAsync(new FleetAuditRecord
        {
            EventType = "Login",
            DeviceId = "host-01",
            Initiator = "Admin",
            Details = "Success"
        });
        await _repo.RecordAuditAsync(new FleetAuditRecord
        {
            EventType = "Reboot",
            DeviceId = "host-02",
            Initiator = "Admin",
            Details = "Rebooted"
        });

        var allLogs = await _repo.GetAuditLogsAsync(limit: 10);
        allLogs.Should().HaveCount(2);

        var dev1Logs = await _repo.GetAuditLogsAsync(deviceId: "host-01", limit: 10);
        dev1Logs.Should().HaveCount(1);
        dev1Logs[0].EventType.Should().Be("Login");
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile))
        {
            try { File.Delete(_tempFile); } catch { }
        }
    }
}
