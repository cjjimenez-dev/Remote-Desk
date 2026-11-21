using FluentAssertions;
using RemoteDesk.Platform.Windows.Session;
using Xunit;

namespace RemoteDesk.Core.Tests.Session;

public class DesktopDetectorTests
{
    [Fact]
    public void QueryActiveInputDesktop_ReturnsValidDesktopName()
    {
        var (desktopName, isSecure) = DesktopDetector.QueryActiveInputDesktop();

        desktopName.Should().NotBeNullOrWhiteSpace();
        // Under standard Windows execution, this should be "Default" or "Winlogon"
        desktopName.Should().Match(name => name == "Default" || name == "Winlogon" || name == "Unknown");
    }

    [Fact]
    public void DesktopDetector_StartsAndStopsGracefully()
    {
        using var detector = new DesktopDetector(TimeSpan.FromMilliseconds(50));
        detector.Start();

        detector.CurrentDesktop.Should().NotBeNullOrWhiteSpace();
    }
}
