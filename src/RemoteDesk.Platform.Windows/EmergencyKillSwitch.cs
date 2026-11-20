using System.Runtime.InteropServices;
using RemoteDesk.Core.Interfaces;
using Serilog;

namespace RemoteDesk.Platform.Windows;

public sealed partial class EmergencyKillSwitch : IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<EmergencyKillSwitch>();

    private const int HOTKEY_ID = 9001;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_ESCAPE = 0x1B;
    private const uint WM_HOTKEY = 0x0312;

    private readonly IInputInjector _injector;
    private CancellationTokenSource? _cts;
    private Task? _listenerTask;

    public event Action? EmergencyKillTriggered;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint hWnd, int id);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [DllImport("user32.dll")]
    private static extern sbyte GetMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessageA(uint idThread, uint msg, nint wParam, nint lParam);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    public EmergencyKillSwitch(IInputInjector injector)
    {
        _injector = injector;
    }

    public void Start()
    {
        if (_cts is not null) return;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        var tcs = new TaskCompletionSource();

        _listenerTask = Task.Factory.StartNew(() =>
        {
            var threadId = GetCurrentThreadId();
            var success = RegisterHotKey(nint.Zero, HOTKEY_ID, MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_NOREPEAT, VK_ESCAPE);
            if (!success)
            {
                Logger.Warning("Failed to register emergency kill hotkey (Ctrl+Alt+Shift+Esc). Error: {Error}", Marshal.GetLastWin32Error());
            }
            else
            {
                Logger.Information("Emergency kill hotkey (Ctrl+Alt+Shift+Esc) active.");
            }

            tcs.SetResult();

            while (!token.IsCancellationRequested)
            {
                var ret = GetMessage(out var msg, nint.Zero, 0, 0);
                if (ret <= 0) break;

                if (msg.message == WM_HOTKEY && (int)msg.wParam == HOTKEY_ID)
                {
                    Logger.Warning("EMERGENCY KILL SWITCH TRIGGERED! Input disabled.");
                    _injector.IsEnabled = false;
                    EmergencyKillTriggered?.Invoke();
                }
            }

            UnregisterHotKey(nint.Zero, HOTKEY_ID);
        }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        tcs.Task.Wait(500);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose()
    {
        Stop();
    }
}
