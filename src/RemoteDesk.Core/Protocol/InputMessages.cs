using MessagePack;
using RemoteDesk.Core.Models;

namespace RemoteDesk.Core.Protocol;

[MessagePackObject]
public sealed class MouseMoveMessage
{
    [Key(0)]
    public int X { get; set; }

    [Key(1)]
    public int Y { get; set; }

    [Key(2)]
    public int ScreenWidth { get; set; }

    [Key(3)]
    public int ScreenHeight { get; set; }
}

[MessagePackObject]
public sealed class MouseButtonMessage
{
    [Key(0)]
    public MouseButton Button { get; set; }

    [Key(1)]
    public ButtonState State { get; set; }
}

[MessagePackObject]
public sealed class MouseWheelMessage
{
    [Key(0)]
    public int DeltaX { get; set; }

    [Key(1)]
    public int DeltaY { get; set; }
}

[MessagePackObject]
public sealed class KeyMessage
{
    [Key(0)]
    public ushort ScanCode { get; set; }

    [Key(1)]
    public bool IsExtended { get; set; }

    [Key(2)]
    public KeyState State { get; set; }
}

[MessagePackObject]
public sealed class UnicodeTextMessage
{
    [Key(0)]
    public string Text { get; set; } = string.Empty;
}
