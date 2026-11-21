using System.Diagnostics;
using MessagePack;
using RemoteDesk.Core.Interfaces;
using RemoteDesk.Core.Models;
using RemoteDesk.Core.Protocol;
using RemoteDesk.Core.Security;
using RemoteDesk.Platform.Windows;
using RemoteDesk.Platform.Windows.Ipc;
using RemoteDesk.Platform.Windows.Security;
using RemoteDesk.Platform.Windows.Session;
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
        serverUrl = args[++i];
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
            }
            else if (state is TransportState.Disconnected or TransportState.Failed or TransportState.Closed)
            {
                StopActiveSession();
            }
        };

        currentTransport.MessageReceived += (channel, data) =>
        {
            if (channel == DataChannels.Input && injector != null && ProtocolEnvelope.TryDecode(data.Span, out var env, out _))
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
    }
}

await Log.CloseAndFlushAsync();
