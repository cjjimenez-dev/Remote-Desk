using System.Runtime.InteropServices;
using RemoteDesk.Core.Interfaces;
using RemoteDesk.Core.Models;
using Serilog;

namespace RemoteDesk.Platform.Windows;

public sealed partial class WindowsInputInjector : IInputInjector
{
    private static readonly ILogger Logger = Log.ForContext<WindowsInputInjector>();

    public bool IsEnabled { get; set; } = true;
    public DisplayInfo? TargetDisplay { get; private set; }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;
        [FieldOffset(0)]
        public KEYBDINPUT ki;
        [FieldOffset(0)]
        public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_XDOWN = 0x0080;
    private const uint MOUSEEVENTF_XUP = 0x0100;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_HWHEEL = 0x1000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    private const uint XBUTTON1 = 0x0001;
    private const uint XBUTTON2 = 0x0002;

    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_SCANCODE = 0x0008;

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int nIndex);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    public WindowsInputInjector()
    {
        var displays = NativeMethods.EnumerateDisplays();
        TargetDisplay = displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault();
    }

    public void SetTargetDisplay(DisplayInfo display)
    {
        TargetDisplay = display;
    }

    public void InjectMouseMove(int targetX, int targetY, int clientWidth, int clientHeight)
    {
        if (!IsEnabled || clientWidth <= 0 || clientHeight <= 0) return;

        var display = TargetDisplay;
        var displayX = display?.X ?? 0;
        var displayY = display?.Y ?? 0;
        var displayW = display?.Width ?? clientWidth;
        var displayH = display?.Height ?? clientHeight;

        // Calculate absolute desktop pixel coordinate
        var absolutePixelX = displayX + (int)((long)targetX * displayW / clientWidth);
        var absolutePixelY = displayY + (int)((long)targetY * displayH / clientHeight);

        // Virtual screen bounds
        var vX = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var vY = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var vW = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        var vH = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        if (vW <= 0 || vH <= 0) return;

        // Normalized to [0, 65535]
        var normalizedX = (int)(((long)(absolutePixelX - vX) * 65535) / (vW - 1));
        var normalizedY = (int)(((long)(absolutePixelY - vY) * 65535) / (vH - 1));

        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = normalizedX,
                    dy = normalizedY,
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                    time = 0,
                    dwExtraInfo = nint.Zero
                }
            }
        };

        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    public void InjectMouseButton(MouseButton button, ButtonState state)
    {
        if (!IsEnabled) return;

        uint flags = 0;
        uint mouseData = 0;

        switch (button)
        {
            case MouseButton.Left:
                flags = state == ButtonState.Down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP;
                break;
            case MouseButton.Right:
                flags = state == ButtonState.Down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP;
                break;
            case MouseButton.Middle:
                flags = state == ButtonState.Down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP;
                break;
            case MouseButton.XButton1:
                flags = state == ButtonState.Down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP;
                mouseData = XBUTTON1;
                break;
            case MouseButton.XButton2:
                flags = state == ButtonState.Down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP;
                mouseData = XBUTTON2;
                break;
        }

        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = 0,
                    dy = 0,
                    mouseData = mouseData,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = nint.Zero
                }
            }
        };

        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    public void InjectMouseWheel(int deltaX, int deltaY)
    {
        if (!IsEnabled) return;

        var inputs = new List<INPUT>();

        if (deltaY != 0)
        {
            inputs.Add(new INPUT
            {
                type = INPUT_MOUSE,
                u = new InputUnion
                {
                    mi = new MOUSEINPUT
                    {
                        mouseData = (uint)deltaY,
                        dwFlags = MOUSEEVENTF_WHEEL,
                        time = 0,
                        dwExtraInfo = nint.Zero
                    }
                }
            });
        }

        if (deltaX != 0)
        {
            inputs.Add(new INPUT
            {
                type = INPUT_MOUSE,
                u = new InputUnion
                {
                    mi = new MOUSEINPUT
                    {
                        mouseData = (uint)deltaX,
                        dwFlags = MOUSEEVENTF_HWHEEL,
                        time = 0,
                        dwExtraInfo = nint.Zero
                    }
                }
            });
        }

        if (inputs.Count > 0)
        {
            SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
        }
    }

    public void InjectKey(ushort scanCode, bool isExtended, KeyState state)
    {
        if (!IsEnabled) return;

        uint flags = KEYEVENTF_SCANCODE;
        if (isExtended) flags |= KEYEVENTF_EXTENDEDKEY;
        if (state == KeyState.Up) flags |= KEYEVENTF_KEYUP;

        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = scanCode,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = nint.Zero
                }
            }
        };

        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    public void InjectUnicodeText(string text)
    {
        if (!IsEnabled || string.IsNullOrEmpty(text)) return;

        var inputs = new List<INPUT>();
        foreach (var ch in text)
        {
            // Key down
            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = ch,
                        dwFlags = KEYEVENTF_UNICODE,
                        time = 0,
                        dwExtraInfo = nint.Zero
                    }
                }
            });

            // Key up
            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = ch,
                        dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP,
                        time = 0,
                        dwExtraInfo = nint.Zero
                    }
                }
            });
        }

        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }
}
