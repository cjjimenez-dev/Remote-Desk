using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Serilog;

namespace RemoteDesk.Platform.Windows.Terminal;

public sealed class PseudoConsole : IDisposable
{
    private const int S_OK = 0;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const nint PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

    [StructLayout(LayoutKind.Sequential)]
    public struct COORD
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public nint lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public nint lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public nint lpReserved2;
        public nint hStdInput;
        public nint hStdOutput;
        public nint hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public nint hProcess;
        public nint hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(
        COORD size,
        SafeFileHandle hInput,
        SafeFileHandle hOutput,
        uint dwFlags,
        out nint phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(nint hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(nint hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(
        out SafeFileHandle hReadPipe,
        out SafeFileHandle hWritePipe,
        ref SECURITY_ATTRIBUTES lpPipeAttributes,
        int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(
        nint lpAttributeList,
        int dwAttributeCount,
        int dwFlags,
        ref nint lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(
        nint lpAttributeList,
        uint dwFlags,
        nint attribute,
        nint lpValue,
        nint cbSize,
        nint lpPreviousValue,
        nint lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteProcThreadAttributeList(nint lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        string lpCommandLine,
        ref SECURITY_ATTRIBUTES lpProcessAttributes,
        ref SECURITY_ATTRIBUTES lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        nint lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint hObject);

    private readonly ILogger _logger = Log.ForContext<PseudoConsole>();

    public nint Handle { get; private set; }
    public SafeFileHandle InputPipeWrite { get; }
    public SafeFileHandle OutputPipeRead { get; }
    public SafeProcessHandle ProcessHandle { get; private set; } = new(nint.Zero, ownsHandle: true);
    public int ProcessId { get; private set; }

    private bool _disposed;

    private PseudoConsole(nint handle, SafeFileHandle inputWrite, SafeFileHandle outputRead)
    {
        Handle = handle;
        InputPipeWrite = inputWrite;
        OutputPipeRead = outputRead;
    }

    public static PseudoConsole Create(string commandLine, int columns = 80, int rows = 25)
    {
        var sa = new SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            bInheritHandle = 1
        };

        // 1. Create Pipes for PseudoConsole communication
        // Input: Process reads from InRead, We write to InWrite
        if (!CreatePipe(out var hPipeInRead, out var hPipeInWrite, ref sa, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create input pipe.");

        // Output: Process writes to OutWrite, We read from OutRead
        if (!CreatePipe(out var hPipeOutRead, out var hPipeOutWrite, ref sa, 0))
        {
            hPipeInRead.Dispose();
            hPipeInWrite.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create output pipe.");
        }

        var coord = new COORD { X = (short)columns, Y = (short)rows };
        int hr = CreatePseudoConsole(coord, hPipeInRead, hPipeOutWrite, 0, out nint hPC);

        // Note: ConPTY takes ownership of hPipeInRead and hPipeOutWrite
        hPipeInRead.Dispose();
        hPipeOutWrite.Dispose();

        if (hr != S_OK)
        {
            hPipeInWrite.Dispose();
            hPipeOutRead.Dispose();
            throw new Win32Exception(hr, $"CreatePseudoConsole failed with HRESULT 0x{hr:X8}");
        }

        var pty = new PseudoConsole(hPC, hPipeInWrite, hPipeOutRead);

        try
        {
            pty.SpawnProcess(commandLine);
        }
        catch
        {
            pty.Dispose();
            throw;
        }

        return pty;
    }

    private void SpawnProcess(string commandLine)
    {
        // 2. Initialize STARTUPINFOEX with PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE
        nint lpSize = nint.Zero;
        InitializeProcThreadAttributeList(nint.Zero, 1, 0, ref lpSize);

        nint lpAttributeList = Marshal.AllocHGlobal(lpSize);
        if (!InitializeProcThreadAttributeList(lpAttributeList, 1, 0, ref lpSize))
        {
            Marshal.FreeHGlobal(lpAttributeList);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed.");
        }

        nint hPcPointer = Handle;
        if (!UpdateProcThreadAttribute(
            lpAttributeList,
            0,
            PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
            hPcPointer,
            (nint)nint.Size,
            nint.Zero,
            nint.Zero))
        {
            DeleteProcThreadAttributeList(lpAttributeList);
            Marshal.FreeHGlobal(lpAttributeList);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed.");
        }

        var startupInfoEx = new STARTUPINFOEX
        {
            StartupInfo = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFOEX>()
            },
            lpAttributeList = lpAttributeList
        };

        var pSec = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>() };
        var tSec = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>() };

        bool success = CreateProcessW(
            lpApplicationName: null,
            lpCommandLine: commandLine,
            lpProcessAttributes: ref pSec,
            lpThreadAttributes: ref tSec,
            bInheritHandles: false,
            dwCreationFlags: EXTENDED_STARTUPINFO_PRESENT,
            lpEnvironment: nint.Zero,
            lpCurrentDirectory: null,
            lpStartupInfo: ref startupInfoEx,
            out var pi);

        DeleteProcThreadAttributeList(lpAttributeList);
        Marshal.FreeHGlobal(lpAttributeList);

        if (!success)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateProcessW failed for '{commandLine}'");
        }

        ProcessHandle = new SafeProcessHandle(pi.hProcess, ownsHandle: true);
        ProcessId = pi.dwProcessId;
        CloseHandle(pi.hThread);

        _logger.Information("Spawned ConPTY process '{Cmd}' with PID {Pid}", commandLine, ProcessId);
    }

    public void Resize(int columns, int rows)
    {
        if (_disposed || Handle == nint.Zero) return;
        var coord = new COORD { X = (short)columns, Y = (short)rows };
        ResizePseudoConsole(Handle, coord);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (Handle != nint.Zero)
        {
            ClosePseudoConsole(Handle);
            Handle = nint.Zero;
        }

        InputPipeWrite.Dispose();
        OutputPipeRead.Dispose();
        ProcessHandle.Dispose();
    }
}
