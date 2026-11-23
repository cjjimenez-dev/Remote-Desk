using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Serilog;

namespace RemoteDesk.Viewer.Services;

public sealed class LanDiscoveryService : IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<LanDiscoveryService>();
    public const int DiscoveryPort = 5004;

    private readonly CancellationTokenSource _cts = new();
    private UdpClient? _listener;
    private UdpClient? _broadcaster;
    private string _localIp = "127.0.0.1";
    private bool _disposed;

    public event Action<string, int, string>? ServerDiscovered;

    public string LocalIp => _localIp;

    public void Start(bool isHost, int serverPort, string deviceName)
    {
        _localIp = GetLocalLanIp();

        // Start listener to discover any peer servers on the LAN
        StartListener();

        // If this machine is running the integrated signaling server, broadcast its presence
        if (isHost)
        {
            StartBroadcaster(serverPort, deviceName);
        }
    }

    private void StartListener()
    {
        try
        {
            _listener = new UdpClient();
            _listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            _listener.EnableBroadcast = true;

            _ = Task.Run(async () =>
            {
                var buffer = new byte[1024];
                while (!_cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        var result = await _listener.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                        var message = Encoding.UTF8.GetString(result.Buffer);

                        if (message.StartsWith("REMOTEDESK|"))
                        {
                            var parts = message.Split('|');
                            if (parts.Length >= 4)
                            {
                                var remoteIp = parts[1];
                                if (int.TryParse(parts[2], out var port) && remoteIp != _localIp && !remoteIp.StartsWith("127."))
                                {
                                    var name = parts[3];
                                    Logger.Information("Discovered RemoteDesk peer at {RemoteIp}:{Port} ({Name})", remoteIp, port, name);
                                    ServerDiscovered?.Invoke(remoteIp, port, name);
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug(ex, "LAN discovery receive error.");
                        await Task.Delay(1000, _cts.Token).ConfigureAwait(false);
                    }
                }
            }, _cts.Token);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Could not start LAN discovery listener on port {Port}", DiscoveryPort);
        }
    }

    private void StartBroadcaster(int serverPort, string deviceName)
    {
        try
        {
            _broadcaster = new UdpClient();
            _broadcaster.EnableBroadcast = true;

            _ = Task.Run(async () =>
            {
                var broadcastEp = new IPEndPoint(IPAddress.Broadcast, DiscoveryPort);
                var payload = Encoding.UTF8.GetBytes($"REMOTEDESK|{_localIp}|{serverPort}|{deviceName}");

                while (!_cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await _broadcaster.SendAsync(payload, payload.Length, broadcastEp).ConfigureAwait(false);
                        await Task.Delay(2500, _cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug(ex, "LAN discovery broadcast error.");
                        await Task.Delay(3000, _cts.Token).ConfigureAwait(false);
                    }
                }
            }, _cts.Token);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Could not start LAN discovery broadcaster.");
        }
    }

    public static string GetLocalLanIp()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            if (socket.LocalEndPoint is IPEndPoint endPoint)
            {
                return endPoint.Address.ToString();
            }
        }
        catch { }

        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus == OperationalStatus.Up &&
                    ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                {
                    foreach (var ip in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ip.Address.AddressFamily == AddressFamily.InterNetwork &&
                            !IPAddress.IsLoopback(ip.Address))
                        {
                            return ip.Address.ToString();
                        }
                    }
                }
            }
        }
        catch { }

        return "127.0.0.1";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
        _listener?.Dispose();
        _broadcaster?.Dispose();
    }
}
