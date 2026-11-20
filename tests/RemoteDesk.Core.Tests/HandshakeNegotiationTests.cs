using FluentAssertions;
using RemoteDesk.Core.Models;
using RemoteDesk.Core.Protocol;
using Xunit;

namespace RemoteDesk.Core.Tests;

public class HandshakeNegotiationTests
{
    [Fact]
    public void Negotiate_MatchingVersions_ReturnsIntersectionOfCapabilities()
    {
        // Arrange
        var request = new HandshakeRequest
        {
            MinProtocolVersion = 1,
            MaxProtocolVersion = 1,
            AppVersion = "0.1.0",
            OsInfo = "Windows 11",
            Capabilities = [StandardCapabilities.VideoH264, StandardCapabilities.InputUnicode, "future.unsupported.feature"]
        };

        var hostCaps = new HashSet<string>
        {
            StandardCapabilities.VideoH264,
            StandardCapabilities.InputUnicode,
            StandardCapabilities.ClipboardText,
            StandardCapabilities.AudioOpus
        };

        // Act
        var response = ProtocolSerializer.Negotiate(
            request,
            hostCurrentVersion: 1,
            sessionId: "session-12345",
            hostSupportedCapabilities: hostCaps,
            initialPermissions: SessionPermissions.View | SessionPermissions.Control
        );

        // Assert
        response.Accepted.Should().BeTrue();
        response.AgreedProtocolVersion.Should().Be(1);
        response.SessionId.Should().Be("session-12345");
        response.AgreedCapabilities.Should().BeEquivalentTo([
            StandardCapabilities.VideoH264,
            StandardCapabilities.InputUnicode
        ]);
        response.AgreedCapabilities.Should().NotContain("future.unsupported.feature");
        response.GrantedPermissions.Should().HaveFlag(SessionPermissions.View);
        response.GrantedPermissions.Should().HaveFlag(SessionPermissions.Control);
        response.GrantedPermissions.Should().NotHaveFlag(SessionPermissions.Clipboard);
    }

    [Fact]
    public void Negotiate_IncompatibleVersion_ReturnsRejection()
    {
        // Arrange
        var request = new HandshakeRequest
        {
            MinProtocolVersion = 2,
            MaxProtocolVersion = 3,
            AppVersion = "2.0.0"
        };

        // Act
        var response = ProtocolSerializer.Negotiate(
            request,
            hostCurrentVersion: 1,
            sessionId: "session-12345",
            hostSupportedCapabilities: [StandardCapabilities.VideoH264],
            initialPermissions: SessionPermissions.View
        );

        // Assert
        response.Accepted.Should().BeFalse();
        response.RejectionReason.Should().Contain("Incompatible protocol version");
    }
}
