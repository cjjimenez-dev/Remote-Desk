using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using MessagePack;
using Serilog;

namespace RemoteDesk.Platform.Windows.Ipc;

public sealed class NamedPipeIpcServer : IAsyncDisposable
{
    private static readonly ILogger Logger = Log.ForContext<NamedPipeIpcServer>();

    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenTask;
    private NamedPipeServerStream? _currentStream;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public event Action? ClientConnected;
    public event Action? ClientDisconnected;
    public event Action<IpcMessage>? MessageReceived;

    public bool IsClientConnected => _currentStream?.IsConnected == true;

    public NamedPipeIpcServer(string pipeName = "RemoteDesk_Service_Agent_Ipc")
    {
        _pipeName = pipeName;
    }

    public void Start()
    {
        if (_listenTask != null) return;
        _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));
        Logger.Information("NamedPipe IPC Server started on pipe [{PipeName}]", _pipeName);
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var pipeSecurity = CreatePipeSecurity();
                var serverStream = NamedPipeServerStreamAcl.Create(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 65536,
                    outBufferSize: 65536,
                    pipeSecurity);

                _currentStream = serverStream;
                Logger.Debug("Waiting for Agent client connection on pipe [{PipeName}]...", _pipeName);

                await serverStream.WaitForConnectionAsync(ct);
                Logger.Information("Agent client connected to NamedPipe IPC Server [{PipeName}]", _pipeName);
                ClientConnected?.Invoke();

                await ReadLoopAsync(serverStream, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "NamedPipe server error or client disconnect.");
            }
            finally
            {
                if (_currentStream != null)
                {
                    try { _currentStream.Dispose(); } catch { }
                    _currentStream = null;
                }
                ClientDisconnected?.Invoke();
            }

            // Brief pause before accepting next connection
            await Task.Delay(500, ct);
        }
    }

    private async Task ReadLoopAsync(NamedPipeServerStream stream, CancellationToken ct)
    {
        var lengthBuffer = new byte[4];

        while (!ct.IsCancellationRequested && stream.IsConnected)
        {
            var bytesRead = await stream.ReadAtLeastAsync(lengthBuffer, 4, throwOnEndOfStream: false, ct);
            if (bytesRead < 4)
            {
                Logger.Information("Agent client closed named pipe connection.");
                break;
            }

            var messageLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
            if (messageLength <= 0 || messageLength > 10 * 1024 * 1024)
            {
                Logger.Warning("Invalid IPC message length: {Length} bytes. Dropping connection.", messageLength);
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
    }

    public async Task SendMessageAsync(IpcMessage message, CancellationToken ct = default)
    {
        var stream = _currentStream;
        if (stream == null || !stream.IsConnected)
        {
            Logger.Warning("Cannot send IPC message: client is not connected.");
            return;
        }

        var serialized = MessagePackSerializer.Serialize(message);
        var lengthBuffer = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lengthBuffer, serialized.Length);

        await _sendLock.WaitAsync(ct);
        try
        {
            await stream.WriteAsync(lengthBuffer, ct);
            await stream.WriteAsync(serialized, ct);
            await stream.FlushAsync(ct);
        }
        finally
        {
            try { _sendLock.Release(); } catch (ObjectDisposedException) { }
        }
    }

    private static PipeSecurity CreatePipeSecurity()
    {
        var ps = new PipeSecurity();

        // SYSTEM: FullControl
        ps.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        // Built-in Administrators: FullControl
        ps.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        // Authenticated Users: ReadWrite
        ps.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        return ps;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        if (_currentStream != null)
        {
            try { _currentStream.Dispose(); } catch { }
            _currentStream = null;
        }

        if (_listenTask != null)
        {
            try { await _listenTask; } catch { }
            _listenTask = null;
        }

        _sendLock.Dispose();
        _cts.Dispose();
    }
}
