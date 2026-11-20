using RemoteDesk.Core.Models;
using Serilog;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;

namespace RemoteDesk.Platform.Windows;

public sealed class FFmpegH264Decoder : RemoteDesk.Core.Interfaces.IVideoDecoder
{
    private static readonly ILogger Logger = Log.ForContext<FFmpegH264Decoder>();

    private FFmpegVideoEncoder? _decoder;
    private int _width;
    private int _height;
    private readonly object _lock = new();

    public FFmpegH264Decoder()
    {
        FFmpegBootstrapper.Initialize();
    }

    public void Initialize(int width, int height)
    {
        lock (_lock)
        {
            _width = width;
            _height = height;

            _decoder?.Dispose();
            _decoder = new FFmpegVideoEncoder();
        }
    }

    public DecodedFrame? Decode(ReadOnlyMemory<byte> encodedData)
    {
        lock (_lock)
        {
            if (_decoder is null)
            {
                Initialize(_width > 0 ? _width : 1920, _height > 0 ? _height : 1080);
            }

            try
            {
                var frames = _decoder!.DecodeVideo(
                    encodedData.ToArray(),
                    VideoPixelFormatsEnum.Bgra,
                    VideoCodecsEnum.H264);

                if (frames is null)
                {
                    return null;
                }

                foreach (var sample in frames)
                {
                    if (sample.Sample is null || sample.Sample.Length == 0)
                    {
                        continue;
                    }

                    _width = (int)sample.Width;
                    _height = (int)sample.Height;
                    var stride = _width * 4;

                    return new DecodedFrame(
                        data: sample.Sample,
                        width: _width,
                        height: _height,
                        stride: stride,
                        format: PixelFormat.Bgra32,
                        timestampTicks: DateTime.UtcNow.Ticks);
                }

                return null;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "FFmpeg H.264 decode failed.");
                return null;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _decoder?.Dispose();
            _decoder = null;
        }
    }
}
