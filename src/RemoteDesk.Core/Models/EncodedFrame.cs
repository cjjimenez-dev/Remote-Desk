namespace RemoteDesk.Core.Models;

public sealed class EncodedFrame
{
    public ReadOnlyMemory<byte> Data { get; }
    public bool IsKeyFrame { get; }
    public int Width { get; }
    public int Height { get; }
    public long TimestampTicks { get; }

    public EncodedFrame(
        ReadOnlyMemory<byte> data,
        bool isKeyFrame,
        int width,
        int height,
        long timestampTicks)
    {
        Data = data;
        IsKeyFrame = isKeyFrame;
        Width = width;
        Height = height;
        TimestampTicks = timestampTicks;
    }
}

public sealed class DecodedFrame : IDisposable
{
    public ReadOnlyMemory<byte> Data { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public PixelFormat Format { get; }
    public long TimestampTicks { get; }

    private readonly IDisposable? _lifetime;

    public DecodedFrame(
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
