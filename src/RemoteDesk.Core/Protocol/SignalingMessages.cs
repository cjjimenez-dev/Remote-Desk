using MessagePack;
using RemoteDesk.Core.Models;

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

    [Key(4)]
    public byte[]? PublicKey { get; set; }
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

    [Key(4)]
    public string ViewerDeviceId { get; set; } = string.Empty;

    [Key(5)]
    public byte[]? ViewerPublicKey { get; set; }

    [Key(6)]
    public byte[]? RequestSignature { get; set; }

    [Key(7)]
    public long Timestamp { get; set; }
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

    [Key(4)]
    public SessionPermissions GrantedPermissions { get; set; } = SessionPermissions.All;

    [Key(5)]
    public string HostDeviceId { get; set; } = string.Empty;
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

    [Key(3)]
    public string SenderDeviceId { get; set; } = string.Empty;

    [Key(4)]
    public byte[]? SenderPublicKey { get; set; }

    [Key(5)]
    public string DtlsFingerprint { get; set; } = string.Empty;

    [Key(6)]
    public byte[]? DtlsFingerprintSignature { get; set; }

    [Key(7)]
    public long Timestamp { get; set; }

    [Key(8)]
    public string FromConnectionId { get; set; } = string.Empty;
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

    [Key(5)]
    public string FromConnectionId { get; set; } = string.Empty;
}
