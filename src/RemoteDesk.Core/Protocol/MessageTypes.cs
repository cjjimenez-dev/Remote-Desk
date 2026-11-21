namespace RemoteDesk.Core.Protocol;

public static class DataChannels
{
    public const byte Control = 1;
    public const byte Input = 2;
    public const byte Clipboard = 3;
    public const byte Files = 4;
    public const byte Terminal = 5;
    public const byte Audio = 6;
    public const byte Chat = 7;
}

public enum MessageType : ushort
{
    // Control (0x0001 - 0x00FF)
    HandshakeRequest = 0x0001,
    HandshakeResponse = 0x0002,
    PermissionUpdate = 0x0003,
    HeartbeatPing = 0x0004,
    HeartbeatPong = 0x0005,
    EmergencyStop = 0x0006,
    DisplayList = 0x0010,
    SwitchDisplay = 0x0011,
    QualityMetricsReport = 0x0020,
    QualitySettingsUpdate = 0x0021,

    // Input (0x0100 - 0x01FF)
    MouseMove = 0x0100,
    MouseButton = 0x0101,
    MouseWheel = 0x0102,
    KeyState = 0x0103,
    TextUnicode = 0x0104,
    CursorPosition = 0x0105,
    CursorShape = 0x0106,

    // Clipboard (0x0200 - 0x02FF)
    ClipboardSync = 0x0200,
    ClipboardText = 0x0201,
    ClipboardImage = 0x0202,

    // Files (0x0300 - 0x03FF)
    FileTransferRequest = 0x0300,
    FileTransferResponse = 0x0301,
    FileChunk = 0x0302,
    FileTransferAck = 0x0303,
    FileTransferCancel = 0x0304,

    // Audio (0x0400 - 0x04FF)
    AudioPacket = 0x0400,
    AudioControl = 0x0401,

    // Chat (0x0500 - 0x05FF)
    SessionChat = 0x0500
}
