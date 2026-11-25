using System.Diagnostics;
using MessagePack;
using RemoteDesk.Core.Interfaces;
using RemoteDesk.Core.Models;
using RemoteDesk.Core.Protocol;
using RemoteDesk.Core.Security;
using RemoteDesk.Core.Quality;
using RemoteDesk.Core.Services;
using RemoteDesk.Platform.Windows;
using RemoteDesk.Platform.Windows.Audio;
using RemoteDesk.Platform.Windows.Clipboard;
using RemoteDesk.Platform.Windows.Fleet;
using RemoteDesk.Platform.Windows.Ipc;
using RemoteDesk.Platform.Windows.Security;
using RemoteDesk.Platform.Windows.Session;
using RemoteDesk.Platform.Windows.Terminal;
using RemoteDesk.Transport.WebRtc;
using Serilog;
using SIPSorcery.Net;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [Host.Agent] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

var pipeName = "RemoteDesk_Service_Agent_Ipc";
var isServiceManaged = false;
var isSecureDesktop = false;
var serverUrl = "http://localhost:5000/signaling";
var fleetUrl = "http://localhost:5001";
var hostId = string.Empty;
var forceRelay = false;

// Parse CLI Arguments
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--pipe" && i + 1 < args.Length)
    {
        pipeName = args[++i];
        isServiceManaged = true;
    }
    else if (args[i] == "--secure-desktop")
    {
        isSecureDesktop = true;
    }
    else if (args[i] == "--server" && i + 1 < args.Length)
    {
        var endpoint = ServerEndpointParser.Parse(args[++i]);
        serverUrl = endpoint.SignalingUrl;
        if (fleetUrl == "http://localhost:5001")
        {
            fleetUrl = endpoint.FleetApiUrl;
        }
    }
    else if (args[i] == "--fleet-url" && i + 1 < args.Length)
    {
        fleetUrl = args[++i];
    }
    else if (args[i] == "--id" && i + 1 < args.Length)
    {
        hostId = args[++i];
    }
    else if (args[i] == "--force-relay")
    {
        forceRelay = true;
    }
}

Log.Information("==================================================");
Log.Information("   RemoteDesk Host Agent - Interactive Engine");
Log.Information("==================================================");
Log.Information("  PID:            {Pid}", Environment.ProcessId);
Log.Information("  Session ID:     {SessionId}", Process.GetCurrentProcess().SessionId);
Log.Information("  Service Managed:{Managed}", isServiceManaged);
Log.Information("  Secure Desktop: {Secure}", isSecureDesktop);
Log.Information("  Pipe Name:      {Pipe}", pipeName);
Log.Information("==================================================");

FFmpegBootstrapper.Initialize();

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (s, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// Components
WebRtcTransport? currentTransport = null;
IScreenCapturer? capturer = null;
IVideoEncoder? encoder = null;
IInputInjector? injector = null;
EmergencyKillSwitch? killSwitch = null;
SessionBannerService? sessionBanner = null;
WindowsClipboardService? clipboardService = null;
FileTransferService? fileTransferService = null;
WasapiAudioCapturer? audioCapturer = null;
AdaptiveQualityController? qualityController = null;
ChatService? chatService = null;
ITerminalSession? activeTerminalSession = null;

var sessionLock = new object();
string? activeSessionId = null;
SessionPermissions activePermissions = SessionPermissions.All;

void StopActiveSession()
{
    lock (sessionLock)
    {
        if (currentTransport != null)
        {
            Log.Information("Stopping active agent session [{SessionId}]...", activeSessionId);

            sessionBanner?.CloseBanner();
            sessionBanner?.Dispose();
            sessionBanner = null;

            clipboardService?.StopMonitoring();
            clipboardService?.Dispose();
            clipboardService = null;

            fileTransferService?.Dispose();
            fileTransferService = null;

            audioCapturer?.Stop();
            audioCapturer?.Dispose();
            audioCapturer = null;

            chatService?.Dispose();
            chatService = null;

            if (activeTerminalSession != null)
            {
                try { activeTerminalSession.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
                activeTerminalSession = null;
            }

            capturer?.Stop();
            capturer?.Dispose();
            capturer = null;

            encoder?.Dispose();
            encoder = null;

            killSwitch?.Stop();
            killSwitch?.Dispose();
            killSwitch = null;

            injector = null;

            currentTransport.DisposeAsync().AsTask().GetAwaiter().GetResult();
            currentTransport = null;

            activeSessionId = null;
            Log.Information("Session stopped.");
        }
    }
}

// Start Desktop Detector to monitor for UAC / Lock screen transitions
using var desktopDetector = new DesktopDetector();
desktopDetector.DesktopChanged += (desktopName, secure) =>
{
    Log.Information("Input desktop switched to [{Desktop}] (Secure: {IsSecure})", desktopName, secure);
};
desktopDetector.Start();

if (isServiceManaged)
{
    Log.Information("Connecting to Host.Service via Named Pipe [{PipeName}]...", pipeName);
    await using var ipcClient = new NamedPipeIpcClient(pipeName);

    desktopDetector.DesktopChanged += async (desktopName, secure) =>
    {
        if (ipcClient.IsConnected)
        {
            var evt = new IpcDesktopSwitchedEvent
            {
                DesktopName = desktopName,
                IsSecureDesktop = secure,
                SessionId = (uint)Process.GetCurrentProcess().SessionId
            };

            await ipcClient.SendMessageAsync(new IpcMessage
            {
                Type = IpcMessageType.DesktopSwitched,
                Payload = MessagePackSerializer.Serialize(evt)
            });
        }
    };

    ipcClient.MessageReceived += async msg =>
    {
        switch (msg.Type)
        {
            case IpcMessageType.Ping:
                await ipcClient.SendMessageAsync(new IpcMessage { Type = IpcMessageType.Pong, CorrelationId = msg.CorrelationId });
                break;

            case IpcMessageType.QueryStatus:
                var status = new IpcStatusResponse
                {
                    IsSessionActive = currentTransport != null,
                    ActiveSessionId = activeSessionId,
                    CurrentDesktop = desktopDetector.CurrentDesktop,
                    ProcessId = (uint)Environment.ProcessId,
                    SessionId = (uint)Process.GetCurrentProcess().SessionId
                };
                await ipcClient.SendMessageAsync(new IpcMessage
                {
                    Type = IpcMessageType.StatusResponse,
                    CorrelationId = msg.CorrelationId,
                    Payload = MessagePackSerializer.Serialize(status)
                });
                break;

            case IpcMessageType.StopSession:
                StopActiveSession();
                break;
        }
    };

    var connected = await ipcClient.ConnectAsync(timeoutMs: 10000, cts.Token);
    if (!connected)
    {
        Log.Warning("Could not connect to Host.Service NamedPipe within timeout.");
    }
    else
    {
        // Announce readiness to Service
        await ipcClient.SendMessageAsync(new IpcMessage
        {
            Type = IpcMessageType.AgentReady,
            Payload = MessagePackSerializer.Serialize(new IpcStatusResponse
            {
                IsSessionActive = false,
                CurrentDesktop = desktopDetector.CurrentDesktop,
                ProcessId = (uint)Environment.ProcessId,
                SessionId = (uint)Process.GetCurrentProcess().SessionId
            })
        });
    }

    // Keep alive until canceled
    try
    {
        await Task.Delay(Timeout.Infinite, cts.Token);
    }
    catch (OperationCanceledException)
    {
        Log.Information("Host.Agent shutting down...");
    }
}
else
{
    // Interactive Standalone Mode
    Log.Information("Running in interactive standalone mode...");
    var keyStore = new DpapiDeviceKeyStore();
    using var identity = new DeviceIdentity(keyStore);
    if (string.IsNullOrEmpty(hostId)) hostId = identity.DeviceId;

    await using var signaling = new SignalingClient(serverUrl);

    signaling.ConnectionRequested += async req =>
    {
        lock (sessionLock)
        {
            if (currentTransport != null)
            {
                _ = signaling.RejectConnectionAsync(req.ViewerConnectionId, "Host is busy.");
                return;
            }
            activeSessionId = req.SessionId;
            activePermissions = req.ViewerDeviceId.Length > 0 ? SessionPermissions.All : SessionPermissions.All;
        }

        await signaling.AcceptConnectionAsync(req.ViewerConnectionId, req.SessionId, activePermissions, identity.DeviceId);

        var iceServers = await signaling.GetIceServersAsync(hostId);
        currentTransport = new WebRtcTransport(isInitiator: false, iceServers: iceServers, forceRelay: forceRelay);

        currentTransport.IceCandidateGenerated += (candidate, mid, line) =>
        {
            _ = signaling.SendIceCandidateAsync(req.ViewerConnectionId, candidate, mid, line);
        };

        currentTransport.StateChanged += state =>
        {
            Log.Information("Transport state: {State}", state);
            if (state == TransportState.Connected)
            {
                sessionBanner = new SessionBannerService();
                sessionBanner.ShowBanner(req.ViewerName, req.ViewerDeviceId, activePermissions, StopActiveSession);

                injector = new WindowsInputInjector();
                killSwitch = new EmergencyKillSwitch(injector);
                killSwitch.Start();

                capturer = new GdiScreenCapturer();
                var display = capturer.CurrentDisplay;
                if (display != null) injector.SetTargetDisplay(display);

                encoder = new FFmpegH264Encoder();
                capturer.FrameCaptured += frame =>
                {
                    try
                    {
                        var enc = encoder?.Encode(frame);
                        if (enc != null && currentTransport != null)
                        {
                            _ = currentTransport.SendVideoPacketAsync(enc.Data);
                        }
                    }
                    finally { frame.Dispose(); }
                };
                capturer.Start(30);

                // --- Phase 7 Features ---
                // 1. Clipboard Synchronization
                if (activePermissions.HasFlag(SessionPermissions.Clipboard))
                {
                    clipboardService = new WindowsClipboardService();
                    long clipSeq = 0;
                    clipboardService.RemoteClipboardChanged += clipData =>
                    {
                        if (currentTransport == null) return;
                        if (clipData.Format == ClipboardFormat.Text && clipData.Text != null)
                        {
                            var msg = new ClipboardTextMessage
                            {
                                Text = clipData.Text,
                                ContentHash = WindowsClipboardService.ComputeHash(clipData),
                                SequenceNumber = Interlocked.Increment(ref clipSeq)
                            };
                            var env = new ProtocolEnvelope(DataChannels.Clipboard, MessageType.ClipboardText, ProtocolSerializer.Serialize(msg));
                            _ = currentTransport.SendMessageAsync(DataChannels.Clipboard, env.ToBytes(), reliable: true);
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
                            _ = currentTransport.SendMessageAsync(DataChannels.Clipboard, env.ToBytes(), reliable: true);
                        }
                    };
                    clipboardService.StartMonitoring();
                }

                // 2. Chunked File Transfer Service
                if (activePermissions.HasFlag(SessionPermissions.Files))
                {
                    fileTransferService = new FileTransferService(currentTransport);
                }

                // 3. Audio Streaming via WASAPI Loopback
                if (activePermissions.HasFlag(SessionPermissions.Audio))
                {
                    try
                    {
                        audioCapturer = new WasapiAudioCapturer();
                        long audioSeq = 0;
                        audioCapturer.AudioSampleCaptured += sample =>
                        {
                            if (currentTransport == null) return;
                            var audioMsg = new AudioPacketMessage
                            {
                                SequenceNumber = Interlocked.Increment(ref audioSeq),
                                TimestampMs = Stopwatch.GetTimestamp(),
                                SampleRate = audioCapturer.WaveFormat?.SampleRate ?? 48000,
                                Channels = audioCapturer.WaveFormat?.Channels ?? 2,
                                AudioData = sample.ToArray()
                            };
                            var env = new ProtocolEnvelope(DataChannels.Audio, MessageType.AudioPacket, ProtocolSerializer.Serialize(audioMsg));
                            _ = currentTransport.SendMessageAsync(DataChannels.Audio, env.ToBytes(), reliable: false);
                        };
                        audioCapturer.Start();
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Failed to start audio capturer on host.");
                    }
                }

                // 4. In-Session Chat
                chatService = new ChatService(currentTransport, hostId, Environment.MachineName);

                // 5. Multi-Monitor Announcement
                var displays = capturer.GetDisplays();
                var displayListMsg = new DisplayListMessage
                {
                    Displays = displays.ToList(),
                    ActiveDisplayId = capturer.CurrentDisplay?.Id ?? -1
                };
                var dispEnv = new ProtocolEnvelope(DataChannels.Control, MessageType.DisplayList, ProtocolSerializer.Serialize(displayListMsg));
                _ = currentTransport.SendMessageAsync(DataChannels.Control, dispEnv.ToBytes(), reliable: true);

                // 6. Adaptive Quality & Congestion Control
                qualityController = new AdaptiveQualityController(QualityPreset.Balanced);
                qualityController.QualitySettingsChanged += update =>
                {
                    encoder?.SetBitrate(update.TargetBitrateKbps);
                };
            }
            else if (state is TransportState.Disconnected or TransportState.Failed or TransportState.Closed)
            {
                StopActiveSession();
            }
        };

        currentTransport.MessageReceived += async (channel, data) =>
        {
            if (!ProtocolEnvelope.TryDecode(data.Span, out var env, out _)) return;

            if (channel == DataChannels.Input && injector != null)
            {
                if (env.MessageType == MessageType.MouseMove)
                {
                    var m = ProtocolSerializer.Deserialize<MouseMoveMessage>(env.Payload);
                    injector.InjectMouseMove(m.X, m.Y, m.ScreenWidth, m.ScreenHeight);
                }
                else if (env.MessageType == MessageType.MouseButton)
                {
                    var b = ProtocolSerializer.Deserialize<MouseButtonMessage>(env.Payload);
                    injector.InjectMouseButton(b.Button, b.State);
                }
                else if (env.MessageType == MessageType.KeyState)
                {
                    var k = ProtocolSerializer.Deserialize<KeyMessage>(env.Payload);
                    injector.InjectKey(k.ScanCode, k.IsExtended, k.State);
                }
            }
            else if (channel == DataChannels.Clipboard && clipboardService != null)
            {
                if (env.MessageType == MessageType.ClipboardText)
                {
                    var txt = ProtocolSerializer.Deserialize<ClipboardTextMessage>(env.Payload);
                    _ = clipboardService.SetClipboardAsync(new ClipboardData(ClipboardFormat.Text, System.Text.Encoding.UTF8.GetBytes(txt.Text), txt.Text));
                }
                else if (env.MessageType == MessageType.ClipboardImage)
                {
                    var img = ProtocolSerializer.Deserialize<ClipboardImageMessage>(env.Payload);
                    _ = clipboardService.SetClipboardAsync(new ClipboardData(ClipboardFormat.ImagePng, img.ImageData));
                }
            }
            else if (channel == DataChannels.Control)
            {
                if (env.MessageType == MessageType.SwitchDisplay)
                {
                    var switchMsg = ProtocolSerializer.Deserialize<SwitchDisplayMessage>(env.Payload);
                    Log.Information("Viewer requested switch to Display [{DisplayId}]", switchMsg.TargetDisplayId);
                    capturer?.SetTargetDisplay(switchMsg.TargetDisplayId);
                    if (capturer?.CurrentDisplay != null && injector != null)
                    {
                        injector.SetTargetDisplay(capturer.CurrentDisplay);
                    }
                }
                else if (env.MessageType == MessageType.QualityMetricsReport && qualityController != null)
                {
                    var metrics = ProtocolSerializer.Deserialize<QualityMetricsReportMessage>(env.Payload);
                    qualityController.ProcessMetrics(metrics);
                }
                else if (env.MessageType == MessageType.QualitySettingsUpdate && qualityController != null)
                {
                    var settings = ProtocolSerializer.Deserialize<QualitySettingsUpdateMessage>(env.Payload);
                    qualityController.ApplyPreset(settings.Preset);
                }
            }
            else if (channel == DataChannels.Terminal)
            {
                if (!activePermissions.HasFlag(SessionPermissions.Terminal))
                {
                    Log.Warning("Terminal message rejected: Session does not have Terminal permission.");
                    return;
                }

                if (env.MessageType == MessageType.TerminalStart)
                {
                    var startMsg = ProtocolSerializer.Deserialize<TerminalStartMessage>(env.Payload);
                    Log.Information("Starting remote terminal session [{SessionId}] (Shell: {Shell})", startMsg.SessionId, startMsg.Shell);

                    if (activeTerminalSession != null)
                    {
                        await activeTerminalSession.DisposeAsync();
                        activeTerminalSession = null;
                    }

                    var term = WindowsTerminalSession.Create(startMsg.SessionId, startMsg.Shell, startMsg.Columns, startMsg.Rows);
                    activeTerminalSession = term;

                    term.OutputReceived += text =>
                    {
                        if (currentTransport == null) return;
                        var outMsg = new TerminalOutputMessage
                        {
                            SessionId = startMsg.SessionId,
                            OutputData = text
                        };
                        var outEnv = new ProtocolEnvelope(DataChannels.Terminal, MessageType.TerminalOutput, ProtocolSerializer.Serialize(outMsg));
                        _ = currentTransport.SendMessageAsync(DataChannels.Terminal, outEnv.ToBytes(), reliable: true);
                    };

                    term.Exited += code =>
                    {
                        Log.Information("Terminal session [{SessionId}] exited with code {Code}", startMsg.SessionId, code);
                        if (currentTransport == null) return;
                        var closeMsg = new TerminalCloseMessage
                        {
                            SessionId = startMsg.SessionId,
                            Reason = $"Process exited with code {code}"
                        };
                        var closeEnv = new ProtocolEnvelope(DataChannels.Terminal, MessageType.TerminalClose, ProtocolSerializer.Serialize(closeMsg));
                        _ = currentTransport.SendMessageAsync(DataChannels.Terminal, closeEnv.ToBytes(), reliable: true);
                    };
                }
                else if (env.MessageType == MessageType.TerminalInput && activeTerminalSession != null)
                {
                    var inMsg = ProtocolSerializer.Deserialize<TerminalInputMessage>(env.Payload);
                    await activeTerminalSession.WriteInputAsync(inMsg.InputData);
                }
                else if (env.MessageType == MessageType.TerminalResize && activeTerminalSession != null)
                {
                    var resMsg = ProtocolSerializer.Deserialize<TerminalResizeMessage>(env.Payload);
                    activeTerminalSession.Resize(resMsg.Columns, resMsg.Rows);
                }
                else if (env.MessageType == MessageType.TerminalClose && activeTerminalSession != null)
                {
                    Log.Information("Closing terminal session [{SessionId}] on viewer request", activeTerminalSession.SessionId);
                    await activeTerminalSession.DisposeAsync();
                    activeTerminalSession = null;
                }
            }
        };
    };

    signaling.OfferReceived += async offer =>
    {
        if (currentTransport != null)
        {
            await currentTransport.SetRemoteDescriptionAsync(RTCSdpType.offer, offer.Sdp);
            var answerSdp = await currentTransport.CreateAnswerAsync();
            var (fp, sig, pubKey, ts) = DtlsFingerprintProtector.SignSdp(identity, offer.SessionId, offer.FromConnectionId, answerSdp);
            await signaling.SendAnswerAsync(offer.FromConnectionId, offer.SessionId, answerSdp, identity.DeviceId, pubKey, fp, sig, ts);
        }
    };

    signaling.IceCandidateReceived += ice =>
    {
        currentTransport?.AddIceCandidate(ice.Candidate, ice.SdpMid, (ushort)ice.SdpMLineIndex);
    };

    await signaling.ConnectAsync(cts.Token);
    await signaling.RegisterHostAsync(hostId, Environment.MachineName, identity.PublicKeyBytes, cts.Token);
    Log.Information("Host.Agent registered with ID [{HostId}]. Waiting for connections...", hostId);

    // Register with Fleet API and start Heartbeat/Command polling loop
    using var fleetClient = new FleetClientService(fleetUrl);
    var telemetry = WindowsSystemTelemetry.CollectDeviceRecord(hostId, identity.PublicKeyBytes);
    await fleetClient.RegisterDeviceAsync(telemetry, cts.Token);
    Log.Information("Host.Agent registered with Fleet API at [{FleetUrl}]", fleetUrl);

    var fleetTask = Task.Run(async () =>
    {
        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                var status = activeSessionId != null ? DeviceStatus.InSession : DeviceStatus.Online;
                await fleetClient.SendHeartbeatAsync(hostId, status, activeSessionId, cts.Token);

                var pending = await fleetClient.GetPendingCommandsAsync(hostId, cts.Token);
                foreach (var cmd in pending)
                {
                    Log.Information("Received Fleet Command [{CmdId}] type {Type}", cmd.CommandId, cmd.CommandType);
                    var (resStatus, output) = await WindowsFleetCommandHandler.ExecuteCommandAsync(cmd);
                    await fleetClient.ReportCommandResultAsync(cmd.CommandId, resStatus, output, cts.Token);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Debug(ex, "Fleet poll loop error");
            }

            try
            {
                await Task.Delay(10000, cts.Token);
            }
            catch (OperationCanceledException) { break; }
        }

        try
        {
            await fleetClient.SendHeartbeatAsync(hostId, DeviceStatus.Offline);
        }
        catch { }
    });

    try
    {
        await Task.Delay(Timeout.Infinite, cts.Token);
    }
    catch (OperationCanceledException)
    {
        Log.Information("Shutting down Host.Agent...");
    }
    finally
    {
        StopActiveSession();
        await signaling.DisposeAsync();
        try { await fleetTask; } catch { }
    }
}

await Log.CloseAndFlushAsync();
