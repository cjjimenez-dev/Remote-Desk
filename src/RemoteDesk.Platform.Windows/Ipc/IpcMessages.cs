using MessagePack;
using RemoteDesk.Core.Models;

namespace RemoteDesk.Platform.Windows.Ipc;

public enum IpcMessageType : byte
{
    Ping = 0,
    Pong = 1,
    StartSession = 2,
    StopSession = 3,
    SessionStateChanged = 4,
    SendSas = 5,
    SasTriggered = 6,
    DesktopSwitched = 7,
    QueryStatus = 8,
    StatusResponse = 9,
    AgentReady = 10,
    AgentExiting = 11
}

[MessagePackObject]
public sealed class IpcMessage
{
    [Key(0)]
    public IpcMessageType Type { get; set; }

    [Key(1)]
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString("N");

    [Key(2)]
    public byte[]? Payload { get; set; }
}

[MessagePackObject]
public sealed class IpcStartSessionRequest
{
    [Key(0)]
    public string SessionId { get; set; } = string.Empty;

    [Key(1)]
    public string ViewerConnectionId { get; set; } = string.Empty;

    [Key(2)]
    public string ViewerDeviceId { get; set; } = string.Empty;

    [Key(3)]
    public string ViewerName { get; set; } = string.Empty;

    [Key(4)]
    public SessionPermissions Permissions { get; set; } = SessionPermissions.All;

    [Key(5)]
    public string HostDeviceId { get; set; } = string.Empty;

    [Key(6)]
    public string SignalingServerUrl { get; set; } = string.Empty;

    [Key(7)]
    public bool ForceRelay { get; set; }
}

[MessagePackObject]
public sealed class IpcSessionStateEvent
{
    [Key(0)]
    public string SessionId { get; set; } = string.Empty;

    [Key(1)]
    public TransportState State { get; set; }

    [Key(2)]
    public ConnectionPath Path { get; set; }

    [Key(3)]
    public string? ErrorMessage { get; set; }
}

[MessagePackObject]
public sealed class IpcDesktopSwitchedEvent
{
    [Key(0)]
    public string DesktopName { get; set; } = string.Empty;

    [Key(1)]
    public bool IsSecureDesktop { get; set; }

    [Key(2)]
    public uint SessionId { get; set; }
}

[MessagePackObject]
public sealed class IpcStatusResponse
{
    [Key(0)]
    public bool IsSessionActive { get; set; }

    [Key(1)]
    public string? ActiveSessionId { get; set; }

    [Key(2)]
    public string CurrentDesktop { get; set; } = string.Empty;

    [Key(3)]
    public uint ProcessId { get; set; }

    [Key(4)]
    public uint SessionId { get; set; }
}
