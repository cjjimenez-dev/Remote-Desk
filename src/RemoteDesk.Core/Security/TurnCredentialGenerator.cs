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
            // Primary Public STUN servers
            new("stun:stun.l.google.com:19302"),

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
