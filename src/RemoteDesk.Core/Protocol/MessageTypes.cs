namespace RemoteDesk.Core.Protocol;

public static class DataChannels
{
    public const byte Control = 1;
    public const byte Input = 2;
    public const byte Clipboard = 3;
    public const byte Files = 4;
    public const byte Terminal = 5;
}

public enum MessageType : ushort
{
    // Control (0x0001 - 0x00FF)
    HandshakeRequest = 0x0001,
    HandshakeResponse = 0x0002,
    PermissionUpdate = 0x0003,
    HeartbeatPing = 0x0004,
    HeartbeatPong = 0x0005,

    // Input (0x0100 - 0x01FF)
    MouseMove = 0x0100,
    MouseButton = 0x0101,
    MouseWheel = 0x0102,
    KeyState = 0x0103,
    TextUnicode = 0x0104,

    // Clipboard (0x0200 - 0x02FF)
    ClipboardSync = 0x0200,

    // Files (0x0300 - 0x03FF)
    FileTransferRequest = 0x0300,
    FileChunk = 0x0301,
    FileTransferAck = 0x0302,
    FileTransferCancel = 0x0303
}
