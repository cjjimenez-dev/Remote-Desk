using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RemoteDesk.Core.Interfaces;
using RemoteDesk.Core.Protocol;
using Serilog;

namespace RemoteDesk.Platform.Windows.Terminal;

public sealed class WindowsTerminalSession : ITerminalSession
{
    private readonly ILogger _logger = Log.ForContext<WindowsTerminalSession>();
    private readonly CancellationTokenSource _cts = new();
    private PseudoConsole? _pty;
    private Process? _fallbackProcess;
    private Stream? _inputStream;
    private Stream? _outputStream;
    private Task? _readTask;
    private Task? _exitMonitorTask;
    private bool _disposed;

    public string SessionId { get; }
    public bool IsRunning { get; private set; }

    public event Action<string>? OutputReceived;
    public event Action<int>? Exited;

    public WindowsTerminalSession(string sessionId)
    {
        SessionId = sessionId;
    }

    public static WindowsTerminalSession Create(string sessionId, ShellType shellType = ShellType.PowerShell, int columns = 80, int rows = 25)
    {
        var session = new WindowsTerminalSession(sessionId);
        session.Start(shellType, columns, rows);
        return session;
    }

    public void Start(ShellType shellType, int columns, int rows)
    {
        string commandLine = shellType switch
        {
            ShellType.Cmd => "cmd.exe",
            _ => "powershell.exe -NoLogo"
        };

        try
        {
            _logger.Information("Starting ConPTY terminal session {SessionId} with shell '{CommandLine}'", SessionId, commandLine);
            _pty = PseudoConsole.Create(commandLine, columns, rows);
            _inputStream = new FileStream(_pty.InputPipeWrite, FileAccess.Write, 4096, isAsync: true);
            _outputStream = new FileStream(_pty.OutputPipeRead, FileAccess.Read, 4096, isAsync: true);
            IsRunning = true;

            _readTask = Task.Run(() => ReadOutputLoopAsync(_outputStream, _cts.Token));
            _exitMonitorTask = Task.Run(() => MonitorProcessExitAsync(_pty.ProcessHandle, _pty.ProcessId, _cts.Token));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to start ConPTY. Falling back to redirected standard process.");
            StartFallback(commandLine);
        }
    }

    private void StartFallback(string commandLine)
    {
        var psi = new ProcessStartInfo
        {
            FileName = commandLine.StartsWith("cmd.exe") ? "cmd.exe" : "powershell.exe",
            Arguments = commandLine.StartsWith("cmd.exe") ? "" : "-NoLogo",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        _fallbackProcess = Process.Start(psi) ?? throw new InvalidOperationException("Failed to launch fallback shell process.");
        _inputStream = _fallbackProcess.StandardInput.BaseStream;
        IsRunning = true;

        _readTask = Task.Run(async () =>
        {
            await Task.WhenAll(
                ReadOutputLoopAsync(_fallbackProcess.StandardOutput.BaseStream, _cts.Token),
                ReadOutputLoopAsync(_fallbackProcess.StandardError.BaseStream, _cts.Token)
            );
        });

        _exitMonitorTask = Task.Run(async () =>
        {
            try
            {
                await _fallbackProcess.WaitForExitAsync(_cts.Token);
                IsRunning = false;
                Exited?.Invoke(_fallbackProcess.ExitCode);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Fallback process monitor error.");
            }
        });
    }

    private async Task ReadOutputLoopAsync(Stream stream, CancellationToken ct)
    {
        byte[] buffer = new byte[4096];
        var decoder = Encoding.UTF8.GetDecoder();
        char[] charBuffer = new char[4096];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                if (bytesRead <= 0) break;

                int charsRead = decoder.GetChars(buffer, 0, bytesRead, charBuffer, 0, flush: false);
                if (charsRead > 0)
                {
                    string text = new string(charBuffer, 0, charsRead);
                    OutputReceived?.Invoke(text);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_disposed)
                _logger.Debug(ex, "Output read loop ended for session {SessionId}", SessionId);
        }
    }

    private async Task MonitorProcessExitAsync(SafeProcessHandle processHandle, int processId, CancellationToken ct)
    {
        try
        {
            Process? proc = null;
            try
            {
                proc = Process.GetProcessById(processId);
            }
            catch (ArgumentException)
            {
                IsRunning = false;
                Exited?.Invoke(0);
                return;
            }

            using (proc)
            {
                await proc.WaitForExitAsync(ct).ConfigureAwait(false);
                IsRunning = false;
                Exited?.Invoke(proc.ExitCode);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.Debug(ex, "ConPTY process exit monitor completed for {SessionId}", SessionId);
        }
    }

    public async ValueTask WriteInputAsync(string input, CancellationToken ct = default)
    {
        if (!IsRunning || _inputStream == null) return;

        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(input);
            await _inputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await _inputStream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to write input to terminal session {SessionId}", SessionId);
        }
    }

    public void Resize(int columns, int rows)
    {
        _pty?.Resize(columns, rows);
    }

    public async Task TerminateAsync()
    {
        await DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        IsRunning = false;

        _cts.Cancel();

        try
        {
            if (_pty != null)
            {
                try
                {
                    using var proc = Process.GetProcessById(_pty.ProcessId);
                    if (!proc.HasExited)
                    {
                        proc.Kill(entireProcessTree: true);
                    }
                }
                catch { }

                _pty.Dispose();
            }

            if (_fallbackProcess != null)
            {
                try
                {
                    if (!_fallbackProcess.HasExited)
                    {
                        _fallbackProcess.Kill(entireProcessTree: true);
                    }
                }
                catch { }
                _fallbackProcess.Dispose();
            }

            if (_readTask != null)
            {
                await Task.WhenAny(_readTask, Task.Delay(500)).ConfigureAwait(false);
            }
        }
        finally
        {
            _cts.Dispose();
        }
    }
}
