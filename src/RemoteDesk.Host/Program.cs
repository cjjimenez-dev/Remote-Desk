using System.Diagnostics;
using RemoteDesk.Core.Interfaces;
using RemoteDesk.Platform.Windows;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateLogger();

Log.Information("=== RemoteDesk Phase 1 Pipeline Test ===");

try
{
    using IScreenCapturer capturer = new GdiScreenCapturer();
    using IVideoEncoder encoder = new FFmpegH264Encoder();
    using IVideoDecoder decoder = new FFmpegH264Decoder();

    var displays = capturer.GetDisplays();
    Log.Information("Target display: {Display}", displays.FirstOrDefault()?.DeviceName ?? "Default");

    var frameCount = 0;
    var totalRawBytes = 0L;
    var totalEncodedBytes = 0L;
    var totalEncodeMs = 0.0;
    var totalDecodeMs = 0.0;
    var totalEndToEndMs = 0.0;

    var sw = Stopwatch.StartNew();

    capturer.FrameCaptured += frame =>
    {
        var frameIndex = Interlocked.Increment(ref frameCount);
        Interlocked.Add(ref totalRawBytes, frame.Data.Length);

        // 1. Measure Encode
        var encodeSw = Stopwatch.StartNew();
        var encoded = encoder.Encode(frame);
        encodeSw.Stop();
        var encodeMs = encodeSw.Elapsed.TotalMilliseconds;

        if (encoded != null)
        {
            Interlocked.Add(ref totalEncodedBytes, encoded.Data.Length);

            // 2. Measure Decode
            var decodeSw = Stopwatch.StartNew();
            using var decoded = decoder.Decode(encoded.Data);
            decodeSw.Stop();
            var decodeMs = decodeSw.Elapsed.TotalMilliseconds;

            var endToEndMs = (DateTime.UtcNow.Ticks - frame.TimestampTicks) / (double)TimeSpan.TicksPerMillisecond;

            totalEncodeMs += encodeMs;
            totalDecodeMs += decodeMs;
            totalEndToEndMs += endToEndMs;

            if (frameIndex <= 3 || frameIndex % 15 == 0)
            {
                var compressionRatio = (double)frame.Data.Length / Math.Max(1, encoded.Data.Length);
                Log.Information("[Frame #{Index:D3}] Raw: {RawKb:F0} KB -> H264: {EncKb:F1} KB ({Ratio:F1}x) | KeyFrame: {Key} | Enc: {EncMs:F1}ms | Dec: {DecMs:F1}ms | Total Latency: {LatMs:F1}ms",
                    frameIndex,
                    frame.Data.Length / 1024.0,
                    encoded.Data.Length / 1024.0,
                    compressionRatio,
                    encoded.IsKeyFrame,
                    encodeMs,
                    decodeMs,
                    endToEndMs);
            }
        }

        frame.Dispose();
    };

    Log.Information("Starting pipeline loop at 30 FPS for 3 seconds...");
    capturer.Start(targetFps: 30);

    await Task.Delay(3000);
    capturer.Stop();
    sw.Stop();

    var elapsedSec = sw.Elapsed.TotalSeconds;
    var fps = frameCount / elapsedSec;
    var rawMb = totalRawBytes / (1024.0 * 1024.0);
    var encMb = totalEncodedBytes / (1024.0 * 1024.0);
    var avgBitrateKbps = (totalEncodedBytes * 8.0) / (elapsedSec * 1000.0);

    Log.Information("=== Phase 1 Pipeline Results ===");
    Log.Information("  Frames processed: {Count} ({Fps:F1} FPS)", frameCount, fps);
    Log.Information("  Raw throughput:   {RawMb:F2} MB", rawMb);
    Log.Information("  H.264 bandwidth:  {EncMb:F2} MB (Average Bitrate: {Bitrate:F0} kbps)", encMb, avgBitrateKbps);
    Log.Information("  Avg Encode time:  {Enc:F2} ms", frameCount > 0 ? totalEncodeMs / frameCount : 0);
    Log.Information("  Avg Decode time:  {Dec:F2} ms", frameCount > 0 ? totalDecodeMs / frameCount : 0);
    Log.Information("  Avg E2E Latency:  {Lat:F2} ms", frameCount > 0 ? totalEndToEndMs / frameCount : 0);
    Log.Information("================================");
}
catch (Exception ex)
{
    Log.Fatal(ex, "Pipeline test failed.");
}
finally
{
    await Log.CloseAndFlushAsync();
}
