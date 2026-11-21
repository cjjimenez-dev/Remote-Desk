using System.Runtime.InteropServices;
using Serilog;

namespace RemoteDesk.Platform.Windows.Session;

public sealed class DesktopDetector : IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<DesktopDetector>();

    private const uint DESKTOP_READOBJECTS = 0x0001;
    private const int UOI_NAME = 2;
    private const int ERROR_ACCESS_DENIED = 5;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(
        IntPtr hObj,
        int nIndex,
        [Out] char[] pvInfo,
        uint nLength,
        out uint lpnLengthNeeded);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    private readonly PeriodicTimer _timer;
    private readonly CancellationTokenSource _cts = new();
    private Task? _pollTask;
    private string _currentDesktop = "Default";
    private bool _isSecureDesktop;

    public event Action<string, bool>? DesktopChanged;

    public string CurrentDesktop => _currentDesktop;
    public bool IsSecureDesktop => _isSecureDesktop;

    public DesktopDetector(TimeSpan? pollInterval = null)
    {
        _timer = new PeriodicTimer(pollInterval ?? TimeSpan.FromMilliseconds(500));
    }

    public void Start()
    {
        if (_pollTask != null) return;
        _pollTask = Task.Run(() => PollLoopAsync(_cts.Token));
        Logger.Information("DesktopDetector started.");
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && await _timer.WaitForNextTickAsync(ct))
        {
            try
            {
                var (desktop, isSecure) = QueryActiveInputDesktop();
                if (desktop != _currentDesktop || isSecure != _isSecureDesktop)
                {
                    Logger.Information("Desktop switch detected: [{OldDesktop}] -> [{NewDesktop}] (Secure: {IsSecure})",
                        _currentDesktop, desktop, isSecure);

                    _currentDesktop = desktop;
                    _isSecureDesktop = isSecure;
                    DesktopChanged?.Invoke(desktop, isSecure);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Error querying active input desktop.");
            }
        }
    }

    public static (string DesktopName, bool IsSecure) QueryActiveInputDesktop()
    {
        var hDesktop = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
        if (hDesktop == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            if (err == ERROR_ACCESS_DENIED)
            {
                // Access denied from non-SYSTEM process typically indicates Secure Desktop is active
                return ("Winlogon", true);
            }

            return ("Unknown", false);
        }

        try
        {
            var buffer = new char[256];
            if (GetUserObjectInformation(hDesktop, UOI_NAME, buffer, (uint)(buffer.Length * sizeof(char)), out var lengthNeeded))
            {
                var len = (int)(lengthNeeded / sizeof(char));
                if (len > 0 && buffer[len - 1] == '\0') len--;
                var name = new string(buffer, 0, len);
                var isSecure = string.Equals(name, "Winlogon", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(name, "Screen-saver", StringComparison.OrdinalIgnoreCase);

                return (name, isSecure);
            }

            return ("Default", false);
        }
        finally
        {
            CloseDesktop(hDesktop);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _timer.Dispose();
        _cts.Dispose();
    }
}
