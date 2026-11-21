using MessagePack;

namespace RemoteDesk.Core.Models;

[MessagePackObject]
public record DisplayInfo(
    [property: Key(0)] int Id,
    [property: Key(1)] string DeviceName,
    [property: Key(2)] int X,
    [property: Key(3)] int Y,
    [property: Key(4)] int Width,
    [property: Key(5)] int Height,
    [property: Key(6)] bool IsPrimary,
    [property: Key(7)] double DpiScale = 1.0);
