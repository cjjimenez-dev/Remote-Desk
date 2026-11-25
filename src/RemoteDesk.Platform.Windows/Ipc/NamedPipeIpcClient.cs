using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using MessagePack;
using Serilog;

namespace RemoteDesk.Platform.Windows.Ipc;

public sealed class NamedPipeIpcClient : IAsyncDisposable
{
    private static readonly ILogger Logger = Log.ForContext<NamedPipeIpcClient>();

    private readonly string _pipeName;
    private readonly string _serverName;
    private NamedPipeClientStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _readTask;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public event Action? Connected;
    public event Action? Disconnected;
    public event Action<IpcMessage>? MessageReceived;

    public bool IsConnected => _stream?.IsConnected == true;

    public NamedPipeIpcClient(string pipeName = "RemoteDesk_Service_Agent_Ipc", string serverName = ".")
    {
        _pipeName = pipeName;
        _serverName = serverName;
    }

    public async Task<bool> ConnectAsync(int timeoutMs = 5000, CancellationToken ct = default)
    {
        if (IsConnected) return true;

        try
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            var clientStream = new NamedPipeClientStream(
                _serverName,
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            Logger.Debug("Connecting to NamedPipe IPC Server [{PipeName}]...", _pipeName);
            await clientStream.ConnectAsync(timeoutMs, ct);

            _stream = clientStream;
            Logger.Information("Successfully connected to NamedPipe IPC Server [{PipeName}]", _pipeName);

            Connected?.Invoke();
            _readTask = Task.Run(() => ReadLoopAsync(_stream, _cts.Token));
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to connect to NamedPipe IPC Server [{PipeName}] within {Timeout}ms.", _pipeName, timeoutMs);
            return false;
        }
    }

    private async Task ReadLoopAsync(NamedPipeClientStream stream, CancellationToken ct)
    {
        var lengthBuffer = new byte[4];

        while (!ct.IsCancellationRequested && stream.IsConnected)
        {
            try
            {
                var bytesRead = await stream.ReadAtLeastAsync(lengthBuffer, 4, throwOnEndOfStream: false, ct);
                if (bytesRead < 4)
                {
                    Logger.Information("NamedPipe server closed connection.");
                    break;
                }

                var messageLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
                if (messageLength <= 0 || messageLength > 10 * 1024 * 1024)
                {
                    Logger.Warning("Invalid IPC message length: {Length} bytes.", messageLength);
                    break;
                }

                var payloadBuffer = new byte[messageLength];
                await stream.ReadExactlyAsync(payloadBuffer, ct);

                try
                {
                    var msg = MessagePackSerializer.Deserialize<IpcMessage>(payloadBuffer);
                    MessageReceived?.Invoke(msg);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Failed to deserialize IPC message of {Length} bytes.", messageLength);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Error reading from NamedPipe IPC Server.");
                break;
            }
        }

        Disconnected?.Invoke();
    }

    public async Task SendMessageAsync(IpcMessage message, CancellationToken ct = default)
    {
        var stream = _stream;
        if (stream == null || !stream.IsConnected)
        {
            Logger.Warning("Cannot send IPC message: client is not connected to NamedPipe server.");
            return;
        }

        var serialized = MessagePackSerializer.Serialize(message);
        var lengthBuffer = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lengthBuffer, serialized.Length);

        try
        {
            await _sendLock.WaitAsync(ct);
            try
            {
                if (!stream.IsConnected) return;
                await stream.WriteAsync(lengthBuffer, ct);
                await stream.WriteAsync(serialized, ct);
                await stream.FlushAsync(ct);
            }
            finally
            {
                try { _sendLock.Release(); } catch (ObjectDisposedException) { }
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException or OperationCanceledException)
        {
            Logger.Debug(ex, "Named pipe client stream closed while sending message");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();

        if (_stream != null)
        {
            try { _stream.Dispose(); } catch { }
            _stream = null;
        }

        if (_readTask != null)
        {
            try { await _readTask; } catch { }
            _readTask = null;
        }

        _sendLock.Dispose();
        _cts?.Dispose();
    }
}
