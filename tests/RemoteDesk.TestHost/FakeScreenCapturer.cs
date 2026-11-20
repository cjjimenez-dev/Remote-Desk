using RemoteDesk.Core.Interfaces;
using RemoteDesk.Core.Models;

namespace RemoteDesk.TestHost;

public sealed class FakeScreenCapturer : IScreenCapturer
{
    private readonly int _width;
    private readonly int _height;
    private readonly DisplayInfo _display;
    private CancellationTokenSource? _cts;
    private Task? _captureLoopTask;
    private long _frameCounter;

    public event Action<CapturedFrame>? FrameCaptured;
    public event Action<string>? ErrorOccurred;

    public bool IsRunning => _cts is not null && !_cts.IsCancellationRequested;
    public DisplayInfo? CurrentDisplay => _display;

    public FakeScreenCapturer(int width = 1920, int height = 1080)
    {
        _width = width;
        _height = height;
        _display = new DisplayInfo(1, "Virtual Headless Display", 0, 0, width, height, true, 1.0);
    }

    public IReadOnlyList<DisplayInfo> GetDisplays() => [_display];

    public void SetTargetDisplay(int displayId)
    {
        // Headless test host uses virtual display
    }

    public void Start(int targetFps = 30)
    {
        if (IsRunning) return;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var intervalMs = Math.Max(1, 1000 / targetFps);

        _captureLoopTask = Task.Run(async () =>
        {
            var stride = _width * 4;
            var buffer = new byte[stride * _height];

            while (!token.IsCancellationRequested)
            {
                var start = DateTime.UtcNow.Ticks;
                _frameCounter++;

                // Generate moving gradient with frame counter tint
                var frameOffset = (byte)(_frameCounter % 256);
                for (var y = 0; y < _height; y += 4)
                {
                    var rowStart = y * stride;
                    for (var x = 0; x < _width; x += 4)
                    {
                        var pixelIdx = rowStart + (x * 4);
                        buffer[pixelIdx + 0] = (byte)((x + frameOffset) % 256); // B
                        buffer[pixelIdx + 1] = (byte)((y + frameOffset) % 256); // G
                        buffer[pixelIdx + 2] = frameOffset;                     // R
                        buffer[pixelIdx + 3] = 255;                             // A
                    }
                }

                var memoryCopy = new byte[buffer.Length];
                Buffer.BlockCopy(buffer, 0, memoryCopy, 0, buffer.Length);

                var frame = new CapturedFrame(
                    memoryCopy,
                    _width,
                    _height,
                    stride,
                    PixelFormat.Bgra32,
                    DateTime.UtcNow.Ticks
                );

                try
                {
                    FrameCaptured?.Invoke(frame);
                }
                catch (Exception ex)
                {
                    ErrorOccurred?.Invoke(ex.Message);
                }

                await Task.Delay(intervalMs, token).ConfigureAwait(false);
            }
        }, token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        try
        {
            _captureLoopTask?.Wait(500);
        }
        catch
        {
            // Ignore cancellation
        }
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose()
    {
        Stop();
    }
}
