using RemoteDesk.Core.Models;
using Serilog;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;

namespace RemoteDesk.Platform.Windows;

public sealed class FFmpegH264Encoder : RemoteDesk.Core.Interfaces.IVideoEncoder
{
    private static readonly ILogger Logger = Log.ForContext<FFmpegH264Encoder>();

    private FFmpegVideoEncoder? _encoder;
    private int _width;
    private int _height;
    private int _targetFps;
    private int _bitrateKbps;
    private bool _requestKeyFrameNext;
    private readonly object _lock = new();

    public FFmpegH264Encoder()
    {
        FFmpegBootstrapper.Initialize();
    }

    public void Initialize(int width, int height, int targetFps = 30, int bitrateKbps = 2500)
    {
        lock (_lock)
        {
            _width = width;
            _height = height;
            _targetFps = targetFps;
            _bitrateKbps = bitrateKbps;

            _encoder?.Dispose();
            _encoder = new FFmpegVideoEncoder();

            var bitrateBps = bitrateKbps * 1000;
            _encoder.SetBitrate(bitrateBps, bitrateBps / 2, bitrateBps / 4, bitrateBps * 2);
            _encoder.SetThreadCount(Environment.ProcessorCount);
            _requestKeyFrameNext = true;
        }
    }

    public EncodedFrame? Encode(CapturedFrame frame)
    {
        lock (_lock)
        {
            if (_encoder is null || frame.Width != _width || frame.Height != _height)
            {
                Initialize(frame.Width, frame.Height, _targetFps, _bitrateKbps);
            }

            if (_requestKeyFrameNext)
            {
                _encoder!.ForceKeyFrame();
                _requestKeyFrameNext = false;
            }

            try
            {
                var encodedBytes = _encoder!.EncodeVideo(
                    frame.Width,
                    frame.Height,
                    frame.Data.ToArray(),
                    VideoPixelFormatsEnum.Bgra,
                    VideoCodecsEnum.H264);

                if (encodedBytes is null || encodedBytes.Length == 0)
                {
                    return null;
                }

                var isKeyFrame = IsH264KeyFrame(encodedBytes);

                return new EncodedFrame(
                    data: encodedBytes,
                    isKeyFrame: isKeyFrame,
                    width: frame.Width,
                    height: frame.Height,
                    timestampTicks: frame.TimestampTicks);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "FFmpeg H.264 encode failed.");
                return null;
            }
        }
    }

    public void SetBitrate(int bitrateKbps)
    {
        lock (_lock)
        {
            _bitrateKbps = bitrateKbps;
            var bitrateBps = bitrateKbps * 1000;
            _encoder?.SetBitrate(bitrateBps, bitrateBps / 2, bitrateBps / 4, bitrateBps * 2);
        }
    }

    public void RequestKeyFrame()
    {
        lock (_lock)
        {
            _requestKeyFrameNext = true;
        }
    }

    private static bool IsH264KeyFrame(byte[] data)
    {
        for (var i = 0; i < data.Length - 4; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0)
            {
                var nalIndex = -1;
                if (data[i + 2] == 1)
                {
                    nalIndex = i + 3;
                }
                else if (data[i + 2] == 0 && data[i + 3] == 1)
                {
                    nalIndex = i + 4;
                }

                if (nalIndex >= 0 && nalIndex < data.Length)
                {
                    var nalType = data[nalIndex] & 0x1F;
                    if (nalType == 5 || nalType == 7) // IDR (5) or SPS (7)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _encoder?.Dispose();
            _encoder = null;
        }
    }
}
