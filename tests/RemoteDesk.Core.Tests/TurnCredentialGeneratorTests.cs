using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using RemoteDesk.Core.Security;
using Xunit;

namespace RemoteDesk.Core.Tests;

public class TurnCredentialGeneratorTests
{
    [Fact]
    public void GenerateCredentials_ProducesValidHmacSha1()
    {
        var secret = "test_shared_secret_key_123";
        var deviceId = "device-abc-789";
        var ttl = TimeSpan.FromHours(1);

        var (username, credential, expiresAt) = TurnCredentialGenerator.GenerateCredentials(secret, deviceId, ttl);

        // Verify username format: <timestamp>:<deviceId>
        username.Should().Contain($":{deviceId}");
        var parts = username.Split(':');
        parts.Length.Should().Be(2);

        var timestamp = long.Parse(parts[0]);
        var expectedMin = DateTimeOffset.UtcNow.AddMinutes(59).ToUnixTimeSeconds();
        var expectedMax = DateTimeOffset.UtcNow.AddMinutes(61).ToUnixTimeSeconds();
        timestamp.Should().BeInRange(expectedMin, expectedMax);

        // Independently verify HMAC-SHA1
        var expectedHmac = HMACSHA1.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(username));
        var expectedCredential = Convert.ToBase64String(expectedHmac);

        credential.Should().Be(expectedCredential);
        expiresAt.ToUnixTimeSeconds().Should().Be(timestamp);
    }

    [Fact]
    public void CreateIceServers_ContainsStunAndTurnProtocols()
    {
        var turnHost = "turn.example.com";
        var secret = "secret_pass";
        var deviceId = "client-1";

        var servers = TurnCredentialGenerator.CreateIceServers(turnHost, secret, deviceId);

        servers.Should().NotBeNull();
        servers.Count.Should().BeGreaterThanOrEqualTo(6);

        // Check Google & Cloudflare Public Anycast STUN
        servers.Should().Contain(s => s.Urls.Any(u => u.Contains("stun:stun.l.google.com:19302")));
        servers.Should().Contain(s => s.Urls.Any(u => u.Contains("stun:stun.cloudflare.com:3478")));

        // Check Host STUN
        servers.Should().Contain(s => s.Urls.Any(u => u.StartsWith($"stun:{turnHost}:3478")));

        // Check TURN UDP
        servers.Should().Contain(s => s.Urls.Any(u => u.StartsWith($"turn:{turnHost}:3478?transport=udp")) &&
                                     !string.IsNullOrEmpty(s.Username) &&
                                     !string.IsNullOrEmpty(s.Credential));

        // Check TURN TCP
        servers.Should().Contain(s => s.Urls.Any(u => u.StartsWith($"turn:{turnHost}:3478?transport=tcp")) &&
                                     !string.IsNullOrEmpty(s.Username) &&
                                     !string.IsNullOrEmpty(s.Credential));

        // Check TURNS TLS on 5349
        servers.Should().Contain(s => s.Urls.Any(u => u.StartsWith($"turns:{turnHost}:5349?transport=tcp")) &&
                                     !string.IsNullOrEmpty(s.Username) &&
                                     !string.IsNullOrEmpty(s.Credential));

        // Check TURNS TLS fallback on 443
        servers.Should().Contain(s => s.Urls.Any(u => u.StartsWith($"turns:{turnHost}:443?transport=tcp")) &&
                                     !string.IsNullOrEmpty(s.Username) &&
                                     !string.IsNullOrEmpty(s.Credential));
    }

    [Fact]
    public void GetConfiguredIceServers_WithoutTurnEnv_ReturnsOnlyPublicStun()
    {
        var prevHost = Environment.GetEnvironmentVariable("TURN_HOST");
        var prevSecret = Environment.GetEnvironmentVariable("TURN_SECRET");
        try
        {
            Environment.SetEnvironmentVariable("TURN_HOST", null);
            Environment.SetEnvironmentVariable("TURN_SECRET", null);

            var servers = TurnCredentialGenerator.GetConfiguredIceServers("device-1");

            servers.Should().HaveCount(2);
            servers.Should().Contain(s => s.Urls.Any(u => u == "stun:stun.l.google.com:19302"));
            servers.Should().Contain(s => s.Urls.Any(u => u == "stun:stun.cloudflare.com:3478"));
            servers.Should().NotContain(s => s.Urls.Any(u => u.Contains("remotedesk.net")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TURN_HOST", prevHost);
            Environment.SetEnvironmentVariable("TURN_SECRET", prevSecret);
        }
    }
}
