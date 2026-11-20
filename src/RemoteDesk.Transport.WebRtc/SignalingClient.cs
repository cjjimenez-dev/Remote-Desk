using Microsoft.AspNetCore.SignalR.Client;
using Serilog;

namespace RemoteDesk.Transport.WebRtc;

public sealed class SignalingClient : IAsyncDisposable
{
    private static readonly ILogger Logger = Log.ForContext<SignalingClient>();

    private HubConnection? _hub;
    private readonly string _serverUrl;

    public bool IsConnected => _hub?.State == HubConnectionState.Connected;
    public string? ConnectionId => _hub?.ConnectionId;

    public event Action<string, string, string>? ConnectionRequested; // sessionId, viewerConnectionId, viewerName
    public event Action<string, string>? ConnectionAccepted;          // sessionId, hostConnectionId
    public event Action<string>? ConnectionRejected;                  // reason
    public event Action<string, string>? OfferReceived;               // fromConnectionId, sdp
    public event Action<string, string>? AnswerReceived;              // fromConnectionId, sdp
    public event Action<string, string, string, int>? IceCandidateReceived; // fromConnectionId, candidate, sdpMid, sdpMLineIndex
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

        _hub.On<string, string, string>("ConnectionRequested", (sessionId, viewerConnId, viewerName) =>
        {
            ConnectionRequested?.Invoke(sessionId, viewerConnId, viewerName);
        });

        _hub.On<string, string>("ConnectionAccepted", (sessionId, hostConnId) =>
        {
            ConnectionAccepted?.Invoke(sessionId, hostConnId);
        });

        _hub.On<string>("ConnectionRejected", reason =>
        {
            ConnectionRejected?.Invoke(reason);
        });

        _hub.On<string, string>("OfferReceived", (fromConnId, sdp) =>
        {
            OfferReceived?.Invoke(fromConnId, sdp);
        });

        _hub.On<string, string>("AnswerReceived", (fromConnId, sdp) =>
        {
            AnswerReceived?.Invoke(fromConnId, sdp);
        });

        _hub.On<string, string, string, int>("IceCandidateReceived", (fromConnId, candidate, sdpMid, sdpMLineIndex) =>
        {
            IceCandidateReceived?.Invoke(fromConnId, candidate, sdpMid, sdpMLineIndex);
        });

        await _hub.StartAsync(ct);
        Logger.Information("Connected to signaling server at {Url}. Connection ID: {Id}", _serverUrl, _hub.ConnectionId);
    }

    public async Task<bool> RegisterHostAsync(string hostId, string deviceName, CancellationToken ct = default)
    {
        if (_hub is null) throw new InvalidOperationException("Not connected to signaling server.");
        return await _hub.InvokeAsync<bool>("RegisterHost", hostId, deviceName, ct);
    }

    public async Task RequestConnectionAsync(string targetHostId, string viewerName, CancellationToken ct = default)
    {
        if (_hub is null) throw new InvalidOperationException("Not connected to signaling server.");
        await _hub.SendAsync("RequestConnection", targetHostId, viewerName, ct);
    }

    public async Task AcceptConnectionAsync(string viewerConnectionId, string sessionId, CancellationToken ct = default)
    {
        if (_hub is null) throw new InvalidOperationException("Not connected to signaling server.");
        await _hub.SendAsync("AcceptConnection", viewerConnectionId, sessionId, ct);
    }

    public async Task RejectConnectionAsync(string viewerConnectionId, string reason, CancellationToken ct = default)
    {
        if (_hub is null) throw new InvalidOperationException("Not connected to signaling server.");
        await _hub.SendAsync("RejectConnection", viewerConnectionId, reason, ct);
    }

    public async Task SendOfferAsync(string targetConnectionId, string sdp, CancellationToken ct = default)
    {
        if (_hub is null) throw new InvalidOperationException("Not connected to signaling server.");
        await _hub.SendAsync("SendOffer", targetConnectionId, sdp, ct);
    }

    public async Task SendAnswerAsync(string targetConnectionId, string sdp, CancellationToken ct = default)
    {
        if (_hub is null) throw new InvalidOperationException("Not connected to signaling server.");
        await _hub.SendAsync("SendAnswer", targetConnectionId, sdp, ct);
    }

    public async Task SendIceCandidateAsync(string targetConnectionId, string candidate, string sdpMid, int sdpMLineIndex, CancellationToken ct = default)
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
