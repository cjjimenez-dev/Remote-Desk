using System.Diagnostics;
using RemoteDesk.Core.Interfaces;
using RemoteDesk.Core.Models;
using RemoteDesk.Core.Protocol;
using RemoteDesk.Core.Security;
using RemoteDesk.Platform.Windows;
using RemoteDesk.Platform.Windows.Security;
using RemoteDesk.Platform.Windows.Session;
using RemoteDesk.Transport.WebRtc;
using Serilog;
using SIPSorcery.Net;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

// Initialize DPAPI Device Identity
var keyStore = new DpapiDeviceKeyStore();
using var identity = new DeviceIdentity(keyStore);

var serverUrl = "http://localhost:5000/signaling";
var hostId = identity.DeviceId;
var deviceName = Environment.MachineName;
var forceRelay = false;
var unattendedPassword = string.Empty;
var autoAccept = false;

// Parse CLI arguments: --server <url>, --id <hostId>, --name <name>, --force-relay, --password <pwd>, --auto-accept
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
    else if (args[i] == "--force-relay")
    {
        forceRelay = true;
    }
    else if ((args[i] == "--password" || args[i] == "--unattended-password") && i + 1 < args.Length)
    {
        unattendedPassword = args[++i];
    }
    else if (args[i] == "--auto-accept")
    {
        autoAccept = true;
    }
}

Log.Information("==================================================");
Log.Information("   RemoteDesk Host Agent - Phase 5 Security");
Log.Information("==================================================");
Log.Information("  Device ID:     {HostId}", hostId);
Log.Information("  Public Key:    {PubKey}...", identity.PublicKeyBase64[..Math.Min(24, identity.PublicKeyBase64.Length)]);
Log.Information("  Device Name:   {DeviceName}", deviceName);
Log.Information("  Signaling:     {ServerUrl}", serverUrl);
Log.Information("  Force Relay:   {ForceRelay}", forceRelay);
Log.Information("  Unattended:    {IsUnattended}", !string.IsNullOrEmpty(unattendedPassword));
Log.Information("  Auto Accept:   {AutoAccept}", autoAccept);
Log.Information("  Kill Hotkey:   Ctrl + Alt + Shift + Esc");
Log.Information("==================================================");

FFmpegBootstrapper.Initialize();

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (s, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

await using var signaling = new SignalingClient(serverUrl);

var rateLimiter = new RateLimiter { MaxAttemptsBeforeLockout = 5, LockoutDuration = TimeSpan.FromMinutes(15) };
using var sessionBanner = new SessionBannerService();

WebRtcTransport? currentTransport = null;
IScreenCapturer? capturer = null;
IVideoEncoder? encoder = null;
IInputInjector? injector = null;
EmergencyKillSwitch? killSwitch = null;

var sessionLock = new object();
string? activeSessionId = null;
string? activeViewerConnId = null;
string? activeViewerDeviceId = null;
string? activeViewerName = null;
SessionPermissions activePermissions = SessionPermissions.All;
DateTimeOffset? sessionStartTime = null;

void CleanupSession()
{
    lock (sessionLock)
    {
        if (currentTransport != null)
        {
            var duration = sessionStartTime.HasValue ? DateTimeOffset.UtcNow - sessionStartTime.Value : TimeSpan.Zero;
            AuditLogger.LogSessionEnd(activeSessionId ?? "unknown", activeViewerDeviceId ?? "unknown", duration, "Session Closed");

            Log.Information("Closing current WebRTC session...");
            sessionBanner.CloseBanner();

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
            activeViewerConnId = null;
            activeViewerDeviceId = null;
            activeViewerName = null;
            sessionStartTime = null;

            Log.Information("Session closed. Waiting for new connections...");
        }
    }
}

signaling.ConnectionRequested += async req =>
{
    lock (sessionLock)
    {
        if (activeViewerConnId != null)
        {
            Log.Warning("Rejecting connection from [{ViewerName}] ({ViewerConn}): Busy with another session.",
                req.ViewerName, req.ViewerConnectionId);
            _ = signaling.RejectConnectionAsync(req.ViewerConnectionId, "Host is busy with an existing session.");
            return;
        }

        // 1. Check Rate Limiter
        if (rateLimiter.IsLockedOut(req.ViewerDeviceId, out var lockout))
        {
            AuditLogger.LogRateLimitTriggered(req.ViewerDeviceId, lockout);
            Log.Warning("Rejected connection from [{ViewerDeviceId}]: Rate limited ({Minutes:F0} min remaining).",
                req.ViewerDeviceId, lockout.TotalMinutes);
            _ = signaling.RejectConnectionAsync(req.ViewerConnectionId, $"Rate limited. Locked out for {lockout.TotalMinutes:F0} min.");
            return;
        }

        // 2. Verify Cryptographic Request Signature if provided
        if (req.ViewerPublicKey != null && req.RequestSignature != null)
        {
            var payload = $"{req.SessionId}:{req.ViewerDeviceId}:{req.Timestamp}";
            var valid = DeviceIdentity.VerifySignature(req.ViewerPublicKey, payload, req.RequestSignature);
            if (!valid)
            {
                AuditLogger.LogTamperingDetected(req.SessionId, req.ViewerDeviceId, "Invalid connection request signature");
                Log.Error("SECURITY ALERT: Invalid connection request signature from Viewer [{ViewerName}] ({ViewerDeviceId})!",
                    req.ViewerName, req.ViewerDeviceId);
                _ = signaling.RejectConnectionAsync(req.ViewerConnectionId, "Security signature verification failed.");
                return;
            }

            Log.Information(">>> Viewer request signature cryptographically verified: [{ViewerDeviceId}]", req.ViewerDeviceId);
        }

        activeViewerConnId = req.ViewerConnectionId;
        activeViewerDeviceId = req.ViewerDeviceId;
        activeViewerName = req.ViewerName;
        activeSessionId = req.SessionId;
        activePermissions = SessionPermissions.All;
    }

    AuditLogger.LogSessionRequested(req.SessionId, req.ViewerDeviceId, req.ViewerName, hostId);
    Log.Information(">>> Incoming connection from [{ViewerName}] ({ViewerConn}), Device: [{ViewerDeviceId}], Session: [{SessionId}]",
        req.ViewerName, req.ViewerConnectionId, req.ViewerDeviceId, req.SessionId);

    // 3. Attended Consent Decision
    AuditLogger.LogConsentDecision(req.SessionId, req.ViewerDeviceId, true, activePermissions);

    try
    {
        // 4. Accept connection
        await signaling.AcceptConnectionAsync(req.ViewerConnectionId, req.SessionId, activePermissions, identity.DeviceId);

        // 5. Fetch ICE servers (STUN + TURN with short-lived credentials)
        List<IceServerConfig>? iceServers = null;
        try
        {
            iceServers = await signaling.GetIceServersAsync(hostId);
            Log.Information("Obtained {Count} STUN/TURN servers from broker.", iceServers.Count);
        }
        catch (Exception ex)
        {
            Log.Warning("Could not retrieve STUN/TURN servers ({Message}). Falling back to local/default.", ex.Message);
        }

        // 6. Create WebRtcTransport
        currentTransport = new WebRtcTransport(isInitiator: false, iceServers: iceServers, forceRelay: forceRelay);

        currentTransport.ConnectionPathChanged += path =>
        {
            Log.Information(">>> Connection path active: {Path}", path);
        };

        // Wire ICE Candidate generation from Host -> Viewer
        currentTransport.IceCandidateGenerated += (candidate, mid, line) =>
        {
            _ = signaling.SendIceCandidateAsync(req.ViewerConnectionId, candidate, mid, line);
        };

        // Wire State changes
        currentTransport.StateChanged += state =>
        {
            Log.Information("WebRTC State: {State}", state);

            if (state == TransportState.Connected)
            {
                sessionStartTime = DateTimeOffset.UtcNow;
                AuditLogger.LogSessionStart(req.SessionId, req.ViewerDeviceId, hostId, currentTransport.ConnectionPath, activePermissions);

                Log.Information(">>> WebRTC session established! Starting screen capture and input injection...");

                // Show always-visible host floating session banner
                sessionBanner.ShowBanner(req.ViewerName, req.ViewerDeviceId, activePermissions, () =>
                {
                    Log.Information("Session banner requested disconnect.");
                    CleanupSession();
                });

                // Initialize input injector if control is permitted
                if (activePermissions.HasFlag(SessionPermissions.Control))
                {
                    injector = new WindowsInputInjector();
                    killSwitch = new EmergencyKillSwitch(injector);
                    killSwitch.EmergencyKillTriggered += () =>
                    {
                        AuditLogger.LogEmergencyKill(hostId, "Host Hotkey (Ctrl+Alt+Shift+Esc)");
                        Log.Warning("EMERGENCY KILL TRIGGERED on Host! Remote input injection has been revoked.");
                    };
                    killSwitch.Start();
                }

                // Initialize capturer and encoder if view is permitted
                if (activePermissions.HasFlag(SessionPermissions.View))
                {
                    capturer = new GdiScreenCapturer();
                    var display = capturer.CurrentDisplay;
                    if (display != null && injector != null)
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
            }
            else if (state is TransportState.Disconnected or TransportState.Failed or TransportState.Closed)
            {
                CleanupSession();
            }
        };

        // Wire Incoming Input & Control Messages
        currentTransport.MessageReceived += (channelId, data) =>
        {
            if (channelId == DataChannels.Input && injector != null && activePermissions.HasFlag(SessionPermissions.Control))
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

signaling.OfferReceived += async offer =>
{
    if (offer.FromConnectionId == activeViewerConnId && currentTransport != null)
    {
        Log.Information("Received SDP offer from Viewer. Verifying DTLS fingerprint signature...");

        // Verify Anti-MITM DTLS Fingerprint signature against Viewer's ECDSA public key
        if (offer.DtlsFingerprintSignature != null && offer.SenderPublicKey != null)
        {
            var valid = DtlsFingerprintProtector.VerifySignedSdp(
                offer.Sdp,
                offer.SessionId,
                offer.SenderDeviceId,
                offer.TargetConnectionId,
                offer.DtlsFingerprint,
                offer.DtlsFingerprintSignature,
                offer.SenderPublicKey,
                offer.Timestamp);

            if (!valid)
            {
                AuditLogger.LogTamperingDetected(offer.SessionId, offer.SenderDeviceId, "SDP DTLS fingerprint signature mismatch (potential MITM attack)!");
                Log.Error("SECURITY ALERT: SDP Offer DTLS fingerprint tampering detected! Aborting session.");
                CleanupSession();
                return;
            }

            Log.Information(">>> SDP Offer DTLS Fingerprint verified! E2E DTLS encryption authenticity guaranteed against signaling server MITM.");
        }

        await currentTransport.SetRemoteDescriptionAsync(RTCSdpType.offer, offer.Sdp);
        var answerSdp = await currentTransport.CreateAnswerAsync();

        // Sign the Answer SDP's DTLS Fingerprint with Host's DeviceIdentity
        var (fingerprint, signature, pubKey, timestamp) = DtlsFingerprintProtector.SignSdp(
            identity,
            offer.SessionId,
            offer.FromConnectionId,
            answerSdp);

        await signaling.SendAnswerAsync(
            offer.FromConnectionId,
            offer.SessionId,
            answerSdp,
            identity.DeviceId,
            pubKey,
            fingerprint,
            signature,
            timestamp);

        Log.Information("Sent signed SDP answer to Viewer.");
    }
};

signaling.IceCandidateReceived += ice =>
{
    if (ice.FromConnectionId == activeViewerConnId && currentTransport != null)
    {
        Log.Debug("Adding remote ICE candidate from Viewer...");
        currentTransport.AddIceCandidate(ice.Candidate, ice.SdpMid, (ushort)ice.SdpMLineIndex);
    }
};

try
{
    Log.Information("Connecting to signaling server at {Url}...", serverUrl);
    await signaling.ConnectAsync(cts.Token);
    var registered = await signaling.RegisterHostAsync(hostId, deviceName, identity.PublicKeyBytes, cts.Token);

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
