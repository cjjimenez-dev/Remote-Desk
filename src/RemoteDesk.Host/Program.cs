using System.Diagnostics;
using RemoteDesk.Core.Interfaces;
using RemoteDesk.Core.Models;
using RemoteDesk.Core.Protocol;
using RemoteDesk.Platform.Windows;
using RemoteDesk.Transport.WebRtc;
using Serilog;
using SIPSorcery.Net;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

var serverUrl = "http://localhost:5000/signaling";
var hostId = $"{Random.Shared.Next(100, 999)}-{Random.Shared.Next(100, 999)}";
var deviceName = Environment.MachineName;

// Parse optional arguments: --server <url>, --id <hostId>, --name <name>
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--server" && i + 1 < args.Length)
    {
        serverUrl = args[++i];
    }
    else if (args[i] == "--id" && i + 1 < args.Length)
    {
        hostId = args[++i];
    }
    else if (args[i] == "--name" && i + 1 < args.Length)
    {
        deviceName = args[++i];
    }
}

Log.Information("==================================================");
Log.Information("   RemoteDesk Host Agent - Phase 3 LAN P2P");
Log.Information("==================================================");
Log.Information("  Host ID:     {HostId}", hostId);
Log.Information("  Device Name: {DeviceName}", deviceName);
Log.Information("  Signaling:   {ServerUrl}", serverUrl);
Log.Information("  Kill Hotkey: Ctrl + Alt + Shift + Esc");
Log.Information("==================================================");

FFmpegBootstrapper.Initialize();

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (s, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

await using var signaling = new SignalingClient(serverUrl);

WebRtcTransport? currentTransport = null;
IScreenCapturer? capturer = null;
IVideoEncoder? encoder = null;
IInputInjector? injector = null;
EmergencyKillSwitch? killSwitch = null;

var sessionLock = new object();
string? activeViewerConnId = null;

void CleanupSession()
{
    lock (sessionLock)
    {
        if (currentTransport != null)
        {
            Log.Information("Closing current WebRTC session...");
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
            activeViewerConnId = null;

            Log.Information("Session closed. Waiting for new connections...");
        }
    }
}

signaling.ConnectionRequested += async (sessionId, viewerConnId, viewerName) =>
{
    lock (sessionLock)
    {
        if (activeViewerConnId != null)
        {
            Log.Warning("Rejecting connection from [{ViewerName}] ({ViewerConn}): Busy with another session.",
                viewerName, viewerConnId);
            _ = signaling.RejectConnectionAsync(viewerConnId, "Host is busy with an existing session.");
            return;
        }

        activeViewerConnId = viewerConnId;
    }

    Log.Information(">>> Incoming connection from [{ViewerName}] ({ViewerConn}), Session: [{SessionId}]",
        viewerName, viewerConnId, sessionId);

    try
    {
        // 1. Accept connection
        await signaling.AcceptConnectionAsync(viewerConnId, sessionId);

        // 2. Create WebRtcTransport
        currentTransport = new WebRtcTransport(isInitiator: false);

        // Wire ICE Candidate generation from Host -> Viewer
        currentTransport.IceCandidateGenerated += (candidate, mid, line) =>
        {
            _ = signaling.SendIceCandidateAsync(viewerConnId, candidate, mid, line);
        };

        // Wire State changes
        currentTransport.StateChanged += state =>
        {
            Log.Information("WebRTC State: {State}", state);

            if (state == TransportState.Connected)
            {
                Log.Information(">>> WebRTC session established! Starting screen capture and input injection...");

                // Initialize input injector
                injector = new WindowsInputInjector();
                killSwitch = new EmergencyKillSwitch(injector);
                killSwitch.EmergencyKillTriggered += () =>
                {
                    Log.Warning("EMERGENCY KILL TRIGGERED on Host! Remote input injection has been revoked.");
                };
                killSwitch.Start();

                // Initialize capturer and encoder
                capturer = new GdiScreenCapturer();
                var display = capturer.CurrentDisplay;
                if (display != null)
                {
                    injector.SetTargetDisplay(display);
                }

                encoder = new FFmpegH264Encoder();

                var frameIndex = 0;
                capturer.FrameCaptured += frame =>
                {
                    try
                    {
                        var enc = encoder?.Encode(frame);
                        if (enc != null && currentTransport != null)
                        {
                            var idx = Interlocked.Increment(ref frameIndex);
                            if (idx <= 3 || idx % 60 == 0)
                            {
                                Log.Debug("[Host Frame #{Index}] H.264 {Size:F1} KB | Key: {Key}",
                                    idx, enc.Data.Length / 1024.0, enc.IsKeyFrame);
                            }

                            _ = currentTransport.SendVideoPacketAsync(enc.Data);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Error encoding or sending frame.");
                    }
                    finally
                    {
                        frame.Dispose();
                    }
                };

                capturer.Start(targetFps: 30);
            }
            else if (state is TransportState.Disconnected or TransportState.Failed or TransportState.Closed)
            {
                CleanupSession();
            }
        };

        // Wire Incoming Input & Control Messages
        currentTransport.MessageReceived += (channelId, data) =>
        {
            if (channelId == DataChannels.Input && injector != null)
            {
                if (ProtocolEnvelope.TryDecode(data.Span, out var env, out _))
                {
                    switch (env.MessageType)
                    {
                        case MessageType.MouseMove:
                            var move = ProtocolSerializer.Deserialize<MouseMoveMessage>(env.Payload);
                            injector.InjectMouseMove(move.X, move.Y, move.ScreenWidth, move.ScreenHeight);
                            break;

                        case MessageType.MouseButton:
                            var btn = ProtocolSerializer.Deserialize<MouseButtonMessage>(env.Payload);
                            injector.InjectMouseButton(btn.Button, btn.State);
                            break;

                        case MessageType.MouseWheel:
                            var wheel = ProtocolSerializer.Deserialize<MouseWheelMessage>(env.Payload);
                            injector.InjectMouseWheel(wheel.DeltaX, wheel.DeltaY);
                            break;

                        case MessageType.KeyState:
                            var key = ProtocolSerializer.Deserialize<KeyMessage>(env.Payload);
                            injector.InjectKey(key.ScanCode, key.IsExtended, key.State);
                            break;

                        case MessageType.TextUnicode:
                            var text = ProtocolSerializer.Deserialize<UnicodeTextMessage>(env.Payload);
                            injector.InjectUnicodeText(text.Text);
                            break;
                    }
                }
            }
        };
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Failed to initialize WebRTC session.");
        CleanupSession();
    }
};

signaling.OfferReceived += async (fromConnId, sdp) =>
{
    if (fromConnId == activeViewerConnId && currentTransport != null)
    {
        Log.Information("Received SDP offer from Viewer. Generating answer...");
        await currentTransport.SetRemoteDescriptionAsync(RTCSdpType.offer, sdp);
        var answerSdp = await currentTransport.CreateAnswerAsync();
        await signaling.SendAnswerAsync(fromConnId, answerSdp);
        Log.Information("Sent SDP answer to Viewer.");
    }
};

signaling.IceCandidateReceived += (fromConnId, candidate, sdpMid, sdpMLineIndex) =>
{
    if (fromConnId == activeViewerConnId && currentTransport != null)
    {
        Log.Debug("Adding remote ICE candidate from Viewer...");
        currentTransport.AddIceCandidate(candidate, sdpMid, sdpMLineIndex);
    }
};

try
{
    Log.Information("Connecting to signaling server at {Url}...", serverUrl);
    await signaling.ConnectAsync(cts.Token);
    var registered = await signaling.RegisterHostAsync(hostId, deviceName, cts.Token);

    if (registered)
    {
        Log.Information(">>> Host successfully registered! Ready for incoming connections.");
        Log.Information("    To connect from a Viewer, enter Host ID: [{HostId}]", hostId);
    }
    else
    {
        Log.Error("Failed to register host with signaling server.");
        return;
    }

    // Wait until canceled
    await Task.Delay(Timeout.Infinite, cts.Token);
}
catch (OperationCanceledException)
{
    Log.Information("Host agent shutting down...");
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host agent encountered a fatal error.");
}
finally
{
    CleanupSession();
    await signaling.DisposeAsync();
    await Log.CloseAndFlushAsync();
}
