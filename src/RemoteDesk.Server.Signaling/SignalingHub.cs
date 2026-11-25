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
    private static readonly ConcurrentDictionary<string, string> PendingHostConnByViewerConnId = new();

    public Task<bool> RegisterHost(string hostId, string deviceName, byte[]? publicKey = null)
    {
        var connectionId = Context.ConnectionId;
        var info = new SignalingHostInfo
        {
            ConnectionId = connectionId,
            HostId = hostId,
            DeviceName = deviceName,
            RegisteredAtUtc = DateTime.UtcNow,
            PublicKey = publicKey
        };

        HostsById[hostId] = info;
        HostIdByConnectionId[connectionId] = hostId;

        Logger.Information("Host registered: ID=[{HostId}], Name=[{Name}], Connection=[{ConnId}], KeySize={KeyLen}",
            hostId, deviceName, connectionId, publicKey?.Length ?? 0);

        return Task.FromResult(true);
    }

    public Task<List<IceServerConfig>> GetIceServers(string deviceId)
    {
        var id = string.IsNullOrWhiteSpace(deviceId) ? Context.ConnectionId : deviceId;
        var servers = TurnCredentialGenerator.GetConfiguredIceServers(id);

        Logger.Debug("Generated {Count} ICE servers for device [{DeviceId}]", servers.Count, id);
        return Task.FromResult(servers);
    }

    public async Task RequestConnection(string targetHostId, string viewerName)
    {
        await RequestConnectionWithSecurity(targetHostId, viewerName, Context.ConnectionId, null, null, 0);
    }

    public async Task RequestConnectionWithSecurity(
        string targetHostId,
        string viewerName,
        string viewerDeviceId,
        byte[]? viewerPublicKey,
        byte[]? requestSignature,
        long timestamp)
    {
        var viewerConnectionId = Context.ConnectionId;

        if (!HostsById.TryGetValue(targetHostId, out var hostInfo))
        {
            Logger.Warning("Viewer [{ViewerConn}] requested offline host [{HostId}]", viewerConnectionId, targetHostId);
            await Clients.Caller.SendAsync("ConnectionRejected", "Target host is offline or ID does not exist.");
            return;
        }

        var sessionId = Guid.NewGuid().ToString("N")[..8];
        PendingHostConnByViewerConnId[viewerConnectionId] = hostInfo.ConnectionId;
        Logger.Information("Connection request: Session=[{SessionId}], Viewer=[{ViewerName} ({ViewerConn})], DeviceId=[{DeviceId}] -> Host=[{HostId}]",
            sessionId, viewerName, viewerConnectionId, viewerDeviceId, targetHostId);

        await Clients.Client(hostInfo.ConnectionId).SendAsync("ConnectionRequested",
            sessionId, viewerConnectionId, viewerName, viewerDeviceId, viewerPublicKey, requestSignature, timestamp);
    }

    public Task AcceptConnection(string viewerConnectionId, string sessionId)
    {
        return AcceptConnectionWithPermissions(viewerConnectionId, sessionId, (int)SessionPermissions.All, Context.ConnectionId);
    }

    public async Task AcceptConnectionWithPermissions(string viewerConnectionId, string sessionId, int permissions, string hostDeviceId)
    {
        PendingHostConnByViewerConnId.TryRemove(viewerConnectionId, out _);
        var hostConnectionId = Context.ConnectionId;
        Logger.Information("Connection accepted: Session=[{SessionId}] for Viewer=[{ViewerConn}], Perms=[{Permissions}]",
            sessionId, viewerConnectionId, (SessionPermissions)permissions);

        await Clients.Client(viewerConnectionId).SendAsync("ConnectionAccepted", sessionId, hostConnectionId, permissions, hostDeviceId);
    }

    public async Task RejectConnection(string viewerConnectionId, string reason)
    {
        PendingHostConnByViewerConnId.TryRemove(viewerConnectionId, out _);
        Logger.Information("Connection rejected for Viewer=[{ViewerConn}]: {Reason}", viewerConnectionId, reason);
        await Clients.Client(viewerConnectionId).SendAsync("ConnectionRejected", reason);
    }

    public async Task CancelConnection()
    {
        var viewerConnectionId = Context.ConnectionId;
        if (PendingHostConnByViewerConnId.TryRemove(viewerConnectionId, out var hostConnectionId))
        {
            Logger.Information("Connection request cancelled by Viewer=[{ViewerConn}] for HostConn=[{HostConn}]",
                viewerConnectionId, hostConnectionId);
            await Clients.Client(hostConnectionId).SendAsync("ConnectionCancelled", viewerConnectionId);
        }
    }

    public async Task NotifySessionEnded(string targetConnectionId, string reason)
    {
        if (string.IsNullOrWhiteSpace(targetConnectionId)) return;
        Logger.Information("Relaying session end: [{From}] -> [{To}]: {Reason}", Context.ConnectionId, targetConnectionId, reason);
        await Clients.Client(targetConnectionId).SendAsync("SessionEndedByPeer", reason);
    }

    public async Task SendOffer(string targetConnectionId, string sdp)
    {
        await SendSignedOffer(targetConnectionId, "", sdp, "", null, "", null, 0);
    }

    public async Task SendSignedOffer(
        string targetConnectionId,
        string sessionId,
        string sdp,
        string senderDeviceId,
        byte[]? senderPublicKey,
        string dtlsFingerprint,
        byte[]? signature,
        long timestamp)
    {
        Logger.Debug("Relaying SDP Offer: [{From}] -> [{To}], HasSig: {HasSig}", Context.ConnectionId, targetConnectionId, signature != null);
        await Clients.Client(targetConnectionId).SendAsync("OfferReceived",
            Context.ConnectionId, sessionId, sdp, senderDeviceId, senderPublicKey, dtlsFingerprint, signature, timestamp);
    }

    public async Task SendAnswer(string targetConnectionId, string sdp)
    {
        await SendSignedAnswer(targetConnectionId, "", sdp, "", null, "", null, 0);
    }

    public async Task SendSignedAnswer(
        string targetConnectionId,
        string sessionId,
        string sdp,
        string senderDeviceId,
        byte[]? senderPublicKey,
        string dtlsFingerprint,
        byte[]? signature,
        long timestamp)
    {
        Logger.Debug("Relaying SDP Answer: [{From}] -> [{To}], HasSig: {HasSig}", Context.ConnectionId, targetConnectionId, signature != null);
        await Clients.Client(targetConnectionId).SendAsync("AnswerReceived",
            Context.ConnectionId, sessionId, sdp, senderDeviceId, senderPublicKey, dtlsFingerprint, signature, timestamp);
    }

    public async Task SendIceCandidate(string targetConnectionId, string candidate, string sdpMid, int sdpMLineIndex)
    {
        Logger.Debug("Relaying ICE Candidate: [{From}] -> [{To}]", Context.ConnectionId, targetConnectionId);
        await Clients.Client(targetConnectionId).SendAsync("IceCandidateReceived", Context.ConnectionId, candidate, sdpMid, sdpMLineIndex);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var connectionId = Context.ConnectionId;
        if (HostIdByConnectionId.TryRemove(connectionId, out var hostId))
        {
            HostsById.TryRemove(hostId, out _);
            Logger.Information("Host disconnected: ID=[{HostId}], Connection=[{ConnId}]", hostId, connectionId);
        }

        if (PendingHostConnByViewerConnId.TryRemove(connectionId, out var hostConnectionId))
        {
            Logger.Information("Pending viewer disconnected before acceptance: Viewer=[{ViewerConn}] -> HostConn=[{HostConn}]",
                connectionId, hostConnectionId);
            await Clients.Client(hostConnectionId).SendAsync("ConnectionCancelled", connectionId);
        }

        await base.OnDisconnectedAsync(exception);
    }
}
