using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using RemoteDesk.Core.Models;
using Serilog;

namespace RemoteDesk.Platform.Windows.Fleet;

public static class WindowsSystemTelemetry
{
    private static readonly ILogger Logger = Log.ForContext(typeof(WindowsSystemTelemetry));

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    public static DeviceRecord CollectDeviceRecord(string deviceId, byte[]? publicKey = null, List<string>? tags = null)
    {
        return new DeviceRecord
        {
            DeviceId = deviceId,
            Hostname = Environment.MachineName,
            OperatingSystem = RuntimeInformation.OSDescription,
            AgentVersion = typeof(WindowsSystemTelemetry).Assembly.GetName().Version?.ToString() ?? "1.0.0",
            IpAddress = GetLocalIPv4Address(),
            TotalMemoryBytes = (long)GetTotalPhysicalMemoryBytes(),
            CpuModel = GetCpuModelName(),
            Status = DeviceStatus.Online,
            Tags = tags ?? ["Windows", "Workstation"],
            LastHeartbeatUtc = DateTime.UtcNow,
            RegisteredAtUtc = DateTime.UtcNow,
            PublicKey = publicKey
        };
    }

    public static ulong GetTotalPhysicalMemoryBytes()
    {
        try
        {
            var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref mem))
            {
                return mem.ullTotalPhys;
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to retrieve physical memory via GlobalMemoryStatusEx.");
        }
        return 0;
    }

    public static string GetCpuModelName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var name = key?.GetValue("ProcessorNameString") as string;
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name.Trim();
            }
        }
        catch { }

        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "x64 Processor";
    }

    public static string GetLocalIPv4Address()
    {
        try
        {
            foreach (var net in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (net.OperationalStatus != OperationalStatus.Up ||
                    net.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                foreach (var ip in net.GetIPProperties().UnicastAddresses)
                {
                    if (ip.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(ip.Address))
                    {
                        return ip.Address.ToString();
                    }
                }
            }
        }
        catch { }

        return "127.0.0.1";
    }
}
