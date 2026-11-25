using System.Net;

namespace RemoteDesk.Core.Services;

/// <summary>
/// Represents parsed server connection endpoints for Signaling and Fleet Management API.
/// </summary>
public record ServerEndpointConfig(
    string RawInput,
    string Host,
    int? Port,
    bool IsSecure,
    string SignalingUrl,
    string FleetApiUrl);

/// <summary>
/// Smart parser for server connection targets entered by users or supplied via CLI.
/// Supports localhost, LAN IPs, public VPS IPs with ports, Cloudflare Tunnel hostnames (.trycloudflare.com),
/// and full HTTPS/WSS URLs.
/// </summary>
public static class ServerEndpointParser
{
    private const int DefaultSignalingPort = 5000;
    private const int DefaultFleetPort = 5001;

    public static ServerEndpointConfig Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return new ServerEndpointConfig(
                RawInput: string.Empty,
                Host: "localhost",
                Port: DefaultSignalingPort,
                IsSecure: false,
                SignalingUrl: $"http://localhost:{DefaultSignalingPort}/signaling",
                FleetApiUrl: $"http://localhost:{DefaultFleetPort}");
        }

        var trimmed = input.Trim().TrimEnd('/');

        // 1. Explicit scheme: http:// or https://
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            {
                var isHttps = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);
                var host = uri.Host;
                var hasCustomPort = !uri.IsDefaultPort && uri.Port > 0;
                var port = hasCustomPort ? uri.Port : (int?)null;

                var path = uri.AbsolutePath.TrimEnd('/');
                var sigPath = path.EndsWith("/signaling", StringComparison.OrdinalIgnoreCase)
                    ? path
                    : (string.IsNullOrEmpty(path) || path == "/" ? "/signaling" : $"{path}/signaling");

                var hostPort = hasCustomPort ? $"{host}:{uri.Port}" : host;
                var sigUrl = $"{uri.Scheme}://{hostPort}{sigPath}";

                // For Fleet API: if same port or standard, derive appropriate fleet URL
                string fleetUrl;
                if (hasCustomPort && uri.Port == DefaultSignalingPort)
                {
                    fleetUrl = $"{uri.Scheme}://{host}:{DefaultFleetPort}";
                }
                else
                {
                    fleetUrl = $"{uri.Scheme}://{hostPort}";
                }

                return new ServerEndpointConfig(
                    RawInput: input,
                    Host: host,
                    Port: port,
                    IsSecure: isHttps,
                    SignalingUrl: sigUrl,
                    FleetApiUrl: fleetUrl);
            }
        }

        // 2. Localhost alias
        if (string.Equals(trimmed, "localhost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            return new ServerEndpointConfig(
                RawInput: input,
                Host: trimmed,
                Port: DefaultSignalingPort,
                IsSecure: false,
                SignalingUrl: $"http://{trimmed}:{DefaultSignalingPort}/signaling",
                FleetApiUrl: $"http://{trimmed}:{DefaultFleetPort}");
        }

        // 3. Cloudflare Tunnel hostname (e.g., remotedesk.trycloudflare.com or *.cfargotunnel.com)
        if (trimmed.EndsWith(".trycloudflare.com", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith(".cfargotunnel.com", StringComparison.OrdinalIgnoreCase))
        {
            return new ServerEndpointConfig(
                RawInput: input,
                Host: trimmed,
                Port: 443,
                IsSecure: true,
                SignalingUrl: $"https://{trimmed}/signaling",
                FleetApiUrl: $"https://{trimmed}");
        }

        // 4. Host with explicit port (e.g. 192.168.1.50:5000, 1.2.3.4:8080, relay.mydomain.com:5000)
        var colonIdx = trimmed.LastIndexOf(':');
        if (colonIdx > 0 && int.TryParse(trimmed[(colonIdx + 1)..], out var parsedPort))
        {
            var hostPart = trimmed[..colonIdx];
            var isSecure = parsedPort == 443 || parsedPort == 8443;
            var scheme = isSecure ? "https" : "http";

            var fleetPort = parsedPort == DefaultSignalingPort ? DefaultFleetPort : parsedPort;
            return new ServerEndpointConfig(
                RawInput: input,
                Host: hostPart,
                Port: parsedPort,
                IsSecure: isSecure,
                SignalingUrl: $"{scheme}://{hostPart}:{parsedPort}/signaling",
                FleetApiUrl: $"{scheme}://{hostPart}:{fleetPort}");
        }

        // 5. Raw IP address without port (LAN or Public IPv4) -> default to standard port 5000
        if (IPAddress.TryParse(trimmed, out var ip))
        {
            return new ServerEndpointConfig(
                RawInput: input,
                Host: trimmed,
                Port: DefaultSignalingPort,
                IsSecure: false,
                SignalingUrl: $"http://{trimmed}:{DefaultSignalingPort}/signaling",
                FleetApiUrl: $"http://{trimmed}:{DefaultFleetPort}");
        }

        // 6. Generic domain name (e.g., relay.mycompany.com, remotedesk.net)
        // Public domain names deployed behind Cloudflare or reverse proxy default to HTTPS on port 443
        if (trimmed.Contains('.'))
        {
            return new ServerEndpointConfig(
                RawInput: input,
                Host: trimmed,
                Port: 443,
                IsSecure: true,
                SignalingUrl: $"https://{trimmed}/signaling",
                FleetApiUrl: $"https://{trimmed}");
        }

        // 7. Fallback: Hostname without dots or port -> local HTTP port 5000
        return new ServerEndpointConfig(
            RawInput: input,
            Host: trimmed,
            Port: DefaultSignalingPort,
            IsSecure: false,
            SignalingUrl: $"http://{trimmed}:{DefaultSignalingPort}/signaling",
            FleetApiUrl: $"http://{trimmed}:{DefaultFleetPort}");
    }
}
