namespace RemoteDesk.Core.Models;

public enum PixelFormat
{
    Bgra32,
    Rgba32,
    Nv12,
    I420
}

public sealed class CapturedFrame : IDisposable
{
    public ReadOnlyMemory<byte> Data { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public PixelFormat Format { get; }
    public long TimestampTicks { get; }

    private readonly IDisposable? _lifetime;

    public CapturedFrame(
        ReadOnlyMemory<byte> data,
        int width,
        int height,
        int stride,
        PixelFormat format,
        long timestampTicks,
        IDisposable? lifetime = null)
    {
        Data = data;
        Width = width;
        Height = height;
        Stride = stride;
        Format = format;
        TimestampTicks = timestampTicks;
        _lifetime = lifetime;
    }

    public void Dispose()
    {
        _lifetime?.Dispose();
    }
}
