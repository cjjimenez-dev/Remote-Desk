using FluentAssertions;
using RemoteDesk.Core.Services;
using Xunit;

namespace RemoteDesk.Core.Tests.Services;

public class ServerEndpointParserTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_NullOrWhitespace_ReturnsLocalhostDefaults(string? input)
    {
        var config = ServerEndpointParser.Parse(input);

        config.Host.Should().Be("localhost");
        config.Port.Should().Be(5000);
        config.IsSecure.Should().BeFalse();
        config.SignalingUrl.Should().Be("http://localhost:5000/signaling");
        config.FleetApiUrl.Should().Be("http://localhost:5001");
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    public void Parse_LocalhostAliases_ReturnsStandardPorts(string input)
    {
        var config = ServerEndpointParser.Parse(input);

        config.Host.Should().Be(input);
        config.Port.Should().Be(5000);
        config.IsSecure.Should().BeFalse();
        config.SignalingUrl.Should().Be($"http://{input}:5000/signaling");
        config.FleetApiUrl.Should().Be($"http://{input}:5001");
    }

    [Theory]
    [InlineData("192.168.1.100")]
    [InlineData("10.0.0.15")]
    [InlineData("203.0.113.42")]
    public void Parse_RawIpWithoutPort_DefaultsToPort5000(string ip)
    {
        var config = ServerEndpointParser.Parse(ip);

        config.Host.Should().Be(ip);
        config.Port.Should().Be(5000);
        config.IsSecure.Should().BeFalse();
        config.SignalingUrl.Should().Be($"http://{ip}:5000/signaling");
        config.FleetApiUrl.Should().Be($"http://{ip}:5001");
    }

    [Theory]
    [InlineData("192.168.1.100:5000", "192.168.1.100", 5000, "http://192.168.1.100:5000/signaling", "http://192.168.1.100:5001")]
    [InlineData("10.0.0.5:8080", "10.0.0.5", 8080, "http://10.0.0.5:8080/signaling", "http://10.0.0.5:8080")]
    [InlineData("vps.example.com:5000", "vps.example.com", 5000, "http://vps.example.com:5000/signaling", "http://vps.example.com:5001")]
    public void Parse_HostWithPort_PreservesPort(string input, string expectedHost, int expectedPort, string expectedSig, string expectedFleet)
    {
        var config = ServerEndpointParser.Parse(input);

        config.Host.Should().Be(expectedHost);
        config.Port.Should().Be(expectedPort);
        config.IsSecure.Should().BeFalse();
        config.SignalingUrl.Should().Be(expectedSig);
        config.FleetApiUrl.Should().Be(expectedFleet);
    }

    [Theory]
    [InlineData("remotedesk.trycloudflare.com")]
    [InlineData("connect-agent.trycloudflare.com")]
    [InlineData("sub.cfargotunnel.com")]
    public void Parse_CloudflareTunnelHostname_DefaultsToHttpsAndPort443(string hostname)
    {
        var config = ServerEndpointParser.Parse(hostname);

        config.Host.Should().Be(hostname);
        config.Port.Should().Be(443);
        config.IsSecure.Should().BeTrue();
        config.SignalingUrl.Should().Be($"https://{hostname}/signaling");
        config.FleetApiUrl.Should().Be($"https://{hostname}");
    }

    [Theory]
    [InlineData("https://remotedesk.trycloudflare.com")]
    [InlineData("https://remotedesk.trycloudflare.com/")]
    [InlineData("https://remotedesk.trycloudflare.com/signaling")]
    public void Parse_FullHttpsCloudflareUrl_NormalizesCleanly(string url)
    {
        var config = ServerEndpointParser.Parse(url);

        config.Host.Should().Be("remotedesk.trycloudflare.com");
        config.IsSecure.Should().BeTrue();
        config.SignalingUrl.Should().Be("https://remotedesk.trycloudflare.com/signaling");
        config.FleetApiUrl.Should().Be("https://remotedesk.trycloudflare.com");
    }

    [Theory]
    [InlineData("relay.remotedesk.net")]
    [InlineData("remote.company.org")]
    public void Parse_PublicDomainWithoutPort_DefaultsToHttps(string domain)
    {
        var config = ServerEndpointParser.Parse(domain);

        config.Host.Should().Be(domain);
        config.Port.Should().Be(443);
        config.IsSecure.Should().BeTrue();
        config.SignalingUrl.Should().Be($"https://{domain}/signaling");
        config.FleetApiUrl.Should().Be($"https://{domain}");
    }

    [Fact]
    public void Parse_HttpUrlWithCustomPort_MapsSignalingAndFleetCorrectly()
    {
        var config = ServerEndpointParser.Parse("http://vps-server:5000/signaling");

        config.Host.Should().Be("vps-server");
        config.Port.Should().Be(5000);
        config.IsSecure.Should().BeFalse();
        config.SignalingUrl.Should().Be("http://vps-server:5000/signaling");
        config.FleetApiUrl.Should().Be("http://vps-server:5001");
    }
}
