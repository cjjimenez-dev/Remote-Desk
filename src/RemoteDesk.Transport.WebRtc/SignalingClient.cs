using Microsoft.AspNetCore.SignalR.Client;
using RemoteDesk.Core.Models;
using RemoteDesk.Core.Protocol;
using Serilog;

namespace RemoteDesk.Transport.WebRtc;

public sealed class SignalingClient : IAsyncDisposable
{
    private static readonly ILogger Logger = Log.ForContext<SignalingClient>();

    private HubConnection? _hub;
    private readonly string _serverUrl;

    public bool IsConnected => _hub?.State == HubConnectionState.Connected;
    public string? ConnectionId => _hub?.ConnectionId;

    public event Action<SignalingConnectionRequest>? ConnectionRequested;
    public event Action<SignalingConnectionResponse>? ConnectionAccepted;
    public event Action<string>? ConnectionRejected;
    public event Action<string>? ConnectionCancelled;
    public event Action<string>? SessionEndedByPeer;
    public event Action<SignalingSdpMessage>? OfferReceived;
    public event Action<SignalingSdpMessage>? AnswerReceived;
    public event Action<SignalingIceCandidateMessage>? IceCandidateReceived;
    public event Action? Reconnecting;
    public event Action<string?>? Reconnected;
    public event Action<Exception?>? Closed;

    public SignalingClient(string serverUrl = "http://localhost:5000/signaling")
    {
        _serverUrl = serverUrl;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_hub is not null) return;

        _hub = new HubConnectionBuilder()
            .WithUrl(_serverUrl)
            .WithAutomaticReconnect(new[]
            {
                TimeSpan.Zero,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(10)
            })
            .Build();

        _hub.Reconnecting += ex =>
        {
            Logger.Warning(ex, "Signaling connection dropped. Reconnecting...");
            Reconnecting?.Invoke();
            return Task.CompletedTask;
        };

        _hub.Reconnected += id =>
        {
            Logger.Information("Signaling reconnected with ID: {Id}", id);
            Reconnected?.Invoke(id);
            return Task.CompletedTask;
        };

        _hub.Closed += ex =>
        {
            Logger.Warning(ex, "Signaling connection closed.");
            Closed?.Invoke(ex);
            return Task.CompletedTask;
        };

        _hub.On<string, string, string, string, byte[]?, byte[]?, long>(
            "ConnectionRequested",
            (sessionId, viewerConnId, viewerName, viewerDeviceId, viewerPubKey, sig, timestamp) =>
            {
                ConnectionRequested?.Invoke(new SignalingConnectionRequest
                {
                    SessionId = sessionId,
                    ViewerConnectionId = viewerConnId,
                    ViewerName = viewerName,
                    ViewerDeviceId = viewerDeviceId,
                    ViewerPublicKey = viewerPubKey,
                    RequestSignature = sig,
                    Timestamp = timestamp
                });
            });

        _hub.On<string, string, int, string>(
            "ConnectionAccepted",
            (sessionId, hostConnId, perms, hostDeviceId) =>
            {
                ConnectionAccepted?.Invoke(new SignalingConnectionResponse
                {
                    SessionId = sessionId,
                    HostConnectionId = hostConnId,
                    Accepted = true,
                    GrantedPermissions = (SessionPermissions)perms,
                    HostDeviceId = hostDeviceId
                });
            });

        _hub.On<string>("ConnectionRejected", reason =>
        {
            ConnectionRejected?.Invoke(reason);
        });

        _hub.On<string>("ConnectionCancelled", viewerConnId =>
        {
            ConnectionCancelled?.Invoke(viewerConnId);
        });

        _hub.On<string>("SessionEndedByPeer", reason =>
        {
            SessionEndedByPeer?.Invoke(reason);
        });

        _hub.On<string, string, string, string, byte[]?, string, byte[]?, long>(
            "OfferReceived",
            (fromConnId, sessionId, sdp, senderDeviceId, senderPubKey, fingerprint, sig, timestamp) =>
            {
                OfferReceived?.Invoke(new SignalingSdpMessage
                {
                    FromConnectionId = fromConnId,
                    TargetConnectionId = _hub?.ConnectionId ?? "",
                    SessionId = sessionId,
                    Sdp = sdp,
                    SenderDeviceId = senderDeviceId,
                    SenderPublicKey = senderPubKey,
                    DtlsFingerprint = fingerprint,
                    DtlsFingerprintSignature = sig,
                    Timestamp = timestamp
                });
            });

        _hub.On<string, string, string, string, byte[]?, string, byte[]?, long>(
            "AnswerReceived",
            (fromConnId, sessionId, sdp, senderDeviceId, senderPubKey, fingerprint, sig, timestamp) =>
            {
                AnswerReceived?.Invoke(new SignalingSdpMessage
                {
                    FromConnectionId = fromConnId,
                    TargetConnectionId = _hub?.ConnectionId ?? "",
                    SessionId = sessionId,
                    Sdp = sdp,
                    SenderDeviceId = senderDeviceId,
                    SenderPublicKey = senderPubKey,
                    DtlsFingerprint = fingerprint,
                    DtlsFingerprintSignature = sig,
                    Timestamp = timestamp
                });
            });

        _hub.On<string, string, string, int>("IceCandidateReceived", (fromConnId, candidate, sdpMid, sdpMLineIndex) =>
        {
            IceCandidateReceived?.Invoke(new SignalingIceCandidateMessage
            {
                FromConnectionId = fromConnId,
                TargetConnectionId = _hub?.ConnectionId ?? "",
                Candidate = candidate,
                SdpMid = sdpMid,
                SdpMLineIndex = sdpMLineIndex
            });
        });

        await _hub.StartAsync(ct);
        Logger.Information("Connected to signaling server at {Url}. Connection ID: {Id}", _serverUrl, _hub.ConnectionId);
    }

    public async Task<List<IceServerConfig>> GetIceServersAsync(string deviceId, CancellationToken ct = default)
    {
        if (_hub is null) throw new InvalidOperationException("Not connected to signaling server.");
        return await _hub.InvokeAsync<List<IceServerConfig>>("GetIceServers", deviceId, ct);
    }

    public async Task<bool> RegisterHostAsync(string hostId, string deviceName, byte[]? publicKey = null, CancellationToken ct = default)
    {
        if (_hub is null) throw new InvalidOperationException("Not connected to signaling server.");
        return await _hub.InvokeAsync<bool>("RegisterHost", hostId, deviceName, publicKey, ct);
    }

    public Task RequestConnectionAsync(string targetHostId, string viewerName, CancellationToken ct = default)
        => RequestConnectionAsync(targetHostId, viewerName, "", null, null, 0, ct);

    public async Task RequestConnectionAsync(
        string targetHostId,
        string viewerName,
        string viewerDeviceId,
        byte[]? viewerPublicKey,
        byte[]? signature,
        long timestamp,
        CancellationToken ct = default)
    {
        if (_hub is null) throw new InvalidOperationException("Not connected to signaling server.");
        await _hub.SendAsync("RequestConnectionWithSecurity",
            targetHostId, viewerName, viewerDeviceId, viewerPublicKey, signature, timestamp, ct);
    }

    public Task AcceptConnectionAsync(string viewerConnectionId, string sessionId, CancellationToken ct = default)
        => AcceptConnectionAsync(viewerConnectionId, sessionId, SessionPermissions.All, "", ct);

    public async Task AcceptConnectionAsync(
        string viewerConnectionId,
        string sessionId,
        SessionPermissions permissions,
        string hostDeviceId = "",
        CancellationToken ct = default)
    {
        if (_hub is null) throw new InvalidOperationException("Not connected to signaling server.");
        await _hub.SendAsync("AcceptConnectionWithPermissions",
            viewerConnectionId, sessionId, (int)permissions, hostDeviceId, ct);
    }

    public async Task RejectConnectionAsync(string viewerConnectionId, string reason, CancellationToken ct = default)
    {
        if (_hub is null) throw new InvalidOperationException("Not connected to signaling server.");
        await _hub.SendAsync("RejectConnection", viewerConnectionId, reason, ct);
    }

    public async Task CancelConnectionAsync(CancellationToken ct = default)
    {
        if (_hub is null || _hub.State != HubConnectionState.Connected) return;
        await _hub.SendAsync("CancelConnection", ct);
    }

    public async Task NotifySessionEndedAsync(string targetConnectionId, string reason, CancellationToken ct = default)
    {
        if (_hub is null || _hub.State != HubConnectionState.Connected || string.IsNullOrWhiteSpace(targetConnectionId)) return;
        await _hub.SendAsync("NotifySessionEnded", targetConnectionId, reason, ct);
    }

    public Task SendOfferAsync(string targetConnectionId, string sdp, CancellationToken ct = default)
        => SendOfferAsync(targetConnectionId, "", sdp, ct: ct);

    public async Task SendOfferAsync(
        string targetConnectionId,
        string sessionId,
        string sdp,
        string senderDeviceId = "",
        byte[]? senderPublicKey = null,
        string dtlsFingerprint = "",
        byte[]? signature = null,
        long timestamp = 0,
        CancellationToken ct = default)
    {
        if (_hub is null) throw new InvalidOperationException("Not connected to signaling server.");
        await _hub.SendAsync("SendSignedOffer",
            targetConnectionId, sessionId, sdp, senderDeviceId, senderPublicKey, dtlsFingerprint, signature, timestamp, ct);
    }

    public Task SendAnswerAsync(string targetConnectionId, string sdp, CancellationToken ct = default)
        => SendAnswerAsync(targetConnectionId, "", sdp, ct: ct);

    public async Task SendAnswerAsync(
        string targetConnectionId,
        string sessionId,
        string sdp,
        string senderDeviceId = "",
        byte[]? senderPublicKey = null,
        string dtlsFingerprint = "",
        byte[]? signature = null,
        long timestamp = 0,
        CancellationToken ct = default)
    {
        if (_hub is null) throw new InvalidOperationException("Not connected to signaling server.");
        await _hub.SendAsync("SendSignedAnswer",
            targetConnectionId, sessionId, sdp, senderDeviceId, senderPublicKey, dtlsFingerprint, signature, timestamp, ct);
    }

    public async Task SendIceCandidateAsync(
        string targetConnectionId,
        string candidate,
        string sdpMid,
        int sdpMLineIndex,
        CancellationToken ct = default)
    {
        if (_hub is null) throw new InvalidOperationException("Not connected to signaling server.");
        await _hub.SendAsync("SendIceCandidate", targetConnectionId, candidate, sdpMid, sdpMLineIndex, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub != null)
        {
            await _hub.DisposeAsync();
            _hub = null;
        }
    }
}
