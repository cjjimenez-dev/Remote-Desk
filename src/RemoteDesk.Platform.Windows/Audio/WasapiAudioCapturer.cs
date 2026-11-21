using NAudio.CoreAudioApi;
using NAudio.Wave;
using RemoteDesk.Core.Interfaces;
using Serilog;

#pragma warning disable CS0618
namespace RemoteDesk.Platform.Windows.Audio;

public sealed class WasapiAudioCapturer : IAudioCapturer
{
    private readonly ILogger _logger = Log.ForContext<WasapiAudioCapturer>();
    private WasapiLoopbackCapture? _capture;
    private readonly object _lock = new();
    private bool _isCapturing;
    private bool _disposed;

    public event Action<ReadOnlyMemory<byte>>? AudioSampleCaptured;
    public event Action<string>? ErrorOccurred;

    public bool IsCapturing
    {
        get
        {
            lock (_lock) return _isCapturing;
        }
    }

    public WaveFormat? WaveFormat => _capture?.WaveFormat;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            if (_isCapturing) return;

            try
            {
                _capture = new WasapiLoopbackCapture();
                _logger.Information("Initialized WASAPI loopback capture: {Channels}ch, {SampleRate}Hz, {Bits}bit",
                    _capture.WaveFormat.Channels, _capture.WaveFormat.SampleRate, _capture.WaveFormat.BitsPerSample);

                _capture.DataAvailable += OnDataAvailable;
                _capture.RecordingStopped += OnRecordingStopped;

                _capture.StartRecording();
                _isCapturing = true;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to start WASAPI loopback capture.");
                ErrorOccurred?.Invoke(ex.Message);
            }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!_isCapturing) return;

            try
            {
                _capture?.StopRecording();
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Exception while stopping WASAPI loopback capture.");
            }
            finally
            {
                _isCapturing = false;
            }
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;

        try
        {
            var buffer = new byte[e.BytesRecorded];
            Buffer.BlockCopy(e.Buffer, 0, buffer, 0, e.BytesRecorded);
            AudioSampleCaptured?.Invoke(buffer);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Error dispatching audio sample.");
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        lock (_lock)
        {
            _isCapturing = false;
        }

        if (e.Exception != null)
        {
            _logger.Error(e.Exception, "WASAPI audio capture stopped unexpectedly.");
            ErrorOccurred?.Invoke(e.Exception.Message);
        }
        else
        {
            _logger.Information("WASAPI audio capture stopped normally.");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();

        lock (_lock)
        {
            if (_capture != null)
            {
                _capture.DataAvailable -= OnDataAvailable;
                _capture.RecordingStopped -= OnRecordingStopped;
                _capture.Dispose();
                _capture = null;
            }
        }
    }
}
