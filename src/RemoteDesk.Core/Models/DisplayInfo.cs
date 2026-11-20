namespace RemoteDesk.Core.Models;

public record DisplayInfo(
    int Id,
    string DeviceName,
    int X,
    int Y,
    int Width,
    int Height,
    bool IsPrimary,
    double DpiScale = 1.0);
