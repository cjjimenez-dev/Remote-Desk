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
    public event Action? SessionStarted;
    public event Action? SessionEnded;
    public event Action? StatusChanged;

    public bool AutoAcceptConnections { get; set; } = true;

    public HostSessionServer(string hostDeviceId, string deviceName, string signalingUrl, bool forceRelay = false)
    {
        _hostDeviceId = hostDeviceId;
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
            _signalingClient.OfferReceived += HandleOfferReceived;
            _signalingClient.IceCandidateReceived += HandleIceCandidateReceived;

            await _signalingClient.ConnectAsync().ConfigureAwait(false);
            var registered = await _signalingClient.RegisterHostAsync(_hostDeviceId, _deviceName).ConfigureAwait(false);
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

        lock (_sessionLock)
        {
            viewerConnId = _activeViewerConnId;
            sessionId = _activeSessionId;
        }

        if (viewerConnId == null || sessionId == null || _signalingClient == null) return;

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
            await _signalingClient.RejectConnectionAsync(viewerConnId, reason).ConfigureAwait(false);
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

                _capturer = new GdiScreenCapturer();
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
                await _activeTransport.SetRemoteDescriptionAsync(RTCSdpType.offer, offer.Sdp).ConfigureAwait(false);
                var answerSdp = await _activeTransport.CreateAnswerAsync().ConfigureAwait(false);
                if (_signalingClient != null)
                {
                    await _signalingClient.SendAnswerAsync(offer.FromConnectionId, offer.SessionId, answerSdp).ConfigureAwait(false);
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

    public void CloseSession()
    {
        lock (_sessionLock)
        {
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
                try { _ = _activeTransport.DisposeAsync(); } catch { }
                _activeTransport = null;
            }

            _activeViewerConnId = null;
            _activeViewerDeviceId = null;
            _activeViewerName = null;
            _activeSessionId = null;
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
