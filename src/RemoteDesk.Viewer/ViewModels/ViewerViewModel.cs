using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteDesk.Core.Interfaces;
using RemoteDesk.Core.Models;
using RemoteDesk.Core.Protocol;
using RemoteDesk.Platform.Windows;
using RemoteDesk.Transport.WebRtc;
using RemoteDesk.Viewer.Rendering;
using Serilog;
using SIPSorcery.Net;

namespace RemoteDesk.Viewer.ViewModels;

public partial class ViewerViewModel : ObservableObject, IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<ViewerViewModel>();

    private readonly Dispatcher _dispatcher;
    private readonly WriteableBitmapVideoRenderer _renderer;

    // Local Loopback components
    private IScreenCapturer? _capturer;
    private IVideoEncoder? _encoder;
    private IInputInjector? _injector;
    private EmergencyKillSwitch? _killSwitch;
    private CursorTracker? _cursorTracker;

    // Remote WebRTC components
    private SignalingClient? _signalingClient;
    private WebRtcTransport? _transport;
    private IVideoDecoder? _decoder;
    private string? _activeHostConnId;

    private readonly DispatcherTimer _statsTimer;
    private int _framesThisInterval;
    private long _bytesThisInterval;
    private double _sumEncodeMs;
    private double _sumDecodeMs;
    private double _sumLatencyMs;
    private int _measurementCount;

    [ObservableProperty]
    private WriteableBitmap? _videoImage;

    [ObservableProperty]
    private bool _isLoopbackActive;

    [ObservableProperty]
    private bool _isRemoteSessionActive;

    [ObservableProperty]
    private bool _isConnecting;

    public bool IsSessionActive => IsLoopbackActive || IsRemoteSessionActive;

    [ObservableProperty]
    private bool _isInputEnabled = true;

    [ObservableProperty]
    private bool _isEmergencyKillTriggered;

    [ObservableProperty]
    private string _targetHostId = "123-456";

    [ObservableProperty]
    private string _signalingServerUrl = "http://localhost:5000/signaling";

    [ObservableProperty]
    private string _viewerName = Environment.MachineName;

    [ObservableProperty]
    private bool _forceRelay;

    [ObservableProperty]
    private ConnectionPath _connectionPath = ConnectionPath.Unknown;

    [ObservableProperty]
    private string _connectionPathBadge = "🟢 Direct P2P";

    [ObservableProperty]
    private string _connectionPathColor = "#10B981";

    [ObservableProperty]
    private string _fpsText = "0.0 FPS";

    [ObservableProperty]
    private string _latencyText = "0.0 ms";

    [ObservableProperty]
    private string _encodeDecodeText = "Enc: 0.0ms | Dec: 0.0ms";

    [ObservableProperty]
    private string _bitrateText = "0 kbps";

    [ObservableProperty]
    private string _resolutionText = "0 x 0";

    [ObservableProperty]
    private string _statusText = "Ready - Enter Host ID to connect or Start Loopback";

    [ObservableProperty]
    private string _cursorPositionText = "X: 0, Y: 0";

    [ObservableProperty]
    private int _selectedDisplayIndex;

    public ObservableCollection<string> AvailableDisplays { get; } = [];

    public IInputInjector? Injector => _injector;

    public ViewerViewModel(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _renderer = new WriteableBitmapVideoRenderer(dispatcher);
        _renderer.FrameRendered += () =>
        {
            if (VideoImage != _renderer.BitmapSource)
            {
                VideoImage = _renderer.BitmapSource;
            }
        };

        _statsTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _statsTimer.Tick += OnStatsTimerTick;

        LoadDisplays();
    }

    partial void OnIsLoopbackActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSessionActive));
    }

    partial void OnIsRemoteSessionActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSessionActive));
    }

    private void LoadDisplays()
    {
        try
        {
            using var capturer = new GdiScreenCapturer();
            var displays = capturer.GetDisplays();
            AvailableDisplays.Clear();
            for (var i = 0; i < displays.Count; i++)
            {
                var d = displays[i];
                AvailableDisplays.Add($"[{d.Id}] {d.DeviceName} ({d.Width}x{d.Height})");
            }

            if (AvailableDisplays.Count > 0)
            {
                SelectedDisplayIndex = 0;
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to enumerate displays.");
        }
    }

    [RelayCommand]
    private async Task ConnectRemoteAsync()
    {
        if (IsRemoteSessionActive || IsConnecting)
        {
            DisconnectRemote();
            return;
        }

        if (IsLoopbackActive)
        {
            StopLoopback();
        }

        if (string.IsNullOrWhiteSpace(TargetHostId))
        {
            StatusText = "Please enter a valid Target Host ID.";
            return;
        }

        IsConnecting = true;
        StatusText = $"Connecting to signaling server at {SignalingServerUrl}...";

        try
        {
            FFmpegBootstrapper.Initialize();
            _decoder = new FFmpegH264Decoder();

            _signalingClient = new SignalingClient(SignalingServerUrl);

            _signalingClient.ConnectionRejected += reason =>
            {
                _dispatcher.Invoke(() =>
                {
                    StatusText = $"Connection rejected: {reason}";
                    DisconnectRemote();
                });
            };

            _signalingClient.ConnectionAccepted += async (sessionId, hostConnId) =>
            {
                _activeHostConnId = hostConnId;
                _dispatcher.Invoke(() => StatusText = $"Host accepted connection [{sessionId}]. Negotiating WebRTC P2P...");

                try
                {
                    // Fetch dynamic STUN/TURN servers from broker
                    List<IceServerConfig>? iceServers = null;
                    try
                    {
                        iceServers = await _signalingClient.GetIceServersAsync(TargetHostId);
                        Logger.Information("Retrieved {Count} ICE servers from signaling broker.", iceServers.Count);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning("Could not retrieve ICE servers ({Message}). Using defaults.", ex.Message);
                    }

                    // Create Initiator WebRTC transport with STUN/TURN & optional forced relay
                    _transport = new WebRtcTransport(isInitiator: true, iceServers: iceServers, forceRelay: ForceRelay);

                    _transport.ConnectionPathChanged += path =>
                    {
                        _dispatcher.Invoke(() =>
                        {
                            ConnectionPath = path;
                            switch (path)
                            {
                                case ConnectionPath.DirectLocal:
                                    ConnectionPathBadge = "🟢 Direct LAN";
                                    ConnectionPathColor = "#10B981";
                                    break;
                                case ConnectionPath.DirectStunHolePunch:
                                    ConnectionPathBadge = "🟢 Direct P2P (STUN)";
                                    ConnectionPathColor = "#059669";
                                    break;
                                case ConnectionPath.TurnRelayed:
                                    ConnectionPathBadge = "🟠 TURN Relayed";
                                    ConnectionPathColor = "#F59E0B";
                                    break;
                                default:
                                    ConnectionPathBadge = "⚪ P2P";
                                    ConnectionPathColor = "#6B7280";
                                    break;
                            }
                            StatusText = $"Connected to [{TargetHostId}] via WebRTC ({ConnectionPathBadge})";
                        });
                    };

                    _transport.IceCandidateGenerated += (candidate, mid, line) =>
                    {
                        if (_signalingClient != null && _activeHostConnId != null)
                        {
                            _ = _signalingClient.SendIceCandidateAsync(_activeHostConnId, candidate, mid, line);
                        }
                    };

                    _transport.StateChanged += state =>
                    {
                        _dispatcher.Invoke(() =>
                        {
                            Logger.Information("Remote WebRTC state: {State}", state);
                            if (state == TransportState.Connected)
                            {
                                IsRemoteSessionActive = true;
                                IsConnecting = false;
                                IsInputEnabled = true;
                                StatusText = $"Connected to [{TargetHostId}] via WebRTC (P2P direct)";
                                _statsTimer.Start();
                            }
                            else if (state is TransportState.Disconnected or TransportState.Failed or TransportState.Closed)
                            {
                                StatusText = $"Session disconnected ({state}).";
                                DisconnectRemote();
                            }
                        });
                    };

                    _transport.VideoPacketReceived += packet =>
                    {
                        OnRemoteVideoPacketReceived(packet);
                    };

                    // Create and send SDP Offer
                    var offerSdp = await _transport.CreateOfferAsync();
                    await _signalingClient.SendOfferAsync(hostConnId, offerSdp);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Failed to establish WebRTC transport.");
                    _dispatcher.Invoke(() =>
                    {
                        StatusText = $"WebRTC negotiation failed: {ex.Message}";
                        DisconnectRemote();
                    });
                }
            };

            _signalingClient.AnswerReceived += async (fromConnId, sdp) =>
            {
                if (fromConnId == _activeHostConnId && _transport != null)
                {
                    Logger.Information("Received SDP answer from Host.");
                    await _transport.SetRemoteDescriptionAsync(RTCSdpType.answer, sdp);
                }
            };

            _signalingClient.IceCandidateReceived += (fromConnId, candidate, sdpMid, sdpMLineIndex) =>
            {
                if (fromConnId == _activeHostConnId && _transport != null)
                {
                    _transport.AddIceCandidate(candidate, sdpMid, sdpMLineIndex);
                }
            };

            await _signalingClient.ConnectAsync();
            StatusText = $"Requesting session with Host [{TargetHostId}]...";
            await _signalingClient.RequestConnectionAsync(TargetHostId, ViewerName);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to connect to host.");
            StatusText = $"Connection failed: {ex.Message}";
            DisconnectRemote();
        }
    }

    [RelayCommand]
    private void DisconnectRemote()
    {
        _statsTimer.Stop();

        if (_transport != null)
        {
            _transport.CloseAsync().GetAwaiter().GetResult();
            _transport = null;
        }

        if (_signalingClient != null)
        {
            _signalingClient.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _signalingClient = null;
        }

        _decoder?.Dispose();
        _decoder = null;

        _activeHostConnId = null;
        IsRemoteSessionActive = false;
        IsConnecting = false;
        StatusText = "Disconnected.";
    }

    private void OnRemoteVideoPacketReceived(ReadOnlyMemory<byte> packet)
    {
        Interlocked.Increment(ref _framesThisInterval);
        Interlocked.Add(ref _bytesThisInterval, packet.Length);

        if (_decoder != null)
        {
            var decSw = Stopwatch.StartNew();
            using var decoded = _decoder.Decode(packet);
            decSw.Stop();

            if (decoded != null)
            {
                lock (_statsTimer)
                {
                    _sumDecodeMs += decSw.Elapsed.TotalMilliseconds;
                    _measurementCount++;
                }

                _renderer.RenderDecoded(decoded);
            }
        }
    }

    public void SendRemoteMouseMove(int x, int y, int screenW, int screenH)
    {
        if (!IsRemoteSessionActive || _transport == null || !IsInputEnabled) return;

        var msg = new MouseMoveMessage
        {
            X = x,
            Y = y,
            ScreenWidth = screenW,
            ScreenHeight = screenH
        };

        var envelope = new ProtocolEnvelope(DataChannels.Input, MessageType.MouseMove, ProtocolSerializer.Serialize(msg));
        _ = _transport.SendMessageAsync(DataChannels.Input, envelope.ToBytes(), reliable: false);
    }

    public void SendRemoteMouseButton(RemoteDesk.Core.Models.MouseButton button, ButtonState state)
    {
        if (!IsRemoteSessionActive || _transport == null || !IsInputEnabled) return;

        var msg = new MouseButtonMessage
        {
            Button = button,
            State = state
        };

        var envelope = new ProtocolEnvelope(DataChannels.Input, MessageType.MouseButton, ProtocolSerializer.Serialize(msg));
        _ = _transport.SendMessageAsync(DataChannels.Input, envelope.ToBytes(), reliable: true);
    }

    public void SendRemoteMouseWheel(int deltaX, int deltaY)
    {
        if (!IsRemoteSessionActive || _transport == null || !IsInputEnabled) return;

        var msg = new MouseWheelMessage
        {
            DeltaX = deltaX,
            DeltaY = deltaY
        };

        var envelope = new ProtocolEnvelope(DataChannels.Input, MessageType.MouseWheel, ProtocolSerializer.Serialize(msg));
        _ = _transport.SendMessageAsync(DataChannels.Input, envelope.ToBytes(), reliable: true);
    }

    public void SendRemoteKey(ushort scanCode, bool isExtended, KeyState state)
    {
        if (!IsRemoteSessionActive || _transport == null || !IsInputEnabled) return;

        var msg = new KeyMessage
        {
            ScanCode = scanCode,
            IsExtended = isExtended,
            State = state
        };

        var envelope = new ProtocolEnvelope(DataChannels.Input, MessageType.KeyState, ProtocolSerializer.Serialize(msg));
        _ = _transport.SendMessageAsync(DataChannels.Input, envelope.ToBytes(), reliable: true);
    }

    public void SendRemoteUnicodeText(string text)
    {
        if (!IsRemoteSessionActive || _transport == null || !IsInputEnabled) return;

        var msg = new UnicodeTextMessage
        {
            Text = text
        };

        var envelope = new ProtocolEnvelope(DataChannels.Input, MessageType.TextUnicode, ProtocolSerializer.Serialize(msg));
        _ = _transport.SendMessageAsync(DataChannels.Input, envelope.ToBytes(), reliable: true);
    }

    [RelayCommand]
    private void ToggleLoopback()
    {
        if (IsLoopbackActive)
        {
            StopLoopback();
        }
        else
        {
            StartLoopback();
        }
    }

    [RelayCommand]
    private void ResetEmergencyKill()
    {
        if (_injector != null)
        {
            _injector.IsEnabled = true;
        }
        IsInputEnabled = true;
        IsEmergencyKillTriggered = false;
        StatusText = "Input control resumed.";
    }

    private void StartLoopback()
    {
        try
        {
            if (IsRemoteSessionActive)
            {
                DisconnectRemote();
            }

            FFmpegBootstrapper.Initialize();

            _capturer = new GdiScreenCapturer();
            _capturer.SetTargetDisplay(SelectedDisplayIndex);

            _encoder = new FFmpegH264Encoder();
            _decoder = new FFmpegH264Decoder();
            _injector = new WindowsInputInjector();

            var currentDisplay = _capturer.CurrentDisplay;
            if (currentDisplay != null)
            {
                _injector.SetTargetDisplay(currentDisplay);
            }

            _killSwitch = new EmergencyKillSwitch(_injector);
            _killSwitch.EmergencyKillTriggered += () =>
            {
                _dispatcher.Invoke(() =>
                {
                    IsEmergencyKillTriggered = true;
                    IsInputEnabled = false;
                    StatusText = "EMERGENCY KILL TRIGGERED: Input disabled by host user.";
                });
            };
            _killSwitch.Start();

            _cursorTracker = new CursorTracker();
            _cursorTracker.CursorPositionChanged += pos =>
            {
                _dispatcher.Invoke(() => CursorPositionText = $"X: {pos.X}, Y: {pos.Y}");
            };
            _cursorTracker.Start();

            _capturer.FrameCaptured += OnLoopbackFrameCaptured;
            _capturer.ErrorOccurred += err =>
            {
                _dispatcher.Invoke(() => StatusText = $"Error: {err}");
            };

            _capturer.Start(targetFps: 30);
            _statsTimer.Start();

            IsLoopbackActive = true;
            IsEmergencyKillTriggered = false;
            IsInputEnabled = true;
            StatusText = "Local loopback running with active input injection. Hotkey: Ctrl+Alt+Shift+Esc to kill.";
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to start loopback pipeline.");
            StatusText = $"Error starting loopback: {ex.Message}";
        }
    }

    private void OnLoopbackFrameCaptured(CapturedFrame frame)
    {
        var frameTimestamp = frame.TimestampTicks;
        Interlocked.Increment(ref _framesThisInterval);

        // 1. Encode
        var encSw = Stopwatch.StartNew();
        var encoded = _encoder?.Encode(frame);
        encSw.Stop();
        var encMs = encSw.Elapsed.TotalMilliseconds;

        if (encoded != null)
        {
            Interlocked.Add(ref _bytesThisInterval, encoded.Data.Length);

            // 2. Decode
            var decSw = Stopwatch.StartNew();
            var decoded = _decoder?.Decode(encoded.Data);
            decSw.Stop();
            var decMs = decSw.Elapsed.TotalMilliseconds;

            if (decoded != null)
            {
                var totalLatencyMs = (DateTime.UtcNow.Ticks - frameTimestamp) / (double)TimeSpan.TicksPerMillisecond;

                lock (_statsTimer)
                {
                    _sumEncodeMs += encMs;
                    _sumDecodeMs += decMs;
                    _sumLatencyMs += totalLatencyMs;
                    _measurementCount++;
                }

                _renderer.RenderDecoded(decoded);
                decoded.Dispose();
            }
        }

        frame.Dispose();
    }

    private void OnStatsTimerTick(object? sender, EventArgs e)
    {
        var fps = _framesThisInterval;
        var bytes = _bytesThisInterval;
        _framesThisInterval = 0;
        _bytesThisInterval = 0;

        double avgEnc = 0, avgDec = 0, avgLat = 0;
        lock (_statsTimer)
        {
            if (_measurementCount > 0)
            {
                avgEnc = _sumEncodeMs / _measurementCount;
                avgDec = _sumDecodeMs / _measurementCount;
                avgLat = _sumLatencyMs / _measurementCount;
            }

            _sumEncodeMs = 0;
            _sumDecodeMs = 0;
            _sumLatencyMs = 0;
            _measurementCount = 0;
        }

        var bitrateKbps = (bytes * 8) / 1000.0;

        FpsText = $"{fps:F1} FPS";
        BitrateText = $"{bitrateKbps:F0} kbps";
        EncodeDecodeText = IsRemoteSessionActive ? $"Dec: {avgDec:F1}ms" : $"Enc: {avgEnc:F1}ms | Dec: {avgDec:F1}ms";
        LatencyText = IsRemoteSessionActive ? "WebRTC P2P" : $"E2E Latency: {avgLat:F1}ms";

        if (_renderer.BitmapSource != null)
        {
            ResolutionText = $"{_renderer.BitmapSource.PixelWidth} x {_renderer.BitmapSource.PixelHeight}";
        }
    }

    private void StopLoopback()
    {
        _statsTimer.Stop();

        _killSwitch?.Stop();
        _killSwitch?.Dispose();
        _killSwitch = null;

        _cursorTracker?.Stop();
        _cursorTracker?.Dispose();
        _cursorTracker = null;

        if (_capturer != null)
        {
            _capturer.Stop();
            _capturer.Dispose();
            _capturer = null;
        }

        _encoder?.Dispose();
        _encoder = null;

        _decoder?.Dispose();
        _decoder = null;

        _injector = null;

        IsLoopbackActive = false;
        StatusText = "Loopback stopped.";
    }

    public void Dispose()
    {
        DisconnectRemote();
        StopLoopback();
        _renderer.Dispose();
    }
}
