using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteDesk.Core.Interfaces;
using RemoteDesk.Core.Models;
using RemoteDesk.Platform.Windows;
using RemoteDesk.Viewer.Rendering;
using Serilog;

namespace RemoteDesk.Viewer.ViewModels;

public partial class ViewerViewModel : ObservableObject, IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<ViewerViewModel>();

    private readonly Dispatcher _dispatcher;
    private readonly WriteableBitmapVideoRenderer _renderer;
    private IScreenCapturer? _capturer;
    private IVideoEncoder? _encoder;
    private IVideoDecoder? _decoder;
    private IInputInjector? _injector;
    private EmergencyKillSwitch? _killSwitch;
    private CursorTracker? _cursorTracker;

    private readonly DispatcherTimer _statsTimer;
    private int _framesThisInterval;
    private long _bytesThisInterval;
    private double _sumEncodeMs;
    private double _sumDecodeMs;
    private double _sumLatencyMs;
    private int _measurementCount;

    [ObservableProperty]
    private WriteableBitmap? _videoImage;

    [ObservableProperty]
    private bool _isLoopbackActive;

    [ObservableProperty]
    private bool _isInputEnabled = true;

    [ObservableProperty]
    private bool _isEmergencyKillTriggered;

    [ObservableProperty]
    private string _fpsText = "0.0 FPS";

    [ObservableProperty]
    private string _latencyText = "0.0 ms";

    [ObservableProperty]
    private string _encodeDecodeText = "Enc: 0.0ms | Dec: 0.0ms";

    [ObservableProperty]
    private string _bitrateText = "0 kbps";

    [ObservableProperty]
    private string _resolutionText = "0 x 0";

    [ObservableProperty]
    private string _statusText = "Ready - Press 'Start Loopback' to test local pipeline";

    [ObservableProperty]
    private string _cursorPositionText = "X: 0, Y: 0";

    [ObservableProperty]
    private int _selectedDisplayIndex;

    public ObservableCollection<string> AvailableDisplays { get; } = [];

    public IInputInjector? Injector => _injector;

    public ViewerViewModel(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _renderer = new WriteableBitmapVideoRenderer(dispatcher);
        _renderer.FrameRendered += () =>
        {
            if (VideoImage != _renderer.BitmapSource)
            {
                VideoImage = _renderer.BitmapSource;
            }
        };

        _statsTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _statsTimer.Tick += OnStatsTimerTick;

        LoadDisplays();
    }

    private void LoadDisplays()
    {
        try
        {
            using var capturer = new GdiScreenCapturer();
            var displays = capturer.GetDisplays();
            AvailableDisplays.Clear();
            for (var i = 0; i < displays.Count; i++)
            {
                var d = displays[i];
                AvailableDisplays.Add($"[{d.Id}] {d.DeviceName} ({d.Width}x{d.Height})");
            }

            if (AvailableDisplays.Count > 0)
            {
                SelectedDisplayIndex = 0;
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to enumerate displays.");
        }
    }

    [RelayCommand]
    private void ToggleLoopback()
    {
        if (IsLoopbackActive)
        {
            StopLoopback();
        }
        else
        {
            StartLoopback();
        }
    }

    [RelayCommand]
    private void ResetEmergencyKill()
    {
        if (_injector != null)
        {
            _injector.IsEnabled = true;
        }
        IsInputEnabled = true;
        IsEmergencyKillTriggered = false;
        StatusText = "Input control resumed.";
    }

    private void StartLoopback()
    {
        try
        {
            FFmpegBootstrapper.Initialize();

            _capturer = new GdiScreenCapturer();
            _capturer.SetTargetDisplay(SelectedDisplayIndex);

            _encoder = new FFmpegH264Encoder();
            _decoder = new FFmpegH264Decoder();
            _injector = new WindowsInputInjector();

            var currentDisplay = _capturer.CurrentDisplay;
            if (currentDisplay != null)
            {
                _injector.SetTargetDisplay(currentDisplay);
            }

            _killSwitch = new EmergencyKillSwitch(_injector);
            _killSwitch.EmergencyKillTriggered += () =>
            {
                _dispatcher.Invoke(() =>
                {
                    IsEmergencyKillTriggered = true;
                    IsInputEnabled = false;
                    StatusText = "EMERGENCY KILL TRIGGERED: Input disabled by host user.";
                });
            };
            _killSwitch.Start();

            _cursorTracker = new CursorTracker();
            _cursorTracker.CursorPositionChanged += pos =>
            {
                _dispatcher.Invoke(() => CursorPositionText = $"X: {pos.X}, Y: {pos.Y}");
            };
            _cursorTracker.Start();

            _capturer.FrameCaptured += OnFrameCaptured;
            _capturer.ErrorOccurred += err =>
            {
                _dispatcher.Invoke(() => StatusText = $"Error: {err}");
            };

            _capturer.Start(targetFps: 30);
            _statsTimer.Start();

            IsLoopbackActive = true;
            IsEmergencyKillTriggered = false;
            IsInputEnabled = true;
            StatusText = "Local loopback running with active input injection. Hotkey: Ctrl+Alt+Shift+Esc to kill.";
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to start loopback pipeline.");
            StatusText = $"Error starting loopback: {ex.Message}";
        }
    }

    private void OnFrameCaptured(CapturedFrame frame)
    {
        var frameTimestamp = frame.TimestampTicks;
        Interlocked.Increment(ref _framesThisInterval);

        // 1. Encode
        var encSw = Stopwatch.StartNew();
        var encoded = _encoder?.Encode(frame);
        encSw.Stop();
        var encMs = encSw.Elapsed.TotalMilliseconds;

        if (encoded != null)
        {
            Interlocked.Add(ref _bytesThisInterval, encoded.Data.Length);

            // 2. Decode
            var decSw = Stopwatch.StartNew();
            var decoded = _decoder?.Decode(encoded.Data);
            decSw.Stop();
            var decMs = decSw.Elapsed.TotalMilliseconds;

            if (decoded != null)
            {
                var totalLatencyMs = (DateTime.UtcNow.Ticks - frameTimestamp) / (double)TimeSpan.TicksPerMillisecond;

                lock (_statsTimer)
                {
                    _sumEncodeMs += encMs;
                    _sumDecodeMs += decMs;
                    _sumLatencyMs += totalLatencyMs;
                    _measurementCount++;
                }

                _renderer.RenderDecoded(decoded);
                decoded.Dispose();
            }
        }

        frame.Dispose();
    }

    private void OnStatsTimerTick(object? sender, EventArgs e)
    {
        var fps = _framesThisInterval;
        var bytes = _bytesThisInterval;
        _framesThisInterval = 0;
        _bytesThisInterval = 0;

        double avgEnc = 0, avgDec = 0, avgLat = 0;
        lock (_statsTimer)
        {
            if (_measurementCount > 0)
            {
                avgEnc = _sumEncodeMs / _measurementCount;
                avgDec = _sumDecodeMs / _measurementCount;
                avgLat = _sumLatencyMs / _measurementCount;
            }

            _sumEncodeMs = 0;
            _sumDecodeMs = 0;
            _sumLatencyMs = 0;
            _measurementCount = 0;
        }

        var bitrateKbps = (bytes * 8) / 1000.0;

        FpsText = $"{fps:F1} FPS";
        BitrateText = $"{bitrateKbps:F0} kbps";
        EncodeDecodeText = $"Enc: {avgEnc:F1}ms | Dec: {avgDec:F1}ms";
        LatencyText = $"E2E Latency: {avgLat:F1}ms";

        if (_renderer.BitmapSource != null)
        {
            ResolutionText = $"{_renderer.BitmapSource.PixelWidth} x {_renderer.BitmapSource.PixelHeight}";
        }
    }

    private void StopLoopback()
    {
        _statsTimer.Stop();

        _killSwitch?.Stop();
        _killSwitch?.Dispose();
        _killSwitch = null;

        _cursorTracker?.Stop();
        _cursorTracker?.Dispose();
        _cursorTracker = null;

        if (_capturer != null)
        {
            _capturer.Stop();
            _capturer.Dispose();
            _capturer = null;
        }

        _encoder?.Dispose();
        _encoder = null;

        _decoder?.Dispose();
        _decoder = null;

        _injector = null;

        IsLoopbackActive = false;
        StatusText = "Loopback stopped.";
    }

    public void Dispose()
    {
        StopLoopback();
        _renderer.Dispose();
    }
}
