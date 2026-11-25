using System.IO;
using RemoteDesk.Core.Interfaces;
using RemoteDesk.Core.Models;
using RemoteDesk.Core.Protocol;
using RemoteDesk.Core.Quality;
using RemoteDesk.Core.Security;
using RemoteDesk.Core.Services;
using RemoteDesk.Platform.Windows;
using RemoteDesk.Transport.WebRtc;
using Serilog;
using SIPSorcery.Net;

namespace RemoteDesk.Viewer.Servers;

public sealed class HostSessionServer : IAsyncDisposable
{
    private static readonly ILogger Logger = Log.ForContext<HostSessionServer>();

    private readonly DeviceIdentity _identity;
    private readonly string _hostDeviceId;
    private readonly string _deviceName;
    private string _signalingUrl;
    private readonly bool _forceRelay;

    private SignalingClient? _signalingClient;
    private WebRtcTransport? _activeTransport;
    private IScreenCapturer? _capturer;
    private IVideoEncoder? _encoder;
    private IInputInjector? _injector;
    private EmergencyKillSwitch? _killSwitch;
    private FileTransferService? _fileTransferService;

    private readonly object _sessionLock = new();
    private string? _activeViewerConnId;
    private string? _activeViewerDeviceId;
    private string? _activeViewerName;
    private string? _activeSessionId;

    public bool IsHostRegistered { get; private set; }
    public bool IsServingSession => _activeTransport != null;
    public string? ActiveViewerName => _activeViewerName;
    public string? ActiveViewerDeviceId => _activeViewerDeviceId;

    public event Action<string, string>? IncomingConnectionPrompt;
    public event Action<string, string>? IncomingSessionStarting;
    public event Action? IncomingConnectionCancelled;
    public event Action? SessionStarted;
    public event Action? SessionEnded;
    public event Action? StatusChanged;

    private int _isEncodingFrame;

    public bool AutoAcceptConnections { get; set; } = false;

    public HostSessionServer(DeviceIdentity identity, string deviceName, string signalingUrl, bool forceRelay = false)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _hostDeviceId = identity.DeviceId;
        _deviceName = deviceName;
        _signalingUrl = signalingUrl;
        _forceRelay = forceRelay;
    }

    public async Task StartAsync(string? newSignalingUrl = null)
    {
        if (newSignalingUrl != null)
        {
            _signalingUrl = newSignalingUrl;
        }

        try
        {
            if (_signalingClient != null)
            {
                await _signalingClient.DisposeAsync().ConfigureAwait(false);
                _signalingClient = null;
            }

            Logger.Information("Starting background Host engine for [{HostId}] on {SignalingUrl}...", _hostDeviceId, _signalingUrl);
            _signalingClient = new SignalingClient(_signalingUrl);

            _signalingClient.ConnectionRequested += HandleConnectionRequested;
            _signalingClient.ConnectionCancelled += HandleConnectionCancelled;
            _signalingClient.OfferReceived += HandleOfferReceived;
            _signalingClient.IceCandidateReceived += HandleIceCandidateReceived;

            await _signalingClient.ConnectAsync().ConfigureAwait(false);
            var registered = await _signalingClient.RegisterHostAsync(_hostDeviceId, _deviceName, _identity.PublicKeyBytes).ConfigureAwait(false);
            IsHostRegistered = registered;

            if (registered)
            {
                Logger.Information("Background Host engine successfully registered as connectable endpoint [{HostId}].", _hostDeviceId);
            }
            StatusChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Could not start background Host engine on {SignalingUrl}. Will retry when server becomes available.", _signalingUrl);
            IsHostRegistered = false;
            StatusChanged?.Invoke();
        }
    }

    private void HandleConnectionRequested(SignalingConnectionRequest req)
    {
        // Verify viewer cryptographic request signature when provided
        if (req.ViewerPublicKey == null || req.RequestSignature == null)
        {
            Logger.Warning("Rejecting unsigned connection request from [{ViewerName}] ({ViewerConn}).", req.ViewerName, req.ViewerConnectionId);
            _ = _signalingClient?.RejectConnectionAsync(req.ViewerConnectionId, "Unsigned connection request rejected.");
            return;
        }

        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(nowUnix - req.Timestamp) > 300)
        {
            Logger.Warning("Rejecting expired connection request from [{ViewerName}] (ts={Timestamp}).", req.ViewerName, req.Timestamp);
            _ = _signalingClient?.RejectConnectionAsync(req.ViewerConnectionId, "Connection request timestamp expired.");
            return;
        }

        var expectedPayload = $"{_hostDeviceId}:{req.ViewerDeviceId}:{req.Timestamp}";
        if (!DeviceIdentity.VerifySignature(req.ViewerPublicKey, expectedPayload, req.RequestSignature))
        {
            Logger.Warning("Rejecting connection request with invalid signature from [{ViewerName}] ({ViewerDeviceId}).", req.ViewerName, req.ViewerDeviceId);
            _ = _signalingClient?.RejectConnectionAsync(req.ViewerConnectionId, "Invalid viewer request signature.");
            return;
        }

        lock (_sessionLock)
        {
            if (_activeViewerConnId != null)
            {
                Logger.Warning("Rejecting connection from [{ViewerName}] ({ViewerConn}): Already busy with an active session.",
                    req.ViewerName, req.ViewerConnectionId);
                _ = _signalingClient?.RejectConnectionAsync(req.ViewerConnectionId, "Host is busy with another session.");
                return;
            }

            _activeViewerConnId = req.ViewerConnectionId;
            _activeViewerDeviceId = req.ViewerDeviceId;
            _activeViewerName = req.ViewerName;
            _activeSessionId = req.SessionId;
        }

        if (AutoAcceptConnections)
        {
            Logger.Information("Auto-accepting incoming connection from [{ViewerName}] ({ViewerDeviceId})...", req.ViewerName, req.ViewerDeviceId);
            IncomingSessionStarting?.Invoke(req.ViewerName, req.ViewerDeviceId);
            _ = AcceptIncomingConnectionAsync();
        }
        else
        {
            IncomingConnectionPrompt?.Invoke(req.ViewerName, req.ViewerDeviceId);
        }
    }

    public async Task AcceptIncomingConnectionAsync()
    {
        string? viewerConnId;
        string? sessionId;
        string? viewerName;
        string? viewerDeviceId;

        lock (_sessionLock)
        {
            viewerConnId = _activeViewerConnId;
            sessionId = _activeSessionId;
            viewerName = _activeViewerName;
            viewerDeviceId = _activeViewerDeviceId;
        }

        if (viewerConnId == null || sessionId == null || _signalingClient == null) return;

        if (!AutoAcceptConnections && viewerName != null && viewerDeviceId != null)
        {
            IncomingSessionStarting?.Invoke(viewerName, viewerDeviceId);
        }

        try
        {
            await _signalingClient.AcceptConnectionAsync(viewerConnId, sessionId, SessionPermissions.All, _hostDeviceId).ConfigureAwait(false);

            List<IceServerConfig>? iceServers = null;
            try
            {
                iceServers = await _signalingClient.GetIceServersAsync(_hostDeviceId).ConfigureAwait(false);
            }
            catch { }

            _activeTransport = new WebRtcTransport(isInitiator: false, iceServers: iceServers, forceRelay: _forceRelay);

            _activeTransport.IceCandidateGenerated += (candidate, mid, line) =>
            {
                if (_signalingClient != null && viewerConnId != null)
                {
                    _ = _signalingClient.SendIceCandidateAsync(viewerConnId, candidate, mid, line);
                }
            };

            _activeTransport.StateChanged += state =>
            {
                Logger.Information("Host WebRTC state changed: {State}", state);

                if (state == TransportState.Connected)
                {
                    Logger.Information("WebRTC P2P connection established! Starting screen capture and input injection...");
                    StartHostStreaming();
                    SessionStarted?.Invoke();
                    StatusChanged?.Invoke();
                }
                else if (state is TransportState.Disconnected or TransportState.Failed or TransportState.Closed)
                {
                    CloseSession();
                }
            };

            _activeTransport.MessageReceived += HandleIncomingControlMessage;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to accept incoming connection.");
            CloseSession();
        }
    }

    public async Task RejectIncomingConnectionAsync(string reason = "Rejected by user")
    {
        string? viewerConnId;
        lock (_sessionLock)
        {
            viewerConnId = _activeViewerConnId;
            _activeViewerConnId = null;
            _activeViewerDeviceId = null;
            _activeViewerName = null;
            _activeSessionId = null;
        }

        if (viewerConnId != null && _signalingClient != null)
        {
            try
            {
                await _signalingClient.RejectConnectionAsync(viewerConnId, reason).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to send connection rejection to viewer [{ViewerConn}].", viewerConnId);
            }
        }
    }

    private void HandleConnectionCancelled(string viewerConnId)
    {
        bool shouldClear;
        lock (_sessionLock)
        {
            shouldClear = _activeViewerConnId == viewerConnId;
        }

        if (shouldClear)
        {
            Logger.Information("Viewer [{ViewerConn}] cancelled pending connection request.", viewerConnId);
            CloseSession(notifyPeer: false);
            IncomingConnectionCancelled?.Invoke();
        }
    }

    private void StartHostStreaming()
    {
        lock (_sessionLock)
        {
            try
            {
                _injector = new WindowsInputInjector();
                _killSwitch = new EmergencyKillSwitch(_injector);
                _killSwitch.Start();

                try
                {
                    _capturer = new DxgiScreenCapturer();
                    Logger.Information("Host using DXGI GPU Hardware Screen Capturer.");
                }
                catch (Exception dxgiEx)
                {
                    Logger.Warning(dxgiEx, "DXGI screen capturer unavailable, falling back to GDI capturer.");
                    _capturer = new GdiScreenCapturer();
                }

                var display = _capturer.CurrentDisplay;
                if (display != null && _injector != null)
                {
                    _injector.SetTargetDisplay(display);
                }

                if (_activeTransport != null)
                {
                    _fileTransferService = new FileTransferService(_activeTransport);
                    _fileTransferService.ProgressUpdated += progress =>
                    {
                        var status = progress.IsCompleted ? "Completed" : (progress.ErrorMessage ?? $"{progress.BytesTransferred * 100 / Math.Max(1, progress.TotalBytes)}%");
                        Logger.Information("Host file transfer: {File} - {Status} ({Bytes}/{Total})",
                            progress.FileName, status, progress.BytesTransferred, progress.TotalBytes);
                    };
                }

                _encoder = new FFmpegH264Encoder();

                _capturer.FrameCaptured += frame =>
                {
                    // Frame-drop throttling: if encoder is already processing previous frame, drop this one to avoid lag
                    if (Interlocked.CompareExchange(ref _isEncodingFrame, 1, 0) != 0)
                    {
                        frame.Dispose();
                        return;
                    }

                    try
                    {
                        var enc = _encoder?.Encode(frame);
                        if (enc != null && _activeTransport != null)
                        {
                            _ = _activeTransport.SendVideoPacketAsync(enc.Data);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "Error encoding host frame.");
                    }
                    finally
                    {
                        frame.Dispose();
                        Interlocked.Exchange(ref _isEncodingFrame, 0);
                    }
                };

                _capturer.Start(targetFps: 30);
                Logger.Information("Host screen capture started (30 FPS).");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to start host screen streaming.");
            }
        }
    }

    private void HandleIncomingControlMessage(byte channelId, ReadOnlyMemory<byte> data)
    {
        if (channelId == DataChannels.Input && _injector != null)
        {
            if (ProtocolEnvelope.TryDecode(data.Span, out var env, out _))
            {
                switch (env.MessageType)
                {
                    case MessageType.MouseMove:
                        var move = ProtocolSerializer.Deserialize<MouseMoveMessage>(env.Payload);
                        _injector.InjectMouseMove(move.X, move.Y, move.ScreenWidth, move.ScreenHeight);
                        break;

                    case MessageType.MouseButton:
                        var btn = ProtocolSerializer.Deserialize<MouseButtonMessage>(env.Payload);
                        _injector.InjectMouseButton(btn.Button, btn.State);
                        break;

                    case MessageType.MouseWheel:
                        var wheel = ProtocolSerializer.Deserialize<MouseWheelMessage>(env.Payload);
                        _injector.InjectMouseWheel(wheel.DeltaX, wheel.DeltaY);
                        break;

                    case MessageType.KeyState:
                        var key = ProtocolSerializer.Deserialize<KeyMessage>(env.Payload);
                        _injector.InjectKey(key.ScanCode, key.IsExtended, key.State);
                        break;

                    case MessageType.TextUnicode:
                        var textMsg = ProtocolSerializer.Deserialize<UnicodeTextMessage>(env.Payload);
                        if (!string.IsNullOrEmpty(textMsg.Text))
                        {
                            _injector.InjectUnicodeText(textMsg.Text);
                        }
                        break;
                }
            }
        }
    }

    private async void HandleOfferReceived(SignalingSdpMessage offer)
    {
        if (_activeTransport != null && offer.FromConnectionId == _activeViewerConnId)
        {
            try
            {
                if (offer.DtlsFingerprintSignature == null || offer.SenderPublicKey == null ||
                    !DtlsFingerprintProtector.VerifySignedSdp(
                        offer.Sdp,
                        offer.SessionId,
                        offer.SenderDeviceId,
                        offer.TargetConnectionId,
                        offer.DtlsFingerprint,
                        offer.DtlsFingerprintSignature,
                        offer.SenderPublicKey,
                        offer.Timestamp))
                {
                    Logger.Error("SECURITY ALERT: Offer DTLS fingerprint signature missing or invalid! Aborting session.");
                    CloseSession();
                    return;
                }

                await _activeTransport.SetRemoteDescriptionAsync(RTCSdpType.offer, offer.Sdp).ConfigureAwait(false);
                var answerSdp = await _activeTransport.CreateAnswerAsync().ConfigureAwait(false);
                if (_signalingClient != null)
                {
                    var (fingerprint, signature, pubKey, timestamp) = DtlsFingerprintProtector.SignSdp(
                        _identity,
                        offer.SessionId,
                        offer.FromConnectionId,
                        answerSdp);

                    await _signalingClient.SendAnswerAsync(
                        offer.FromConnectionId,
                        offer.SessionId,
                        answerSdp,
                        _identity.DeviceId,
                        pubKey,
                        fingerprint,
                        signature,
                        timestamp).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to process SDP offer.");
                CloseSession();
            }
        }
    }

    private void HandleIceCandidateReceived(SignalingIceCandidateMessage candidate)
    {
        if (_activeTransport != null && candidate.FromConnectionId == _activeViewerConnId)
        {
            _activeTransport.AddIceCandidate(candidate.Candidate, candidate.SdpMid, candidate.SdpMLineIndex);
        }
    }

    public void CloseSession(bool notifyPeer = true)
    {
        string? viewerConnIdToNotify = null;
        WebRtcTransport? transportToDispose = null;

        lock (_sessionLock)
        {
            viewerConnIdToNotify = notifyPeer ? _activeViewerConnId : null;

            _capturer?.Stop();
            _capturer?.Dispose();
            _capturer = null;

            _encoder?.Dispose();
            _encoder = null;

            _killSwitch?.Stop();
            _killSwitch?.Dispose();
            _killSwitch = null;

            _injector = null;

            _fileTransferService?.Dispose();
            _fileTransferService = null;

            if (_activeTransport != null)
            {
                transportToDispose = _activeTransport;
                _activeTransport = null;
            }

            _activeViewerConnId = null;
            _activeViewerDeviceId = null;
            _activeViewerName = null;
            _activeSessionId = null;
        }

        if (transportToDispose != null)
        {
            _ = Task.Run(async () =>
            {
                try { await transportToDispose.DisposeAsync().ConfigureAwait(false); } catch { }
            });
        }

        if (!string.IsNullOrWhiteSpace(viewerConnIdToNotify) && _signalingClient != null)
        {
            var signaling = _signalingClient;
            _ = Task.Run(async () =>
            {
                try
                {
                    await signaling.NotifySessionEndedAsync(viewerConnIdToNotify, "Remote partner ended the session.").ConfigureAwait(false);
                }
                catch { }
            });
        }

        SessionEnded?.Invoke();
        StatusChanged?.Invoke();
        Logger.Information("Host session closed cleanly. Ready for next incoming connection.");
    }

    public async ValueTask DisposeAsync()
    {
        CloseSession();

        if (_signalingClient != null)
        {
            try { await _signalingClient.DisposeAsync().ConfigureAwait(false); } catch { }
            _signalingClient = null;
        }
    }
}
