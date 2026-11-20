using System.Runtime.InteropServices;
using RemoteDesk.Core.Protocol;
using Serilog;

namespace RemoteDesk.Platform.Windows;

public sealed partial class CursorTracker : IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<CursorTracker>();

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public nint hCursor;
        public POINT ptScreenPos;
    }

    private const int CURSOR_SHOWING = 0x00000001;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorInfo(out CURSORINFO pci);

    public event Action<CursorPositionMessage>? CursorPositionChanged;
    public event Action<CursorShapeMessage>? CursorShapeChanged;

    private CancellationTokenSource? _cts;
    private Task? _trackTask;
    private nint _lastCursorHandle = nint.Zero;
    private int _lastX = -1;
    private int _lastY = -1;
    private bool _lastVisible = true;

    public void Start(int pollingRateHz = 60)
    {
        if (_cts is not null) return;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var intervalMs = Math.Max(1, 1000 / pollingRateHz);

        _trackTask = Task.Run(async () =>
        {
            var ci = new CURSORINFO();
            ci.cbSize = Marshal.SizeOf<CURSORINFO>();

            while (!token.IsCancellationRequested)
            {
                if (GetCursorInfo(out ci))
                {
                    var isVisible = (ci.flags & CURSOR_SHOWING) != 0;
                    var x = ci.ptScreenPos.x;
                    var y = ci.ptScreenPos.y;

                    if (x != _lastX || y != _lastY || isVisible != _lastVisible)
                    {
                        _lastX = x;
                        _lastY = y;
                        _lastVisible = isVisible;

                        CursorPositionChanged?.Invoke(new CursorPositionMessage
                        {
                            X = x,
                            Y = y,
                            IsVisible = isVisible
                        });
                    }

                    if (ci.hCursor != _lastCursorHandle && ci.hCursor != nint.Zero)
                    {
                        _lastCursorHandle = ci.hCursor;
                        var shape = ExtractCursorShape(ci.hCursor);
                        if (shape != null)
                        {
                            CursorShapeChanged?.Invoke(shape);
                        }
                    }
                }

                await Task.Delay(intervalMs, token).ConfigureAwait(false);
            }
        }, token);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public int fIcon; // BOOL is 4-byte int
        public int xHotspot;
        public int yHotspot;
        public nint hbmMask;
        public nint hbmColor;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetIconInfo(nint hIcon, out ICONINFO piconinfo);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint hObject);

    private CursorShapeMessage? ExtractCursorShape(nint hCursor)
    {
        try
        {
            if (!GetIconInfo(hCursor, out var iconInfo))
            {
                return null;
            }

            try
            {
                var hash = (ulong)hCursor.ToInt64() ^ ((ulong)iconInfo.xHotspot << 32) ^ (ulong)iconInfo.yHotspot;

                return new CursorShapeMessage
                {
                    ShapeHash = hash,
                    HotSpotX = iconInfo.xHotspot,
                    HotSpotY = iconInfo.yHotspot,
                    Width = 32,
                    Height = 32,
                    ImageData = []
                };
            }
            finally
            {
                if (iconInfo.hbmColor != nint.Zero) DeleteObject(iconInfo.hbmColor);
                if (iconInfo.hbmMask != nint.Zero) DeleteObject(iconInfo.hbmMask);
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to extract cursor shape.");
            return null;
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        try
        {
            _trackTask?.Wait(500);
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
