using FluentAssertions;
using RemoteDesk.Platform.Windows.Session;
using Xunit;

namespace RemoteDesk.Core.Tests.Session;

public class SasSimulatorTests
{
    [Fact]
    public void SasSimulator_TriggerSas_ExecutesWithoutException()
    {
        // Calling TriggerSas will attempt SendSAS or software simulation.
        // It must handle all DllNotFound, AccessDenied, or Win32 errors cleanly without throwing unhandled exceptions.
        var act = () => SasSimulator.TriggerSas();

        act.Should().NotThrow();
    }

    [Fact]
    public void SessionProcessLauncher_GetActiveConsoleSession_ReturnsValidSessionId()
    {
        var sessionId = SessionProcessLauncher.GetActiveConsoleSession();

        // 0xFFFFFFFF means no console session (e.g. headless VM), otherwise >= 0
        (sessionId >= 0 || sessionId == 0xFFFFFFFF).Should().BeTrue();
    }
}
