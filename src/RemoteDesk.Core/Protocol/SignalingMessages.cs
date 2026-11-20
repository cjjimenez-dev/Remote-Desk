using MessagePack;

namespace RemoteDesk.Core.Protocol;

[MessagePackObject]
public sealed class SignalingHostInfo
{
    [Key(0)]
    public string ConnectionId { get; set; } = string.Empty;

    [Key(1)]
    public string HostId { get; set; } = string.Empty;

    [Key(2)]
    public string DeviceName { get; set; } = string.Empty;

    [Key(3)]
    public DateTime RegisteredAtUtc { get; set; } = DateTime.UtcNow;
}

[MessagePackObject]
public sealed class SignalingConnectionRequest
{
    [Key(0)]
    public string SessionId { get; set; } = string.Empty;

    [Key(1)]
    public string ViewerConnectionId { get; set; } = string.Empty;

    [Key(2)]
    public string ViewerName { get; set; } = string.Empty;

    [Key(3)]
    public string TargetHostId { get; set; } = string.Empty;
}

[MessagePackObject]
public sealed class SignalingConnectionResponse
{
    [Key(0)]
    public string SessionId { get; set; } = string.Empty;

    [Key(1)]
    public bool Accepted { get; set; }

    [Key(2)]
    public string HostConnectionId { get; set; } = string.Empty;

    [Key(3)]
    public string? RejectionReason { get; set; }
}

[MessagePackObject]
public sealed class SignalingSdpMessage
{
    [Key(0)]
    public string SessionId { get; set; } = string.Empty;

    [Key(1)]
    public string TargetConnectionId { get; set; } = string.Empty;

    [Key(2)]
    public string Sdp { get; set; } = string.Empty;
}

[MessagePackObject]
public sealed class SignalingIceCandidateMessage
{
    [Key(0)]
    public string SessionId { get; set; } = string.Empty;

    [Key(1)]
    public string TargetConnectionId { get; set; } = string.Empty;

    [Key(2)]
    public string Candidate { get; set; } = string.Empty;

    [Key(3)]
    public string SdpMid { get; set; } = string.Empty;

    [Key(4)]
    public int SdpMLineIndex { get; set; }
}
