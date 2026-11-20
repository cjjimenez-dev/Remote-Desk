using RemoteDesk.Core.Models;

namespace RemoteDesk.Core.Interfaces;

public interface IScreenCapturer : IDisposable
{
    event Action<CapturedFrame>? FrameCaptured;
    event Action<string>? ErrorOccurred;

    IReadOnlyList<DisplayInfo> GetDisplays();
    void SetTargetDisplay(int displayId);
    void Start(int targetFps = 30);
    void Stop();
    bool IsRunning { get; }
    DisplayInfo? CurrentDisplay { get; }
}
