using System.Runtime.InteropServices;
using RemoteDesk.Core.Interfaces;
using RemoteDesk.Core.Models;
using Serilog;

namespace RemoteDesk.Platform.Windows;

public sealed partial class GdiScreenCapturer : IScreenCapturer
{
    private static readonly ILogger Logger = Log.ForContext<GdiScreenCapturer>();

    private int _targetDisplayIndex;
    private List<DisplayInfo> _displays = [];
    private CancellationTokenSource? _cts;
    private Task? _captureTask;

    public event Action<CapturedFrame>? FrameCaptured;
    public event Action<string>? ErrorOccurred;

    public bool IsRunning => _cts is not null && !_cts.IsCancellationRequested;
    public DisplayInfo? CurrentDisplay => _displays.Count > _targetDisplayIndex ? _displays[_targetDisplayIndex] : null;

    public GdiScreenCapturer()
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

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint hObject);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint hdc, nint hgdiobj);

    [LibraryImport("user32.dll")]
    private static partial nint GetDesktopWindow();

    [LibraryImport("user32.dll")]
    private static partial nint GetWindowDC(nint hWnd);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint hWnd, nint hDC);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BitBlt(nint hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, nint hdcSrc, int nXSrc, int nYSrc, uint dwRop);

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(nint hdc, ref BITMAPINFO pbmi, uint iUsage, out nint ppvBits, nint hSection, uint dwOffset);

    private const uint SRCCOPY = 0x00CC0020;
    private const uint CAPTUREBLT = 0x40000000;

    private void CaptureLoop(int targetFps, CancellationToken token)
    {
        var intervalMs = Math.Max(1, 1000 / targetFps);

        nint hDesktopWnd = nint.Zero;
        nint hDesktopDC = nint.Zero;
        nint hMemoryDC = nint.Zero;
        nint hBitmap = nint.Zero;
        nint hOldBitmap = nint.Zero;
        nint pBits = nint.Zero;
        int allocWidth = 0;
        int allocHeight = 0;

        void CleanupGdiResources()
        {
            if (hMemoryDC != nint.Zero)
            {
                if (hOldBitmap != nint.Zero) SelectObject(hMemoryDC, hOldBitmap);
                if (hBitmap != nint.Zero) DeleteObject(hBitmap);
                DeleteDC(hMemoryDC);
                hMemoryDC = nint.Zero;
                hBitmap = nint.Zero;
                hOldBitmap = nint.Zero;
                pBits = nint.Zero;
            }
            if (hDesktopDC != nint.Zero && hDesktopWnd != nint.Zero)
            {
                ReleaseDC(hDesktopWnd, hDesktopDC);
                hDesktopDC = nint.Zero;
                hDesktopWnd = nint.Zero;
            }
            allocWidth = 0;
            allocHeight = 0;
        }

        try
        {
            while (!token.IsCancellationRequested)
            {
                var target = CurrentDisplay;
                if (target is null)
                {
                    Thread.Sleep(200);
                    continue;
                }

                var width = target.Width;
                var height = target.Height;
                var stride = width * 4;
                var totalBytes = stride * height;

                // Re-initialize GDI surface only if resolution changes or first frame
                if (width != allocWidth || height != allocHeight || hMemoryDC == nint.Zero)
                {
                    CleanupGdiResources();

                    hDesktopWnd = GetDesktopWindow();
                    hDesktopDC = GetWindowDC(hDesktopWnd);
                    hMemoryDC = CreateCompatibleDC(hDesktopDC);

                    var bmi = new BITMAPINFO();
                    bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
                    bmi.bmiHeader.biWidth = width;
                    bmi.bmiHeader.biHeight = -height; // Top-down
                    bmi.bmiHeader.biPlanes = 1;
                    bmi.bmiHeader.biBitCount = 32;
                    bmi.bmiHeader.biCompression = 0; // BI_RGB

                    hBitmap = CreateDIBSection(hMemoryDC, ref bmi, 0, out pBits, nint.Zero, 0);
                    hOldBitmap = SelectObject(hMemoryDC, hBitmap);

                    allocWidth = width;
                    allocHeight = height;
                }

                try
                {
                    BitBlt(hMemoryDC, 0, 0, width, height, hDesktopDC, target.X, target.Y, SRCCOPY | CAPTUREBLT);

                    if (pBits != nint.Zero)
                    {
                        var buffer = new byte[totalBytes];
                        Marshal.Copy(pBits, buffer, 0, totalBytes);

                        var frame = new CapturedFrame(
                            data: buffer,
                            width: width,
                            height: height,
                            stride: stride,
                            format: PixelFormat.Bgra32,
                            timestampTicks: DateTime.UtcNow.Ticks);

                        FrameCaptured?.Invoke(frame);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "GDI screen capture failed.");
                    ErrorOccurred?.Invoke(ex.Message);
                }

                try
                {
                    Thread.Sleep(intervalMs);
                }
                catch
                {
                    break;
                }
            }
        }
        finally
        {
            CleanupGdiResources();
        }
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
            // Ignore
        }
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose()
    {
        Stop();
    }
}
