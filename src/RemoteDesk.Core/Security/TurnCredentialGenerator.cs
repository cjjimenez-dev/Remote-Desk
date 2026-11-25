using System.Security.Cryptography;
using System.Text;
using RemoteDesk.Core.Models;

namespace RemoteDesk.Core.Security;

public static class TurnCredentialGenerator
{
    public static (string Username, string Credential, DateTimeOffset ExpiresAt) GenerateCredentials(
        string sharedSecret,
        string deviceId,
        TimeSpan ttl)
    {
        if (string.IsNullOrWhiteSpace(sharedSecret))
            throw new ArgumentException("Shared secret must not be empty.", nameof(sharedSecret));

        var expiresAt = DateTimeOffset.UtcNow.Add(ttl);
        var timestamp = expiresAt.ToUnixTimeSeconds();
        var username = $"{timestamp}:{deviceId}";

        var keyBytes = Encoding.UTF8.GetBytes(sharedSecret);
        var dataBytes = Encoding.UTF8.GetBytes(username);

        var hash = HMACSHA1.HashData(keyBytes, dataBytes);
        var credential = Convert.ToBase64String(hash);

        return (username, credential, expiresAt);
    }

    /// <summary>
    /// Builds ICE server configuration from environment variables when a TURN server is configured,
    /// otherwise returns only public Anycast STUN servers (no non-existent placeholder TURN hosts).
    /// </summary>
    public static List<IceServerConfig> GetConfiguredIceServers(string? deviceId)
    {
        var turnHost = Environment.GetEnvironmentVariable("TURN_HOST");
        var turnSecret = Environment.GetEnvironmentVariable("TURN_SECRET");

        if (string.IsNullOrWhiteSpace(turnHost) ||
            string.Equals(turnHost.Trim(), "turn.remotedesk.net", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(turnSecret))
        {
            return new List<IceServerConfig>
            {
                new("stun:stun.l.google.com:19302"),
                new("stun:stun.cloudflare.com:3478")
            };
        }

        var udpPort = int.TryParse(Environment.GetEnvironmentVariable("TURN_UDP_PORT"), out var u) ? u : 3478;
        var tlsPort = int.TryParse(Environment.GetEnvironmentVariable("TURN_TLS_PORT"), out var t) ? t : 5349;
        var fallbackPort = int.TryParse(Environment.GetEnvironmentVariable("TURN_FALLBACK_PORT"), out var f) ? f : 443;
        var id = string.IsNullOrWhiteSpace(deviceId) ? Guid.NewGuid().ToString("N")[..8] : deviceId;

        return CreateIceServers(
            turnHost.Trim(),
            turnSecret,
            id,
            ttl: TimeSpan.FromHours(24),
            udpPort: udpPort,
            tcpPort: udpPort,
            tlsPort: tlsPort,
            fallbackTlsPort: fallbackPort);
    }

    public static List<IceServerConfig> CreateIceServers(
        string turnHost,
        string sharedSecret,
        string deviceId,
        TimeSpan? ttl = null,
        int udpPort = 3478,
        int tcpPort = 3478,
        int tlsPort = 5349,
        int fallbackTlsPort = 443)
    {
        ttl ??= TimeSpan.FromHours(24);

        var (username, credential, _) = GenerateCredentials(sharedSecret, deviceId, ttl.Value);

        var servers = new List<IceServerConfig>
        {
            // Primary Public Anycast STUN servers (Google & Cloudflare)
            new("stun:stun.l.google.com:19302"),
            new("stun:stun.cloudflare.com:3478"),

            // Host STUN endpoints
            new($"stun:{turnHost}:{udpPort}"),

            // TURN UDP (Low-latency standard relay)
            new($"turn:{turnHost}:{udpPort}?transport=udp", username, credential),

            // TURN TCP (Reliable relay for UDP-blocking networks)
            new($"turn:{turnHost}:{tcpPort}?transport=tcp", username, credential),

            // TURNS TLS on port 5349 (Encrypted TURN)
            new($"turns:{turnHost}:{tlsPort}?transport=tcp", username, credential),

            // TURNS TLS on port 443 (Fallback for restrictive enterprise proxies)
            new($"turns:{turnHost}:{fallbackTlsPort}?transport=tcp", username, credential)
        };

        return servers;
    }
}
