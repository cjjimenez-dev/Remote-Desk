using System.Diagnostics;
using System.Runtime.InteropServices;
using RemoteDesk.Core.Models;
using Serilog;

namespace RemoteDesk.Platform.Windows.Fleet;

public static class WindowsFleetCommandHandler
{
    private static readonly ILogger Logger = Log.ForContext(typeof(WindowsFleetCommandHandler));

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    public static async Task<(FleetCommandStatus Status, string Output)> ExecuteCommandAsync(FleetCommand command)
    {
        Logger.Information("Executing Fleet Command [{CmdId}] of type {Type}", command.CommandId, command.CommandType);

        try
        {
            switch (command.CommandType)
            {
                case FleetCommandType.LockWorkstation:
                    bool locked = LockWorkStation();
                    return locked
                        ? (FleetCommandStatus.Completed, "Workstation locked successfully.")
                        : (FleetCommandStatus.Failed, $"LockWorkStation returned false. Win32 error: {Marshal.GetLastWin32Error()}");

                case FleetCommandType.Reboot:
                    Logger.Warning("Initiating scheduled reboot via shutdown.exe...");
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "shutdown.exe",
                        Arguments = "/r /t 10 /c \"Reboot initiated by RemoteDesk Fleet Manager\"",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    });
                    return (FleetCommandStatus.Completed, "System reboot scheduled in 10 seconds.");

                case FleetCommandType.RestartAgent:
                    Logger.Information("Restarting RemoteDesk Agent process...");
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(1000);
                        var path = Environment.ProcessPath;
                        if (!string.IsNullOrEmpty(path))
                        {
                            Process.Start(path);
                        }
                        Environment.Exit(0);
                    });
                    return (FleetCommandStatus.Completed, "Agent restart sequence initiated.");

                case FleetCommandType.GetDiagnostics:
                    var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
                    using (var currentProc = Process.GetCurrentProcess())
                    {
                        var memMb = currentProc.WorkingSet64 / (1024 * 1024);
                        var diag = $"Host: {Environment.MachineName}, Uptime: {uptime.Days}d {uptime.Hours}h {uptime.Minutes}m, Agent Mem: {memMb} MB, CPU Threads: {Environment.ProcessorCount}";
                        return (FleetCommandStatus.Completed, diag);
                    }

                default:
                    return (FleetCommandStatus.Failed, $"Unsupported command type: {command.CommandType}");
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error executing Fleet Command {Type}", command.CommandType);
            return (FleetCommandStatus.Failed, $"Execution failed: {ex.Message}");
        }
    }
}
