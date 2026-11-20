using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using RemoteDesk.Core.Models;
using RemoteDesk.Core.Protocol;
using RemoteDesk.Core.Security;

namespace RemoteDesk.Server.Signaling;

public sealed class SignalingHub : Hub
{
    private static readonly Serilog.ILogger Logger = Serilog.Log.ForContext<SignalingHub>();

    private static readonly ConcurrentDictionary<string, SignalingHostInfo> HostsById = new();
    private static readonly ConcurrentDictionary<string, string> HostIdByConnectionId = new();

    public Task<bool> RegisterHost(string hostId, string deviceName)
    {
        var connectionId = Context.ConnectionId;
        var info = new SignalingHostInfo
        {
            ConnectionId = connectionId,
            HostId = hostId,
            DeviceName = deviceName,
            RegisteredAtUtc = DateTime.UtcNow
        };

        HostsById[hostId] = info;
        HostIdByConnectionId[connectionId] = hostId;

        Logger.Information("Host registered: ID=[{HostId}], Name=[{Name}], Connection=[{ConnId}]",
            hostId, deviceName, connectionId);

        return Task.FromResult(true);
    }

    public Task<List<IceServerConfig>> GetIceServers(string deviceId)
    {
        var turnHost = Environment.GetEnvironmentVariable("TURN_HOST") ?? "turn.remotedesk.net";
        var turnSecret = Environment.GetEnvironmentVariable("TURN_SECRET") ?? "RemoteDesk_Turn_Secret_Key_Change_In_Production_2026!";
        var udpPort = int.TryParse(Environment.GetEnvironmentVariable("TURN_UDP_PORT"), out var u) ? u : 3478;
        var tlsPort = int.TryParse(Environment.GetEnvironmentVariable("TURN_TLS_PORT"), out var t) ? t : 5349;
        var fallbackPort = int.TryParse(Environment.GetEnvironmentVariable("TURN_FALLBACK_PORT"), out var f) ? f : 443;

        var id = string.IsNullOrWhiteSpace(deviceId) ? Context.ConnectionId : deviceId;
        var servers = TurnCredentialGenerator.CreateIceServers(
            turnHost,
            turnSecret,
            id,
            ttl: TimeSpan.FromHours(24),
            udpPort: udpPort,
            tcpPort: udpPort,
            tlsPort: tlsPort,
            fallbackTlsPort: fallbackPort);

        Logger.Debug("Generated {Count} ICE servers for device [{DeviceId}]", servers.Count, id);
        return Task.FromResult(servers);
    }

    public async Task RequestConnection(string targetHostId, string viewerName)
    {
        var viewerConnectionId = Context.ConnectionId;

        if (!HostsById.TryGetValue(targetHostId, out var hostInfo))
        {
            Logger.Warning("Viewer [{ViewerConn}] requested offline host [{HostId}]", viewerConnectionId, targetHostId);
            await Clients.Caller.SendAsync("ConnectionRejected", "Target host is offline or ID does not exist.");
            return;
        }

        var sessionId = Guid.NewGuid().ToString("N")[..8];
        Logger.Information("Connection request: Session=[{SessionId}], Viewer=[{ViewerName} ({ViewerConn})] -> Host=[{HostId}]",
            sessionId, viewerName, viewerConnectionId, targetHostId);

        await Clients.Client(hostInfo.ConnectionId).SendAsync("ConnectionRequested", sessionId, viewerConnectionId, viewerName);
    }

    public async Task AcceptConnection(string viewerConnectionId, string sessionId)
    {
        var hostConnectionId = Context.ConnectionId;
        Logger.Information("Connection accepted: Session=[{SessionId}] for Viewer=[{ViewerConn}]", sessionId, viewerConnectionId);

        await Clients.Client(viewerConnectionId).SendAsync("ConnectionAccepted", sessionId, hostConnectionId);
    }

    public async Task RejectConnection(string viewerConnectionId, string reason)
    {
        Logger.Information("Connection rejected for Viewer=[{ViewerConn}]: {Reason}", viewerConnectionId, reason);
        await Clients.Client(viewerConnectionId).SendAsync("ConnectionRejected", reason);
    }

    public async Task SendOffer(string targetConnectionId, string sdp)
    {
        Logger.Debug("Relaying SDP Offer: [{From}] -> [{To}]", Context.ConnectionId, targetConnectionId);
        await Clients.Client(targetConnectionId).SendAsync("OfferReceived", Context.ConnectionId, sdp);
    }

    public async Task SendAnswer(string targetConnectionId, string sdp)
    {
        Logger.Debug("Relaying SDP Answer: [{From}] -> [{To}]", Context.ConnectionId, targetConnectionId);
        await Clients.Client(targetConnectionId).SendAsync("AnswerReceived", Context.ConnectionId, sdp);
    }

    public async Task SendIceCandidate(string targetConnectionId, string candidate, string sdpMid, int sdpMLineIndex)
    {
        Logger.Debug("Relaying ICE Candidate: [{From}] -> [{To}]", Context.ConnectionId, targetConnectionId);
        await Clients.Client(targetConnectionId).SendAsync("IceCandidateReceived", Context.ConnectionId, candidate, sdpMid, sdpMLineIndex);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        var connectionId = Context.ConnectionId;
        if (HostIdByConnectionId.TryRemove(connectionId, out var hostId))
        {
            HostsById.TryRemove(hostId, out _);
            Logger.Information("Host disconnected: ID=[{HostId}], Connection=[{ConnId}]", hostId, connectionId);
        }

        return base.OnDisconnectedAsync(exception);
    }
}
