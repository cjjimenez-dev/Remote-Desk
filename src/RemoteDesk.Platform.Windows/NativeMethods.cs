using System.Runtime.InteropServices;
using RemoteDesk.Core.Models;

namespace RemoteDesk.Platform.Windows;

internal static partial class NativeMethods
{
    private const int CCHDEVICENAME = 32;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    public delegate bool MonitorEnumProc(nint hMonitor, nint hdcMonitor, ref RECT lprcMonitor, nint dwData);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumDisplayMonitors(nint hdc, nint lprcClip, MonitorEnumProc lpfnEnum, nint dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFOEX lpmi);

    [LibraryImport("shcore.dll")]
    public static partial int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    public static List<DisplayInfo> EnumerateDisplays()
    {
        var displays = new List<DisplayInfo>();
        var index = 0;

        EnumDisplayMonitors(nint.Zero, nint.Zero, (nint hMonitor, nint _, ref RECT _, nint _) =>
        {
            var mi = new MONITORINFOEX();
            mi.cbSize = Marshal.SizeOf<MONITORINFOEX>();

            if (GetMonitorInfo(hMonitor, ref mi))
            {
                var isPrimary = (mi.dwFlags & 1) != 0;
                double dpiScale = 1.0;

                try
                {
                    if (GetDpiForMonitor(hMonitor, 0, out uint dpiX, out _) == 0 && dpiX > 0)
                    {
                        dpiScale = dpiX / 96.0;
                    }
                }
                catch
                {
                    // Fallback to 1.0 on older systems or error
                }

                displays.Add(new DisplayInfo(
                    Id: index++,
                    DeviceName: mi.szDevice ?? $"Display {index}",
                    X: mi.rcMonitor.Left,
                    Y: mi.rcMonitor.Top,
                    Width: mi.rcMonitor.Width,
                    Height: mi.rcMonitor.Height,
                    IsPrimary: isPrimary,
                    DpiScale: dpiScale
                ));
            }

            return true;
        }, nint.Zero);

        return displays;
    }
}
