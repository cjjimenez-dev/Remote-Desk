using System.Diagnostics;
using System.Security.Cryptography;
using MessagePack;
using Microsoft.Extensions.Hosting;
using RemoteDesk.Core.Models;
using RemoteDesk.Core.Protocol;
using RemoteDesk.Core.Security;
using RemoteDesk.Platform.Windows.Ipc;
using RemoteDesk.Platform.Windows.Security;
using RemoteDesk.Platform.Windows.Session;
using RemoteDesk.Transport.WebRtc;
using Serilog;

namespace RemoteDesk.Host.Service;

public sealed class HostBackgroundService : BackgroundService
{
    private static readonly ILogger Logger = Log.ForContext<HostBackgroundService>();

    private readonly string _serverUrl;
    private readonly bool _forceRelay;
    private readonly string _pipeName = "RemoteDesk_Service_Agent_Ipc";

    private NamedPipeIpcServer? _ipcServer;
    private SignalingClient? _signalingClient;
    private Process? _currentAgentProcess;
    private DeviceIdentity? _identity;
    private readonly RateLimiter _rateLimiter = new() { MaxAttemptsBeforeLockout = 5, LockoutDuration = TimeSpan.FromMinutes(15) };
    private readonly object _lock = new();

    private uint _activeSessionId = 0xFFFFFFFF;
    private bool _isAgentReady;

    public HostBackgroundService(string serverUrl = "http://localhost:5000/signaling", bool forceRelay = false)
    {
        _serverUrl = serverUrl;
        _forceRelay = forceRelay;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.Information("==================================================");
        Logger.Information("   RemoteDesk Windows Service (Session 0 SYSTEM)");
        Logger.Information("==================================================");

        // 1. Initialize Machine-Scope DPAPI Device Identity
        var keyStore = new DpapiDeviceKeyStore(scope: DataProtectionScope.LocalMachine);
        _identity = new DeviceIdentity(keyStore);

        Logger.Information("  Service Device ID: {DeviceId}", _identity.DeviceId);
        Logger.Information("  Signaling Server:  {ServerUrl}", _serverUrl);
        Logger.Information("  Named Pipe:        {PipeName}", _pipeName);
        Logger.Information("==================================================");

        // 2. Start Named Pipe IPC Server
        _ipcServer = new NamedPipeIpcServer(_pipeName);
        _ipcServer.ClientConnected += () =>
        {
            Logger.Information("Host.Agent successfully connected to Service Named Pipe.");
        };

        _ipcServer.ClientDisconnected += () =>
        {
            Logger.Warning("Host.Agent disconnected from Service Named Pipe.");
            _isAgentReady = false;
        };

        _ipcServer.MessageReceived += OnIpcMessageReceived;
        _ipcServer.Start();

        // 3. Connect to Signaling Server
        _signalingClient = new SignalingClient(_serverUrl);
        _signalingClient.ConnectionRequested += OnSignalingConnectionRequested;

        try
        {
            await _signalingClient.ConnectAsync(stoppingToken);
            var registered = await _signalingClient.RegisterHostAsync(_identity.DeviceId, Environment.MachineName, _identity.PublicKeyBytes, stoppingToken);

            if (registered)
            {
                Logger.Information(">>> RemoteDesk Service successfully registered with ID [{DeviceId}]", _identity.DeviceId);
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Could not immediately connect to signaling server. Auto-reconnect will retry in background.");
        }

        // 4. Launch Agent into active console session
        EnsureAgentRunning();

        // 5. Supervision Loop
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                CheckAndSuperviseAgent();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Error in Host.Service supervision loop.");
            }
        }

        Logger.Information("RemoteDesk Service shutting down...");
        Cleanup();
    }

    private void EnsureAgentRunning()
    {
        lock (_lock)
        {
            var consoleSessionId = SessionProcessLauncher.GetActiveConsoleSession();
            if (consoleSessionId == 0xFFFFFFFF)
            {
                Logger.Information("No active console session currently available.");
                return;
            }

            if (_currentAgentProcess != null && !_currentAgentProcess.HasExited && _activeSessionId == consoleSessionId)
            {
                return; // Already running in active session
            }

            // Terminate existing agent if session changed
            if (_currentAgentProcess != null && !_currentAgentProcess.HasExited)
            {
                try
                {
                    Logger.Information("Terminating old agent process (PID: {Pid}) due to session transition...", _currentAgentProcess.Id);
                    _currentAgentProcess.Kill();
                }
                catch { }
                _currentAgentProcess = null;
            }

            var agentExePath = FindAgentExecutable();
            if (string.IsNullOrEmpty(agentExePath) || !File.Exists(agentExePath))
            {
                Logger.Warning("RemoteDesk.Host.Agent.exe not found at [{Path}]. Skipping launch.", agentExePath);
                return;
            }

            Logger.Information("Launching Host.Agent into Console Session {SessionId}...", consoleSessionId);
            var args = $"--pipe {_pipeName}";
            var proc = SessionProcessLauncher.LaunchProcessInSession(agentExePath, args, consoleSessionId);

            if (proc != null)
            {
                _currentAgentProcess = proc;
                _activeSessionId = consoleSessionId;
                proc.EnableRaisingEvents = true;
                proc.Exited += (s, e) =>
                {
                    Logger.Warning("Host.Agent process (PID: {Pid}) has exited.", proc.Id);
                    _isAgentReady = false;
                };
            }
        }
    }

    private void CheckAndSuperviseAgent()
    {
        var consoleSession = SessionProcessLauncher.GetActiveConsoleSession();
        if (consoleSession != 0xFFFFFFFF && (_currentAgentProcess == null || _currentAgentProcess.HasExited || _activeSessionId != consoleSession))
        {
            Logger.Information("Session transition or Agent restart needed (Active Session: {SessionId}).", consoleSession);
            EnsureAgentRunning();
        }
    }

    private void OnIpcMessageReceived(IpcMessage msg)
    {
        switch (msg.Type)
        {
            case IpcMessageType.AgentReady:
                Logger.Information("Host.Agent announced READY state.");
                _isAgentReady = true;
                break;

            case IpcMessageType.SendSas:
                Logger.Information("Host.Agent requested SAS (Ctrl+Alt+Del). Triggering SendSAS...");
                var triggered = SasSimulator.TriggerSas();
                Logger.Information("SAS invocation result: {Success}", triggered);
                break;

            case IpcMessageType.DesktopSwitched:
                if (msg.Payload != null)
                {
                    var evt = MessagePackSerializer.Deserialize<IpcDesktopSwitchedEvent>(msg.Payload);
                    Logger.Information("Agent reported desktop switch: [{Desktop}] (Secure: {IsSecure})", evt.DesktopName, evt.IsSecureDesktop);

                    if (evt.IsSecureDesktop)
                    {
                        // Spawning SYSTEM agent on WinSta0\Winlogon for UAC prompt interaction
                        LaunchSecureDesktopAgent();
                    }
                }
                break;
        }
    }

    private void LaunchSecureDesktopAgent()
    {
        var agentExePath = FindAgentExecutable();
        if (string.IsNullOrEmpty(agentExePath) || !File.Exists(agentExePath)) return;

        var consoleSessionId = SessionProcessLauncher.GetActiveConsoleSession();
        Logger.Information("Launching secure-desktop Agent onto WinSta0\\Winlogon in Session {SessionId}...", consoleSessionId);

        var args = $"--pipe {_pipeName} --secure-desktop";
        SessionProcessLauncher.LaunchProcessInSession(agentExePath, args, consoleSessionId, isSecureDesktop: true);
    }

    private void OnSignalingConnectionRequested(SignalingConnectionRequest req)
    {
        Logger.Information("Incoming connection request: Viewer=[{ViewerName}], Device=[{ViewerDeviceId}], Session=[{SessionId}]",
            req.ViewerName, req.ViewerDeviceId, req.SessionId);

        // Check rate limiter
        if (_rateLimiter.IsLockedOut(req.ViewerDeviceId, out var lockout))
        {
            AuditLogger.LogRateLimitTriggered(req.ViewerDeviceId, lockout);
            Logger.Warning("Rejecting [{ViewerDeviceId}]: Locked out ({Duration:F0} min remaining).", req.ViewerDeviceId, lockout.TotalMinutes);
            _ = _signalingClient?.RejectConnectionAsync(req.ViewerConnectionId, $"Rate limited. Please wait {lockout.TotalMinutes:F0} minutes.");
            return;
        }

        // Verify request signature
        if (req.ViewerPublicKey != null && req.RequestSignature != null)
        {
            var payload = $"{req.SessionId}:{req.ViewerDeviceId}:{req.Timestamp}";
            var valid = DeviceIdentity.VerifySignature(req.ViewerPublicKey, payload, req.RequestSignature);
            if (!valid)
            {
                AuditLogger.LogTamperingDetected(req.SessionId, req.ViewerDeviceId, "Invalid signature on connection request");
                Logger.Error("Invalid request signature from Viewer [{ViewerName}] ({ViewerDeviceId})!", req.ViewerName, req.ViewerDeviceId);
                _ = _signalingClient?.RejectConnectionAsync(req.ViewerConnectionId, "Invalid signature.");
                return;
            }
        }

        AuditLogger.LogSessionRequested(req.SessionId, req.ViewerDeviceId, req.ViewerName, _identity?.DeviceId ?? "");

        // Accept connection & notify Agent via Named Pipe
        _ = _signalingClient?.AcceptConnectionAsync(req.ViewerConnectionId, req.SessionId, SessionPermissions.All, _identity?.DeviceId ?? "");

        if (_ipcServer != null && _isAgentReady)
        {
            var startReq = new IpcStartSessionRequest
            {
                SessionId = req.SessionId,
                ViewerConnectionId = req.ViewerConnectionId,
                ViewerDeviceId = req.ViewerDeviceId,
                ViewerName = req.ViewerName,
                Permissions = SessionPermissions.All,
                HostDeviceId = _identity?.DeviceId ?? "",
                SignalingServerUrl = _serverUrl,
                ForceRelay = _forceRelay
            };

            _ = _ipcServer.SendMessageAsync(new IpcMessage
            {
                Type = IpcMessageType.StartSession,
                Payload = MessagePackSerializer.Serialize(startReq)
            });
        }
    }

    private static string FindAgentExecutable()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "RemoteDesk.Host.Agent.exe"),
            Path.Combine(baseDir, "..", "RemoteDesk.Host.Agent", "bin", "Debug", "net10.0-windows10.0.19041.0", "RemoteDesk.Host.Agent.exe"),
            Path.Combine(baseDir, "..", "RemoteDesk.Host.Agent", "RemoteDesk.Host.Agent.exe")
        };

        foreach (var c in candidates)
        {
            var full = Path.GetFullPath(c);
            if (File.Exists(full)) return full;
        }

        return Path.Combine(baseDir, "RemoteDesk.Host.Agent.exe");
    }

    private void Cleanup()
    {
        lock (_lock)
        {
            if (_currentAgentProcess != null && !_currentAgentProcess.HasExited)
            {
                try { _currentAgentProcess.Kill(); } catch { }
                _currentAgentProcess = null;
            }

            _signalingClient?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _signalingClient = null;

            _ipcServer?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _ipcServer = null;

            _identity?.Dispose();
            _identity = null;
        }
    }
}
