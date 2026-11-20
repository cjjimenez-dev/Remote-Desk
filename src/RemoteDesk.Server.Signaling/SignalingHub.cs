using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using RemoteDesk.Core.Protocol;

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
