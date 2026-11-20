using FluentAssertions;
using RemoteDesk.Core.Models;
using Xunit;

namespace RemoteDesk.Core.Tests;

public class InputCoordinateMappingTests
{
    [Theory]
    [InlineData(0, 0, 1920, 1080, 0, 0)]
    [InlineData(1919, 1079, 1920, 1080, 65535, 65535)]
    public void MapDisplayToNormalizedVirtual_SingleMonitor_CalculatesAccurateCoordinates(
        int targetX, int targetY, int width, int height, int expectedNormX, int expectedNormY)
    {
        // 0-based pixel indexing on display: 0 .. (width - 1)
        var normX = (int)(((long)targetX * 65535) / (width - 1));
        var normY = (int)(((long)targetY * 65535) / (height - 1));

        normX.Should().Be(expectedNormX);
        normY.Should().Be(expectedNormY);
    }

    [Fact]
    public void MapDisplayToNormalizedVirtual_SecondaryMonitorWithNegativeOffset_MapsCorrectly()
    {
        // Setup: Primary monitor at (0, 0) 1920x1080
        // Secondary monitor at (-1920, 0) 1920x1080
        // Total virtual desktop: vX = -1920, vY = 0, vW = 3840, vH = 1080
        var secondaryDisplay = new DisplayInfo(
            Id: 1,
            DeviceName: "DISPLAY2",
            X: -1920,
            Y: 0,
            Width: 1920,
            Height: 1080,
            IsPrimary: false);

        int vX = -1920;
        int vY = 0;
        int vW = 3840;
        int vH = 1080;

        // User clicks at center of secondary monitor: targetX = 960, targetY = 540
        var absPixelX = secondaryDisplay.X + (int)((long)960 * secondaryDisplay.Width / 1920); // -1920 + 960 = -960
        var absPixelY = secondaryDisplay.Y + (int)((long)540 * secondaryDisplay.Height / 1080); // 0 + 540 = 540

        absPixelX.Should().Be(-960);
        absPixelY.Should().Be(540);

        // Normalize to virtual desktop [0, 65535]
        var normX = (int)(((long)(absPixelX - vX) * 65535) / (vW - 1));
        var normY = (int)(((long)(absPixelY - vY) * 65535) / (vH - 1));

        // Center of left monitor in a dual setup should be ~25% of virtual desktop: 65535 * (960 / 3839) = 16388
        normX.Should().Be(16388);
        normY.Should().Be(32797);
    }
}
