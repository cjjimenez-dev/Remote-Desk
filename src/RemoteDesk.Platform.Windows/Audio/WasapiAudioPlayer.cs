using NAudio.Wave;
using Serilog;

#pragma warning disable CS0618
namespace RemoteDesk.Platform.Windows.Audio;

public sealed class WasapiAudioPlayer : IDisposable
{
    private readonly ILogger _logger = Log.ForContext<WasapiAudioPlayer>();
    private WasapiOut? _wasapiOut;
    private BufferedWaveProvider? _bufferedWaveProvider;
    private readonly object _lock = new();
    private bool _isPlaying;
    private bool _disposed;
    private float _volume = 1.0f;
    private bool _isMuted;

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            UpdateVolume();
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            _isMuted = value;
            UpdateVolume();
        }
    }

    private void UpdateVolume()
    {
        lock (_lock)
        {
            if (_wasapiOut != null)
            {
                try
                {
                    _wasapiOut.Volume = _isMuted ? 0f : _volume;
                }
                catch { }
            }
        }
    }

    public void PlaySample(ReadOnlyMemory<byte> pcmData, int sampleRate = 48000, int channels = 2)
    {
        if (_disposed || pcmData.IsEmpty) return;

        lock (_lock)
        {
            if (_wasapiOut == null || _bufferedWaveProvider == null ||
                _bufferedWaveProvider.WaveFormat.SampleRate != sampleRate ||
                _bufferedWaveProvider.WaveFormat.Channels != channels)
            {
                InitializePlayer(sampleRate, channels);
            }

            if (_bufferedWaveProvider != null && _isPlaying)
            {
                try
                {
                    byte[] array = pcmData.ToArray();
                    _bufferedWaveProvider.AddSamples(array, 0, array.Length);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to queue audio buffer for playback.");
                }
            }
        }
    }

    private void InitializePlayer(int sampleRate, int channels)
    {
        try
        {
            _wasapiOut?.Stop();
            _wasapiOut?.Dispose();

            // Default to 32-bit IEEE float or 16-bit PCM depending on stream
#pragma warning disable CS0618
            var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
            _bufferedWaveProvider = new BufferedWaveProvider(waveFormat)
            {
                DiscardOnBufferOverflow = true
            };

            _wasapiOut = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 50);
            _wasapiOut.Init(_bufferedWaveProvider);
            UpdateVolume();
            _wasapiOut.Play();
            _isPlaying = true;
#pragma warning restore CS0618

            _logger.Information("Initialized WASAPI audio output: {Channels}ch, {SampleRate}Hz", channels, sampleRate);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to initialize WASAPI audio playback.");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_lock)
        {
            _isPlaying = false;
            try
            {
                _wasapiOut?.Stop();
                _wasapiOut?.Dispose();
                _wasapiOut = null;
            }
            catch { }
        }
    }
}
