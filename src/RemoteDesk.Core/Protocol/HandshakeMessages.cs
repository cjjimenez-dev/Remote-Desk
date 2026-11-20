using MessagePack;

namespace RemoteDesk.Core.Protocol;

public static class StandardCapabilities
{
    public const string VideoH264 = "video.h264";
    public const string VideoAv1 = "video.av1";
    public const string InputUnicode = "input.unicode";
    public const string ClipboardText = "clipboard.text";
    public const string ClipboardFiles = "clipboard.files";
    public const string FilesResume = "files.resume";
    public const string AudioOpus = "audio.opus";
    public const string Terminal = "terminal";

    public static readonly HashSet<string> AllSupported =
    [
        VideoH264,
        InputUnicode,
        ClipboardText,
        ClipboardFiles,
        FilesResume,
        AudioOpus,
        Terminal
    ];
}

[MessagePackObject]
public sealed class HandshakeRequest
{
    [Key(0)]
    public int MinProtocolVersion { get; set; } = 1;

    [Key(1)]
    public int MaxProtocolVersion { get; set; } = 1;

    [Key(2)]
    public string AppVersion { get; set; } = string.Empty;

    [Key(3)]
    public string OsInfo { get; set; } = string.Empty;

    [Key(4)]
    public HashSet<string> Capabilities { get; set; } = [];
}

[MessagePackObject]
public sealed class HandshakeResponse
{
    [Key(0)]
    public bool Accepted { get; set; }

    [Key(1)]
    public int AgreedProtocolVersion { get; set; }

    [Key(2)]
    public string SessionId { get; set; } = string.Empty;

    [Key(3)]
    public HashSet<string> AgreedCapabilities { get; set; } = [];

    [Key(4)]
    public string? RejectionReason { get; set; }

    [Key(5)]
    public Models.SessionPermissions GrantedPermissions { get; set; }
}
