using MessagePack;

namespace RemoteDesk.Core.Protocol;

[MessagePackObject]
public sealed class CursorPositionMessage
{
    [Key(0)]
    public int X { get; set; }

    [Key(1)]
    public int Y { get; set; }

    [Key(2)]
    public bool IsVisible { get; set; }
}

[MessagePackObject]
public sealed class CursorShapeMessage
{
    [Key(0)]
    public ulong ShapeHash { get; set; }

    [Key(1)]
    public int HotSpotX { get; set; }

    [Key(2)]
    public int HotSpotY { get; set; }

    [Key(3)]
    public int Width { get; set; }

    [Key(4)]
    public int Height { get; set; }

    [Key(5)]
    public byte[] ImageData { get; set; } = [];
}
