using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteDesk.Core.Interfaces;
using RemoteDesk.Core.Models;
using RemoteDesk.Core.Protocol;
using RemoteDesk.Core.Security;
using RemoteDesk.Core.Quality;
using RemoteDesk.Core.Services;
using RemoteDesk.Core.Terminal;
using RemoteDesk.Platform.Windows;
using RemoteDesk.Platform.Windows.Audio;
using RemoteDesk.Platform.Windows.Clipboard;
using RemoteDesk.Platform.Windows.Security;
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
    private readonly DeviceIdentity _identity;

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
    private string? _activeSessionId;
    private string? _activeHostDeviceId;
    private SessionPermissions _grantedPermissions = SessionPermissions.All;

    private readonly DispatcherTimer _statsTimer;
    private int _framesThisInterval;
    private long _bytesThisInterval;

    [ObservableProperty]
    private string _localDeviceId = string.Empty;

    [ObservableProperty]
    private string _securityStatusBadge = "🔒 Disconnected";

    [ObservableProperty]
    private string _securityStatusColor = "#6B7280";

    [ObservableProperty]
    private string _grantedPermissionsText = "Full Access";
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

    // --- Phase 7 Core Features ---
    private WindowsClipboardService? _clipboardService;
    private FileTransferService? _fileTransferService;
    private WasapiAudioPlayer? _audioPlayer;
    private ChatService? _chatService;
    private readonly AddressBookService _addressBookService;

    // File Transfer
    [ObservableProperty]
    private bool _isFileTransferActive;

    [ObservableProperty]
    private string _fileTransferStatusText = string.Empty;

    [ObservableProperty]
    private double _fileTransferProgressPercent;

    [ObservableProperty]
    private string? _activeFileTransferId;

    // Audio
    [ObservableProperty]
    private bool _isAudioMuted;

    public string AudioButtonText => IsAudioMuted ? "🔇 Muted" : "🔊 Audio";

    partial void OnIsAudioMutedChanged(bool value)
    {
        OnPropertyChanged(nameof(AudioButtonText));
    }

    [ObservableProperty]
    private float _audioVolume = 1.0f;

    // Multi-Monitor
    public ObservableCollection<DisplayInfo> RemoteDisplays { get; } = [];

    [ObservableProperty]
    private DisplayInfo? _selectedRemoteDisplay;

    partial void OnSelectedRemoteDisplayChanged(DisplayInfo? value)
    {
        if (value != null && IsRemoteSessionActive)
        {
            _ = SwitchRemoteDisplayAsync(value);
        }
    }

    // Adaptive Quality Presets
    public ObservableCollection<QualityPreset> AvailableQualityPresets { get; } = [
        QualityPreset.Speed,
        QualityPreset.Balanced,
        QualityPreset.Quality
    ];

    [ObservableProperty]
    private QualityPreset _selectedQualityPreset = QualityPreset.Balanced;

    partial void OnSelectedQualityPresetChanged(QualityPreset value)
    {
        if (IsRemoteSessionActive)
        {
            _ = ChangeQualityPresetAsync(value);
        }
    }

    // Chat
    public ObservableCollection<SessionChatMessage> ChatMessages { get; } = [];

    [ObservableProperty]
    private string _chatInputText = string.Empty;

    [ObservableProperty]
    private bool _isChatPaneOpen;

    [ObservableProperty]
    private int _unreadChatCount;

    public string UnreadChatBadgeText => UnreadChatCount > 0 ? $" ({UnreadChatCount})" : string.Empty;

    partial void OnUnreadChatCountChanged(int value)
    {
        OnPropertyChanged(nameof(UnreadChatBadgeText));
    }

    // Address Book
    public ObservableCollection<AddressBookEntry> AddressBookEntries { get; } = [];

    [ObservableProperty]
    private bool _isAddressBookOpen;

    // --- Phase 8 Remote Terminal ---
    private TerminalClientService? _terminalClientService;
    private readonly TerminalBuffer _terminalBuffer = new();

    [ObservableProperty]
    private bool _isTerminalOpen;

    [ObservableProperty]
    private bool _isTerminalSessionActive;

    [ObservableProperty]
    private string _terminalOutputText = string.Empty;

    [ObservableProperty]
    private string _terminalInputText = string.Empty;

    [ObservableProperty]
    private ShellType _selectedShellType = ShellType.PowerShell;

    public ObservableCollection<ShellType> AvailableShells { get; } = [ShellType.PowerShell, ShellType.Cmd];

    public ViewerViewModel(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _addressBookService = new AddressBookService();
        var keyStore = new DpapiDeviceKeyStore();
        _identity = new DeviceIdentity(keyStore);
        LocalDeviceId = _identity.DeviceId;

        _ = LoadAddressBookAsync();

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

            _signalingClient.ConnectionAccepted += async res =>
            {
                _activeHostConnId = res.HostConnectionId;
                _activeSessionId = res.SessionId;
                _activeHostDeviceId = res.HostDeviceId;
                _grantedPermissions = res.GrantedPermissions;

                _dispatcher.Invoke(() =>
                {
                    GrantedPermissionsText = _grantedPermissions.ToString();
                    StatusText = $"Host [{res.HostDeviceId}] accepted connection [{res.SessionId}]. Negotiating WebRTC...";
                });

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
                                IsInputEnabled = _grantedPermissions.HasFlag(SessionPermissions.Control);
                                StatusText = $"Connected to [{TargetHostId}] via WebRTC ({ConnectionPathBadge})";
                                _statsTimer.Start();

                                // 1. Record connection in Address Book
                                _ = _addressBookService.RecordConnectionAsync(TargetHostId);

                                // 2. Clipboard Sync
                                if (_grantedPermissions.HasFlag(SessionPermissions.Clipboard))
                                {
                                    _clipboardService = new WindowsClipboardService();
                                    long clipSeq = 0;
                                    _clipboardService.RemoteClipboardChanged += clipData =>
                                    {
                                        if (_transport == null) return;
                                        if (clipData.Format == ClipboardFormat.Text && clipData.Text != null)
                                        {
                                            var msg = new ClipboardTextMessage
                                            {
                                                Text = clipData.Text,
                                                ContentHash = WindowsClipboardService.ComputeHash(clipData),
                                                SequenceNumber = Interlocked.Increment(ref clipSeq)
                                            };
                                            var env = new ProtocolEnvelope(DataChannels.Clipboard, MessageType.ClipboardText, ProtocolSerializer.Serialize(msg));
                                            _ = _transport.SendMessageAsync(DataChannels.Clipboard, env.ToBytes(), reliable: true);
                                        }
                                        else if (clipData.Format == ClipboardFormat.ImagePng && clipData.Data.Length > 0)
                                        {
                                            var msg = new ClipboardImageMessage
                                            {
                                                ImageData = clipData.Data,
                                                Format = "image/png",
                                                ContentHash = WindowsClipboardService.ComputeHash(clipData)
                                            };
                                            var env = new ProtocolEnvelope(DataChannels.Clipboard, MessageType.ClipboardImage, ProtocolSerializer.Serialize(msg));
                                            _ = _transport.SendMessageAsync(DataChannels.Clipboard, env.ToBytes(), reliable: true);
                                        }
                                    };
                                    _clipboardService.StartMonitoring();
                                }

                                // 3. File Transfer Service
                                if (_grantedPermissions.HasFlag(SessionPermissions.Files))
                                {
                                    _fileTransferService = new FileTransferService(_transport);
                                    _fileTransferService.ProgressUpdated += progress =>
                                    {
                                        _dispatcher.Invoke(() =>
                                        {
                                            if (progress.IsCompleted)
                                            {
                                                IsFileTransferActive = false;
                                                FileTransferStatusText = $"Transfer complete: {progress.FileName}";
                                                FileTransferProgressPercent = 100;
                                            }
                                            else if (!string.IsNullOrEmpty(progress.ErrorMessage))
                                            {
                                                IsFileTransferActive = false;
                                                FileTransferStatusText = $"Transfer error: {progress.ErrorMessage}";
                                            }
                                            else
                                            {
                                                IsFileTransferActive = true;
                                                ActiveFileTransferId = progress.TransferId;
                                                double percent = progress.TotalBytes > 0 ? (progress.BytesTransferred * 100.0) / progress.TotalBytes : 0;
                                                FileTransferProgressPercent = percent;
                                                double speedMb = progress.SpeedBytesPerSecond / (1024 * 1024);
                                                FileTransferStatusText = $"{progress.FileName}: {percent:F1}% ({speedMb:F1} MB/s)";
                                            }
                                        });
                                    };
                                }

                                // 4. Audio Playback
                                if (_grantedPermissions.HasFlag(SessionPermissions.Audio))
                                {
                                    _audioPlayer = new WasapiAudioPlayer
                                    {
                                        Volume = AudioVolume,
                                        IsMuted = IsAudioMuted
                                    };
                                }

                                // 5. In-Session Chat
                                _chatService = new ChatService(_transport, _identity.DeviceId, ViewerName);
                                _chatService.MessageReceived += msg =>
                                {
                                    _dispatcher.Invoke(() =>
                                    {
                                        ChatMessages.Add(msg);
                                        if (!IsChatPaneOpen)
                                        {
                                            UnreadChatCount++;
                                        }
                                    });
                                };

                                // 6. Remote Terminal
                                if (_grantedPermissions.HasFlag(SessionPermissions.Terminal))
                                {
                                    _terminalClientService = new TerminalClientService(_transport);
                                    _terminalClientService.OutputReceived += text =>
                                    {
                                        _dispatcher.Invoke(() =>
                                        {
                                            _terminalBuffer.Append(text);
                                            TerminalOutputText = _terminalBuffer.GetFullText();
                                        });
                                    };
                                    _terminalClientService.Closed += reason =>
                                    {
                                        _dispatcher.Invoke(() =>
                                        {
                                            IsTerminalSessionActive = false;
                                            _terminalBuffer.Append($"\r\n[Terminal closed: {reason}]\r\n");
                                            TerminalOutputText = _terminalBuffer.GetFullText();
                                        });
                                    };
                                }
                            }
                            else if (state is TransportState.Disconnected or TransportState.Failed or TransportState.Closed)
                            {
                                StatusText = $"Session disconnected ({state}).";
                                DisconnectRemote();
                            }
                        });
                    };

                    _transport.MessageReceived += (channel, data) =>
                    {
                        if (!ProtocolEnvelope.TryDecode(data.Span, out var env, out _)) return;

                        if (channel == DataChannels.Clipboard && _clipboardService != null)
                        {
                            if (env.MessageType == MessageType.ClipboardText)
                            {
                                var txt = ProtocolSerializer.Deserialize<ClipboardTextMessage>(env.Payload);
                                _ = _clipboardService.SetClipboardAsync(new ClipboardData(ClipboardFormat.Text, System.Text.Encoding.UTF8.GetBytes(txt.Text), txt.Text));
                            }
                            else if (env.MessageType == MessageType.ClipboardImage)
                            {
                                var img = ProtocolSerializer.Deserialize<ClipboardImageMessage>(env.Payload);
                                _ = _clipboardService.SetClipboardAsync(new ClipboardData(ClipboardFormat.ImagePng, img.ImageData));
                            }
                        }
                        else if (channel == DataChannels.Audio && _audioPlayer != null)
                        {
                            if (env.MessageType == MessageType.AudioPacket)
                            {
                                var packet = ProtocolSerializer.Deserialize<AudioPacketMessage>(env.Payload);
                                _audioPlayer.PlaySample(packet.AudioData, packet.SampleRate, packet.Channels);
                            }
                        }
                        else if (channel == DataChannels.Control)
                        {
                            if (env.MessageType == MessageType.DisplayList)
                            {
                                var list = ProtocolSerializer.Deserialize<DisplayListMessage>(env.Payload);
                                _dispatcher.Invoke(() =>
                                {
                                    RemoteDisplays.Clear();
                                    foreach (var d in list.Displays)
                                    {
                                        RemoteDisplays.Add(d);
                                    }
                                    if (RemoteDisplays.Count > 0)
                                    {
                                        SelectedRemoteDisplay = RemoteDisplays.FirstOrDefault(d => d.Id == list.ActiveDisplayId) ?? RemoteDisplays[0];
                                    }
                                });
                            }
                        }
                    };

                    _transport.VideoPacketReceived += packet =>
                    {
                        OnRemoteVideoPacketReceived(packet);
                    };

                    // Create and cryptographically sign SDP Offer
                    var offerSdp = await _transport.CreateOfferAsync();
                    var (fingerprint, signature, pubKey, timestamp) = DtlsFingerprintProtector.SignSdp(
                        _identity,
                        res.SessionId,
                        res.HostConnectionId,
                        offerSdp);

                    await _signalingClient.SendOfferAsync(
                        res.HostConnectionId,
                        res.SessionId,
                        offerSdp,
                        _identity.DeviceId,
                        pubKey,
                        fingerprint,
                        signature,
                        timestamp);
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

            _signalingClient.AnswerReceived += async answer =>
            {
                if (answer.FromConnectionId == _activeHostConnId && _transport != null)
                {
                    Logger.Information("Received SDP answer from Host. Verifying DTLS fingerprint signature...");

                    if (answer.DtlsFingerprintSignature != null && answer.SenderPublicKey != null)
                    {
                        var valid = DtlsFingerprintProtector.VerifySignedSdp(
                            answer.Sdp,
                            answer.SessionId,
                            answer.SenderDeviceId,
                            answer.TargetConnectionId,
                            answer.DtlsFingerprint,
                            answer.DtlsFingerprintSignature,
                            answer.SenderPublicKey,
                            answer.Timestamp);

                        if (!valid)
                        {
                            Logger.Error("SECURITY ALERT: Host SDP Answer DTLS fingerprint signature mismatch or expired! Possible MITM tampering.");
                            _dispatcher.Invoke(() =>
                            {
                                StatusText = "SECURITY ALERT: SDP Answer DTLS fingerprint signature mismatch! Session aborted.";
                                SecurityStatusBadge = "⚠️ MITM Tampering Detected";
                                SecurityStatusColor = "#EF4444";
                                DisconnectRemote();
                            });
                            return;
                        }

                        Logger.Information(">>> Host DTLS Fingerprint signature successfully verified against Host ECDSA public key!");
                        _dispatcher.Invoke(() =>
                        {
                            SecurityStatusBadge = "🔒 Verified E2EE (ECDSA)";
                            SecurityStatusColor = "#10B981";
                        });
                    }

                    await _transport.SetRemoteDescriptionAsync(RTCSdpType.answer, answer.Sdp);
                }
            };

            _signalingClient.IceCandidateReceived += ice =>
            {
                if (ice.FromConnectionId == _activeHostConnId && _transport != null)
                {
                    _transport.AddIceCandidate(ice.Candidate, ice.SdpMid, (ushort)ice.SdpMLineIndex);
                }
            };

            await _signalingClient.ConnectAsync();
            StatusText = $"Requesting authenticated session with Host [{TargetHostId}]...";

            // Sign connection request with Viewer DeviceIdentity
            var reqTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var reqPayload = $"{TargetHostId}:{_identity.DeviceId}:{reqTimestamp}";
            var reqSig = _identity.Sign(reqPayload);

            await _signalingClient.RequestConnectionAsync(
                TargetHostId,
                ViewerName,
                _identity.DeviceId,
                _identity.PublicKeyBytes,
                reqSig,
                reqTimestamp);
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
        _activeSessionId = null;
        _activeHostDeviceId = null;
        IsRemoteSessionActive = false;
        IsConnecting = false;
        IsInputEnabled = false;
        ConnectionPath = ConnectionPath.Unknown;
        ConnectionPathBadge = "⚪ P2P";
        ConnectionPathColor = "#6B7280";
        SecurityStatusBadge = "🔒 Disconnected";
        SecurityStatusColor = "#6B7280";
        StatusText = "Disconnected.";

        _clipboardService?.StopMonitoring();
        _clipboardService?.Dispose();
        _clipboardService = null;

        _fileTransferService?.Dispose();
        _fileTransferService = null;

        _audioPlayer?.Dispose();
        _audioPlayer = null;

        _chatService?.Dispose();
        _chatService = null;

        _terminalClientService?.Dispose();
        _terminalClientService = null;

        _dispatcher.Invoke(() =>
        {
            RemoteDisplays.Clear();
            SelectedRemoteDisplay = null;
            IsFileTransferActive = false;
            IsTerminalSessionActive = false;
        });
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

        // Send adaptive quality metrics report to Host
        if (IsRemoteSessionActive && _transport != null)
        {
            var report = new QualityMetricsReportMessage
            {
                RttMs = 25.0,
                PacketLossPercent = 0.0,
                CurrentFps = fps,
                CurrentBitrateKbps = (int)bitrateKbps,
                DecodeLatencyMs = avgDec
            };
            var env = new ProtocolEnvelope(DataChannels.Control, MessageType.QualityMetricsReport, ProtocolSerializer.Serialize(report));
            _ = _transport.SendMessageAsync(DataChannels.Control, env.ToBytes(), reliable: false);
        }
    }

    [RelayCommand]
    private async Task SendFilePromptAsync()
    {
        if (_fileTransferService == null || !IsRemoteSessionActive) return;

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select File to Send to Remote Host"
        };

        if (dlg.ShowDialog() == true)
        {
            try
            {
                IsFileTransferActive = true;
                FileTransferStatusText = $"Initiating transfer for {System.IO.Path.GetFileName(dlg.FileName)}...";
                ActiveFileTransferId = await _fileTransferService.SendFileAsync(dlg.FileName);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to initiate file transfer.");
                FileTransferStatusText = $"Error: {ex.Message}";
                IsFileTransferActive = false;
            }
        }
    }

    [RelayCommand]
    private async Task CancelFileTransferAsync()
    {
        if (_fileTransferService != null && !string.IsNullOrEmpty(ActiveFileTransferId))
        {
            await _fileTransferService.CancelTransferAsync(ActiveFileTransferId);
            IsFileTransferActive = false;
            FileTransferStatusText = "Transfer cancelled.";
        }
    }

    [RelayCommand]
    private void ToggleAudioMute()
    {
        IsAudioMuted = !IsAudioMuted;
        if (_audioPlayer != null)
        {
            _audioPlayer.IsMuted = IsAudioMuted;
        }
    }

    partial void OnAudioVolumeChanged(float value)
    {
        if (_audioPlayer != null)
        {
            _audioPlayer.Volume = value;
        }
    }

    [RelayCommand]
    private async Task SwitchRemoteDisplayAsync(DisplayInfo? display)
    {
        if (display == null || _transport == null || !IsRemoteSessionActive) return;

        SelectedRemoteDisplay = display;
        var msg = new SwitchDisplayMessage
        {
            TargetDisplayId = display.Id
        };
        var env = new ProtocolEnvelope(DataChannels.Control, MessageType.SwitchDisplay, ProtocolSerializer.Serialize(msg));
        await _transport.SendMessageAsync(DataChannels.Control, env.ToBytes(), reliable: true);
        Logger.Information("Requested remote host switch to Display [{Id}] ({Name})", display.Id, display.DeviceName);
    }

    [RelayCommand]
    private async Task ChangeQualityPresetAsync(QualityPreset preset)
    {
        SelectedQualityPreset = preset;
        if (_transport != null && IsRemoteSessionActive)
        {
            var msg = new QualitySettingsUpdateMessage
            {
                Preset = preset
            };
            var env = new ProtocolEnvelope(DataChannels.Control, MessageType.QualitySettingsUpdate, ProtocolSerializer.Serialize(msg));
            await _transport.SendMessageAsync(DataChannels.Control, env.ToBytes(), reliable: true);
            Logger.Information("Sent quality preset update: {Preset}", preset);
        }
    }

    [RelayCommand]
    private void ToggleChatPane()
    {
        IsChatPaneOpen = !IsChatPaneOpen;
        if (IsChatPaneOpen)
        {
            UnreadChatCount = 0;
        }
    }

    [RelayCommand]
    private async Task SendChatMessageAsync()
    {
        if (string.IsNullOrWhiteSpace(ChatInputText) || _chatService == null || !IsRemoteSessionActive) return;

        var text = ChatInputText.Trim();
        ChatInputText = string.Empty;

        try
        {
            var sent = await _chatService.SendMessageAsync(text);
            ChatMessages.Add(sent);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to send chat message.");
        }
    }

    [RelayCommand]
    private void ToggleAddressBook()
    {
        IsAddressBookOpen = !IsAddressBookOpen;
        if (IsAddressBookOpen)
        {
            _ = LoadAddressBookAsync();
        }
    }

    [RelayCommand]
    private async Task LoadAddressBookAsync()
    {
        try
        {
            var entries = await _addressBookService.GetAllEntriesAsync();
            _dispatcher.Invoke(() =>
            {
                AddressBookEntries.Clear();
                foreach (var entry in entries)
                {
                    AddressBookEntries.Add(entry);
                }
            });
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to load address book.");
        }
    }

    [RelayCommand]
    private async Task SaveCurrentHostToAddressBookAsync()
    {
        if (string.IsNullOrWhiteSpace(TargetHostId)) return;

        var entry = new AddressBookEntry
        {
            DeviceId = TargetHostId,
            Alias = $"Host {TargetHostId}",
            LastConnectedUtc = DateTime.UtcNow
        };

        await _addressBookService.UpsertEntryAsync(entry);
        await LoadAddressBookAsync();
    }

    [RelayCommand]
    private async Task ToggleFavoriteAddressBookAsync(string deviceId)
    {
        await _addressBookService.ToggleFavoriteAsync(deviceId);
        await LoadAddressBookAsync();
    }

    [RelayCommand]
    private async Task DeleteAddressBookEntryAsync(string deviceId)
    {
        await _addressBookService.DeleteEntryAsync(deviceId);
        await LoadAddressBookAsync();
    }

    [RelayCommand]
    private void SelectAddressBookEntry(AddressBookEntry entry)
    {
        TargetHostId = entry.DeviceId;
        IsAddressBookOpen = false;
    }

    // --- Terminal Commands ---
    [RelayCommand]
    private void ToggleTerminalPane()
    {
        IsTerminalOpen = !IsTerminalOpen;
    }

    [RelayCommand]
    private async Task StartTerminalSessionAsync()
    {
        if (_terminalClientService == null || !IsRemoteSessionActive) return;

        try
        {
            _terminalBuffer.Clear();
            _terminalBuffer.Append($"[Launching {SelectedShellType} via ConPTY...]\r\n");
            TerminalOutputText = _terminalBuffer.GetFullText();
            IsTerminalSessionActive = true;
            await _terminalClientService.StartTerminalAsync(SelectedShellType, columns: 120, rows: 30);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to start terminal session.");
            _terminalBuffer.Append($"[Failed to start terminal: {ex.Message}]\r\n");
            TerminalOutputText = _terminalBuffer.GetFullText();
            IsTerminalSessionActive = false;
        }
    }

    [RelayCommand]
    private async Task SendTerminalInputAsync()
    {
        if (_terminalClientService == null || !IsTerminalSessionActive) return;
        var text = TerminalInputText;
        TerminalInputText = string.Empty;
        try
        {
            await _terminalClientService.SendInputAsync(text + "\r\n");
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to send terminal input.");
        }
    }

    [RelayCommand]
    private async Task CloseTerminalSessionAsync()
    {
        if (_terminalClientService == null || !IsTerminalSessionActive) return;
        try
        {
            await _terminalClientService.CloseTerminalAsync("Closed by user");
            IsTerminalSessionActive = false;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to close terminal session.");
        }
    }

    [RelayCommand]
    private void ClearTerminalOutput()
    {
        _terminalBuffer.Clear();
        TerminalOutputText = string.Empty;
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
        _identity.Dispose();
    }
}
