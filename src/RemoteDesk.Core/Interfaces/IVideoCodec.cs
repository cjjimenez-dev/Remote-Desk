using RemoteDesk.Core.Models;

namespace RemoteDesk.Core.Interfaces;

public interface IVideoEncoder : IDisposable
{
    void Initialize(int width, int height, int targetFps = 30, int bitrateKbps = 2500);
    EncodedFrame? Encode(CapturedFrame frame);
    void SetBitrate(int bitrateKbps);
    void RequestKeyFrame();
}

public interface IVideoDecoder : IDisposable
{
    void Initialize(int width, int height);
    DecodedFrame? Decode(ReadOnlyMemory<byte> encodedData);
}
