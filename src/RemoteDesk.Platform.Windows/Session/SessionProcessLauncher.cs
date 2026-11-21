using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Serilog;

namespace RemoteDesk.Platform.Windows.Session;

public static class SessionProcessLauncher
{
    private static readonly ILogger Logger = Log.ForContext(typeof(SessionProcessLauncher));

    private const uint MAXIMUM_ALLOWED = 0x02000000;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const int TokenSessionId = 12;

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
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint SessionId, out IntPtr phToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(
        IntPtr hExistingToken,
        uint dwDesiredAccess,
        IntPtr lpTokenAttributes,
        int ImpersonationLevel,
        int TokenType,
        out IntPtr phNewToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(
        IntPtr TokenHandle,
        int TokenInformationClass,
        ref uint TokenInformation,
        uint TokenInformationLength);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        IntPtr hToken,
        string? lpApplicationName,
        string? lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    public static uint GetActiveConsoleSession()
    {
        return WTSGetActiveConsoleSessionId();
    }

    public static Process? LaunchProcessInSession(
        string executablePath,
        string arguments,
        uint? targetSessionId = null,
        bool isSecureDesktop = false)
    {
        var sessionId = targetSessionId ?? GetActiveConsoleSession();
        if (sessionId == 0xFFFFFFFF)
        {
            Logger.Warning("Cannot launch process: No active console session available.");
            return null;
        }

        IntPtr userToken = IntPtr.Zero;
        IntPtr primaryToken = IntPtr.Zero;
        IntPtr environmentBlock = IntPtr.Zero;

        try
        {
            // 1. Try querying the active user's token
            var hasUserToken = WTSQueryUserToken(sessionId, out userToken);
            if (!hasUserToken || userToken == IntPtr.Zero)
            {
                var win32Err = Marshal.GetLastWin32Error();
                Logger.Information("WTSQueryUserToken failed (Win32: {Error}). User may be at lock screen. Using SYSTEM token for Session {SessionId}.", win32Err, sessionId);

                // Use duplicated SYSTEM token configured for console session
                if (!OpenProcessToken(GetCurrentProcess(), MAXIMUM_ALLOWED, out var sysToken))
                {
                    Logger.Error("OpenProcessToken failed for SYSTEM process. Win32: {Error}", Marshal.GetLastWin32Error());
                    return null;
                }

                try
                {
                    if (!DuplicateTokenEx(sysToken, MAXIMUM_ALLOWED, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out primaryToken))
                    {
                        Logger.Error("DuplicateTokenEx failed for SYSTEM token. Win32: {Error}", Marshal.GetLastWin32Error());
                        return null;
                    }

                    // Set token's Session ID to the console session
                    SetTokenInformation(primaryToken, TokenSessionId, ref sessionId, sizeof(uint));
                }
                finally
                {
                    CloseHandle(sysToken);
                }
            }
            else
            {
                // Duplicate the logged-on user's token
                if (!DuplicateTokenEx(userToken, MAXIMUM_ALLOWED, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out primaryToken))
                {
                    Logger.Error("DuplicateTokenEx failed. Win32: {Error}", Marshal.GetLastWin32Error());
                    return null;
                }
            }

            // 2. Create environment block
            CreateEnvironmentBlock(out environmentBlock, primaryToken, false);

            // 3. Prepare STARTUPINFO targeting WinSta0\Default or WinSta0\Winlogon
            var desktopName = isSecureDesktop ? @"WinSta0\Winlogon" : @"WinSta0\Default";
            var si = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                lpDesktop = desktopName
            };

            var commandLine = $"\"{executablePath}\" {arguments}";
            var creationFlags = CREATE_UNICODE_ENVIRONMENT;

            Logger.Information("Launching agent into Session {SessionId} on desktop [{Desktop}]: {Executable} {Args}",
                sessionId, desktopName, executablePath, arguments);

            var success = CreateProcessAsUser(
                primaryToken,
                null,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                creationFlags,
                environmentBlock,
                Path.GetDirectoryName(executablePath),
                ref si,
                out var pi);

            if (!success)
            {
                var err = Marshal.GetLastWin32Error();
                Logger.Error("CreateProcessAsUser failed with Win32 Error: {Error}", err);
                return null;
            }

            try
            {
                var proc = Process.GetProcessById(pi.dwProcessId);
                Logger.Information("Successfully spawned Agent Process ID {Pid} in Session {SessionId}", pi.dwProcessId, sessionId);
                return proc;
            }
            finally
            {
                CloseHandle(pi.hProcess);
                CloseHandle(pi.hThread);
            }
        }
        finally
        {
            if (environmentBlock != IntPtr.Zero)
            {
                DestroyEnvironmentBlock(environmentBlock);
            }

            if (primaryToken != IntPtr.Zero)
            {
                CloseHandle(primaryToken);
            }

            if (userToken != IntPtr.Zero)
            {
                CloseHandle(userToken);
            }
        }
    }
}
