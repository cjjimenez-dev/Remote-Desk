using FluentAssertions;
using RemoteDesk.Core.Protocol;
using RemoteDesk.Core.Quality;
using Xunit;

namespace RemoteDesk.Core.Tests.Quality;

public class AdaptiveQualityTests
{
    [Fact]
    public void ApplyPreset_SetsTargetFpsAndBitrate_Accurately()
    {
        var controller = new AdaptiveQualityController(QualityPreset.Balanced);
        controller.CurrentPreset.Should().Be(QualityPreset.Balanced);
        controller.TargetFps.Should().Be(30);
        controller.CurrentBitrateKbps.Should().Be(4000);

        controller.ApplyPreset(QualityPreset.Speed);
        controller.CurrentPreset.Should().Be(QualityPreset.Speed);
        controller.TargetFps.Should().Be(60);
        controller.CurrentBitrateKbps.Should().Be(2000);

        controller.ApplyPreset(QualityPreset.Quality);
        controller.CurrentPreset.Should().Be(QualityPreset.Quality);
        controller.TargetFps.Should().Be(30);
        controller.CurrentBitrateKbps.Should().Be(8000);
    }

    [Fact]
    public void ProcessMetrics_OnHighPacketLoss_ReducesBitrate()
    {
        var controller = new AdaptiveQualityController(QualityPreset.Balanced);
        int initialBitrate = controller.CurrentBitrateKbps;

        var congestionReport = new QualityMetricsReportMessage
        {
            PacketLossPercent = 8.5,
            RttMs = 250,
            CurrentFps = 28,
            CurrentBitrateKbps = initialBitrate
        };

        var update = controller.ProcessMetrics(congestionReport);

        update.TargetBitrateKbps.Should().BeLessThan(initialBitrate);
        controller.CurrentBitrateKbps.Should().BeLessThan(initialBitrate);
    }

    [Fact]
    public void ProcessMetrics_OnSustainedHealthyConnection_ProbesHigherBitrate()
    {
        var controller = new AdaptiveQualityController(QualityPreset.Balanced);
        int initialBitrate = controller.CurrentBitrateKbps;

        var healthyReport = new QualityMetricsReportMessage
        {
            PacketLossPercent = 0.1,
            RttMs = 25,
            CurrentFps = 30,
            CurrentBitrateKbps = initialBitrate
        };

        // Report 3 times to trigger probing
        controller.ProcessMetrics(healthyReport);
        controller.ProcessMetrics(healthyReport);
        var update = controller.ProcessMetrics(healthyReport);

        update.TargetBitrateKbps.Should().BeGreaterThan(initialBitrate);
        controller.CurrentBitrateKbps.Should().BeGreaterThan(initialBitrate);
    }
}
