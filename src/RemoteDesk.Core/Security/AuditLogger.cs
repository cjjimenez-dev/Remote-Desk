using RemoteDesk.Core.Models;
using Serilog;

namespace RemoteDesk.Core.Security;

public static class AuditLogger
{
    private static readonly ILogger Logger = Log.ForContext("SourceContext", "RemoteDesk.Audit");

    public static void LogSessionRequested(string sessionId, string viewerId, string viewerName, string targetHostId)
    {
        Logger.Information("[AUDIT] Session Requested: SessionId={SessionId}, ViewerId={ViewerId}, ViewerName={ViewerName}, TargetHost={TargetHostId}, Timestamp={Timestamp:u}",
            sessionId, viewerId, viewerName, targetHostId, DateTimeOffset.UtcNow);
    }

    public static void LogConsentDecision(string sessionId, string viewerId, bool granted, SessionPermissions permissions)
    {
        Logger.Information("[AUDIT] Host Consent Decision: SessionId={SessionId}, ViewerId={ViewerId}, Granted={Granted}, Permissions={Permissions}, Timestamp={Timestamp:u}",
            sessionId, viewerId, granted, permissions, DateTimeOffset.UtcNow);
    }

    public static void LogSessionStart(string sessionId, string viewerId, string hostId, ConnectionPath path, SessionPermissions permissions)
    {
        Logger.Information("[AUDIT] Session Started: SessionId={SessionId}, ViewerId={ViewerId}, HostId={HostId}, Path={Path}, Permissions={Permissions}, Timestamp={Timestamp:u}",
            sessionId, viewerId, hostId, path, permissions, DateTimeOffset.UtcNow);
    }

    public static void LogSessionEnd(string sessionId, string viewerId, TimeSpan duration, string reason)
    {
        Logger.Information("[AUDIT] Session Ended: SessionId={SessionId}, ViewerId={ViewerId}, Duration={Duration:g}, Reason={Reason}, Timestamp={Timestamp:u}",
            sessionId, viewerId, duration, reason, DateTimeOffset.UtcNow);
    }

    public static void LogAuthAttempt(string viewerId, bool success, string reason)
    {
        if (success)
        {
            Logger.Information("[AUDIT] Auth Succeeded: ViewerId={ViewerId}, Reason={Reason}, Timestamp={Timestamp:u}",
                viewerId, reason, DateTimeOffset.UtcNow);
        }
        else
        {
            Logger.Warning("[AUDIT] Auth FAILED: ViewerId={ViewerId}, Reason={Reason}, Timestamp={Timestamp:u}",
                viewerId, reason, DateTimeOffset.UtcNow);
        }
    }

    public static void LogRateLimitTriggered(string viewerId, TimeSpan lockoutDuration)
    {
        Logger.Warning("[AUDIT] Rate Limit Lockout: ViewerId={ViewerId}, LockoutDuration={Duration}, Timestamp={Timestamp:u}",
            viewerId, lockoutDuration, DateTimeOffset.UtcNow);
    }

    public static void LogEmergencyKill(string hostId, string source)
    {
        Logger.Warning("[AUDIT] Emergency Kill Triggered: HostId={HostId}, Source={Source}, Timestamp={Timestamp:u}",
            hostId, source, DateTimeOffset.UtcNow);
    }

    public static void LogTamperingDetected(string sessionId, string fromId, string reason)
    {
        Logger.Error("[AUDIT] SECURITY TAMPERING DETECTED: SessionId={SessionId}, FromId={FromId}, Reason={Reason}, Timestamp={Timestamp:u}",
            sessionId, fromId, reason, DateTimeOffset.UtcNow);
    }
}
