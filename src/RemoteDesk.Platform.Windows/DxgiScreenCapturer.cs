using System.Runtime.InteropServices;
using RemoteDesk.Core.Interfaces;
using RemoteDesk.Core.Models;
using Serilog;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RemoteDesk.Platform.Windows;

public sealed class DxgiScreenCapturer : IScreenCapturer
{
    private static readonly ILogger Logger = Log.ForContext<DxgiScreenCapturer>();

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _stagingTexture;

    private int _targetDisplayIndex;
    private List<DisplayInfo> _displays = [];
    private CancellationTokenSource? _cts;
    private Task? _captureTask;

    public event Action<CapturedFrame>? FrameCaptured;
    public event Action<string>? ErrorOccurred;

    public bool IsRunning => _cts is not null && !_cts.IsCancellationRequested;
    public DisplayInfo? CurrentDisplay => _displays.Count > _targetDisplayIndex ? _displays[_targetDisplayIndex] : null;

    public DxgiScreenCapturer()
    {
        _displays = NativeMethods.EnumerateDisplays();
    }

    public IReadOnlyList<DisplayInfo> GetDisplays()
    {
        _displays = NativeMethods.EnumerateDisplays();
        return _displays;
    }

    public void SetTargetDisplay(int displayId)
    {
        if (displayId < 0 || displayId >= _displays.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(displayId));
        }

        if (_targetDisplayIndex != displayId)
        {
            var wasRunning = IsRunning;
            if (wasRunning) Stop();
            _targetDisplayIndex = displayId;
            if (wasRunning) Start();
        }
    }

    public void Start(int targetFps = 30)
    {
        if (IsRunning) return;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _captureTask = Task.Run(() => CaptureLoop(targetFps, token), token);
    }

    private void InitializeD3D()
    {
        CleanupD3D();

        var featureLevels = new[]
        {
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0
        };

        var result = D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            featureLevels,
            out _device,
            out _context);

        if (result.Failure || _device is null || _context is null)
        {
            throw new InvalidOperationException($"Failed to create Direct3D11 device. Result: {result}");
        }

        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        var enumResult = adapter.EnumOutputs((uint)_targetDisplayIndex, out var output);
        if (enumResult.Failure || output is null)
        {
            throw new InvalidOperationException($"Failed to enumerate display output {_targetDisplayIndex}. Result: {enumResult}");
        }

        using (output)
        {
            using var output1 = output.QueryInterface<IDXGIOutput1>();
            _duplication = output1.DuplicateOutput(_device);
        }
    }

    private unsafe void CaptureLoop(int targetFps, CancellationToken token)
    {
        var frameIntervalMs = Math.Max(1, 1000 / targetFps);

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (_duplication is null)
                {
                    InitializeD3D();
                }

                var acquireResult = _duplication!.AcquireNextFrame(
                    100,
                    out var frameInfo,
                    out var desktopResource);

                if (acquireResult.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code)
                {
                    Thread.Sleep(5);
                    continue;
                }

                if (acquireResult.Failure)
                {
                    if (acquireResult.Code == Vortice.DXGI.ResultCode.AccessLost.Code ||
                        acquireResult.Code == Vortice.DXGI.ResultCode.AccessDenied.Code)
                    {
                        Logger.Warning("DXGI Output Duplication access lost/denied. Re-initializing...");
                        CleanupD3D();
                        Thread.Sleep(500);
                        continue;
                    }

                    throw new InvalidOperationException($"AcquireNextFrame failed: {acquireResult}");
                }

                using (desktopResource)
                {
                    using var desktopTexture = desktopResource.QueryInterface<ID3D11Texture2D>();
                    var desc = desktopTexture.Description;

                    EnsureStagingTexture(desc);

                    _context!.CopyResource(_stagingTexture!, desktopTexture);

                    var mapped = _context.Map(_stagingTexture!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                    try
                    {
                        var width = (int)desc.Width;
                        var height = (int)desc.Height;
                        var rowPitch = (int)mapped.RowPitch;
                        var totalBytes = rowPitch * height;

                        var buffer = new byte[totalBytes];
                        new ReadOnlySpan<byte>((void*)mapped.DataPointer, totalBytes).CopyTo(buffer);

                        var frame = new CapturedFrame(
                            data: buffer,
                            width: width,
                            height: height,
                            stride: rowPitch,
                            format: PixelFormat.Bgra32,
                            timestampTicks: DateTime.UtcNow.Ticks);

                        FrameCaptured?.Invoke(frame);
                    }
                    finally
                    {
                        _context.Unmap(_stagingTexture!, 0);
                    }
                }

                _duplication.ReleaseFrame();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error during DXGI capture loop.");
                ErrorOccurred?.Invoke(ex.Message);
                CleanupD3D();
                Thread.Sleep(500);
            }
        }

        CleanupD3D();
    }

    private void EnsureStagingTexture(Texture2DDescription sourceDesc)
    {
        if (_stagingTexture is not null)
        {
            var current = _stagingTexture.Description;
            if (current.Width == sourceDesc.Width &&
                current.Height == sourceDesc.Height &&
                current.Format == sourceDesc.Format)
            {
                return;
            }

            _stagingTexture.Dispose();
            _stagingTexture = null;
        }

        var stagingDesc = new Texture2DDescription
        {
            Width = sourceDesc.Width,
            Height = sourceDesc.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = sourceDesc.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None
        };

        _stagingTexture = _device!.CreateTexture2D(stagingDesc);
    }

    public void Stop()
    {
        _cts?.Cancel();
        try
        {
            _captureTask?.Wait(1000);
        }
        catch
        {
            // Ignore cancel exceptions
        }

        _cts?.Dispose();
        _cts = null;
        CleanupD3D();
    }

    private void CleanupD3D()
    {
        _stagingTexture?.Dispose();
        _stagingTexture = null;

        _duplication?.Dispose();
        _duplication = null;

        _context?.Dispose();
        _context = null;

        _device?.Dispose();
        _device = null;
    }

    public void Dispose()
    {
        Stop();
    }
}
