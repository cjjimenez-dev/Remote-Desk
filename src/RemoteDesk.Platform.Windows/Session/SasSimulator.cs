using System.Runtime.InteropServices;
using Serilog;

namespace RemoteDesk.Platform.Windows.Session;

public static class SasSimulator
{
    private static readonly ILogger Logger = Log.ForContext(typeof(SasSimulator));

    [DllImport("sas.dll", SetLastError = true)]
    private static extern void SendSAS(bool AsUser);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_MENU = 0x12; // Alt
    private const ushort VK_DELETE = 0x2E;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    public static bool TriggerSas()
    {
        // 1. Attempt official SendSAS API from sas.dll (Windows Service running as SYSTEM)
        try
        {
            Logger.Information("Invoking SendSAS(false) from sas.dll...");
            SendSAS(false);
            Logger.Information("SendSAS successfully invoked.");
            return true;
        }
        catch (DllNotFoundException)
        {
            Logger.Warning("sas.dll not found on this system. Falling back to software simulation.");
        }
        catch (EntryPointNotFoundException)
        {
            Logger.Warning("SendSAS entry point not found in sas.dll. Falling back to software simulation.");
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "SendSAS failed with exception. Falling back to software simulation.");
        }

        // 2. Fallback: Software SendInput simulation (Ctrl + Alt + Del)
        try
        {
            Logger.Information("Attempting software simulation of Ctrl+Alt+Del via SendInput...");
            var inputs = new INPUT[]
            {
                // Ctrl down
                new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_CONTROL } },
                // Alt down
                new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_MENU } },
                // Del down
                new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_DELETE, dwFlags = KEYEVENTF_EXTENDEDKEY } },
                // Del up
                new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_DELETE, dwFlags = KEYEVENTF_KEYUP | KEYEVENTF_EXTENDEDKEY } },
                // Alt up
                new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_MENU, dwFlags = KEYEVENTF_KEYUP } },
                // Ctrl up
                new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = KEYEVENTF_KEYUP } }
            };

            var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
            return sent == inputs.Length;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to simulate Ctrl+Alt+Del via software.");
            return false;
        }
    }

    public static bool TriggerLockWorkstation()
    {
        Logger.Information("Calling LockWorkStation()...");
        return LockWorkStation();
    }
}
