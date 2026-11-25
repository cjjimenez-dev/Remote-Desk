using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using Serilog;

namespace RemoteDesk.Viewer.Services;

/// <summary>
/// Embeds and orchestrates Cloudflare Quick Tunnels directly inside RemoteDesk.
/// Automatically downloads cloudflared if missing, launches the tunnel as a child process,
/// captures the generated *.trycloudflare.com URL, and cleans up on exit.
/// </summary>
public sealed class CloudflareTunnelService : IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<CloudflareTunnelService>();
    private static readonly Regex TunnelUrlRegex = new(@"https://[a-zA-Z0-9\-]+\.trycloudflare\.com", RegexOptions.Compiled);

    private Process? _tunnelProcess;
    private readonly object _lock = new();
    private bool _isDisposed;

    public event Action<string>? TunnelUrlDiscovered;
    public event Action<string>? TunnelStatusChanged;
    public event Action<string>? TunnelError;

    public bool IsRunning => _tunnelProcess != null && !_tunnelProcess.HasExited;
    public string? CurrentTunnelUrl { get; private set; }
    public string? CurrentTunnelHost { get; private set; }

    public CloudflareTunnelService()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => StopTunnel();
    }

    public async Task<string?> StartTunnelAsync(int localPort = 5000, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (IsRunning && CurrentTunnelUrl != null)
            {
                return CurrentTunnelUrl;
            }
            StopTunnel();
        }

        TunnelStatusChanged?.Invoke("Locating cloudflared binary...");
        var binaryPath = await EnsureBinaryAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(binaryPath) || !File.Exists(binaryPath))
        {
            var err = "Cloudflare tunnel binary could not be found or downloaded.";
            Logger.Error(err);
            TunnelError?.Invoke(err);
            return null;
        }

        TunnelStatusChanged?.Invoke("Starting Cloudflare Tunnel on port " + localPort + "...");
        Logger.Information("Starting Cloudflare tunnel using {Binary} on port {Port}", binaryPath, localPort);

        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = ct.Register(() => tcs.TrySetResult(null));

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = binaryPath,
                Arguments = $"tunnel --url http://localhost:{localPort}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            void ProcessOutput(string? line)
            {
                if (string.IsNullOrWhiteSpace(line)) return;
                Logger.Debug("[cloudflared] {Line}", line);

                var match = TunnelUrlRegex.Match(line);
                if (match.Success)
                {
                    var url = match.Value;
                    var uri = new Uri(url);
                    var host = uri.Host;

                    lock (_lock)
                    {
                        CurrentTunnelUrl = url;
                        CurrentTunnelHost = host;
                    }

                    Logger.Information("Cloudflare Tunnel active at: {Url} (host: {Host})", url, host);
                    TunnelStatusChanged?.Invoke($"🟢 Tunnel Active: {host}");
                    TunnelUrlDiscovered?.Invoke(url);
                    tcs.TrySetResult(url);
                }
            }

            process.OutputDataReceived += (_, e) => ProcessOutput(e.Data);
            process.ErrorDataReceived += (_, e) => ProcessOutput(e.Data);

            process.Exited += (_, _) =>
            {
                Logger.Information("Cloudflare tunnel process exited.");
                TunnelStatusChanged?.Invoke("⚪ Tunnel Stopped");
                tcs.TrySetResult(null);
            };

            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to launch cloudflared process.");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            lock (_lock)
            {
                _tunnelProcess = process;
            }

            // Wait up to 20 seconds for tunnel URL discovery
            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(20), ct)).ConfigureAwait(false);
            if (completedTask == tcs.Task)
            {
                return await tcs.Task.ConfigureAwait(false);
            }
            else
            {
                TunnelStatusChanged?.Invoke("🟡 Tunnel is still connecting in background...");
                return null;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to start Cloudflare tunnel.");
            TunnelError?.Invoke($"Tunnel error: {ex.Message}");
            StopTunnel();
            return null;
        }
    }

    public void StopTunnel()
    {
        lock (_lock)
        {
            if (_tunnelProcess != null)
            {
                try
                {
                    if (!_tunnelProcess.HasExited)
                    {
                        _tunnelProcess.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "Error stopping cloudflared process.");
                }
                finally
                {
                    _tunnelProcess.Dispose();
                    _tunnelProcess = null;
                }
            }
            CurrentTunnelUrl = null;
            CurrentTunnelHost = null;
        }
        TunnelStatusChanged?.Invoke("⚪ Tunnel Inactive");
    }

    private static async Task<string?> EnsureBinaryAsync(CancellationToken ct)
    {
        // 1. Check local App Directory / bin
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var localBin = Path.Combine(baseDir, "cloudflared.exe");
        if (File.Exists(localBin)) return localBin;

        var subBin = Path.Combine(baseDir, "bin", "cloudflared.exe");
        if (File.Exists(subBin)) return subBin;

        // 2. Check LocalAppData
        var appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteDesk", "bin");
        Directory.CreateDirectory(appDataDir);
        var appDataBin = Path.Combine(appDataDir, "cloudflared.exe");
        if (File.Exists(appDataBin)) return appDataBin;

        // 3. Check System PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, "cloudflared.exe");
            if (File.Exists(candidate)) return candidate;
        }

        // 4. Download standalone binary
        try
        {
            Logger.Information("Downloading cloudflared.exe to {Destination}...", appDataBin);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
            var bytes = await http.GetByteArrayAsync("https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe", ct).ConfigureAwait(false);
            await File.WriteAllBytesAsync(appDataBin, bytes, ct).ConfigureAwait(false);
            Logger.Information("Downloaded cloudflared.exe successfully ({Size} bytes).", bytes.Length);
            return appDataBin;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to download cloudflared binary.");
            return null;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        StopTunnel();
    }
}
