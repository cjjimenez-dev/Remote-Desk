namespace RemoteDesk.Core.Interfaces;

public interface IAudioCapturer : IDisposable
{
    event Action<ReadOnlyMemory<byte>>? AudioSampleCaptured;
    event Action<string>? ErrorOccurred;

    void Start();
    void Stop();
    bool IsCapturing { get; }
}
