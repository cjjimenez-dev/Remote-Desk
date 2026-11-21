using RemoteDesk.Core.Interfaces;
using RemoteDesk.Core.Protocol;
using Serilog;

namespace RemoteDesk.Core.Services;

public sealed class TerminalClientService : IDisposable
{
    private readonly ITransport _transport;
    private readonly ILogger _logger = Log.ForContext<TerminalClientService>();
    private bool _disposed;
    private string? _activeSessionId;

    public string? ActiveSessionId => _activeSessionId;
    public bool IsActive => !string.IsNullOrEmpty(_activeSessionId);

    public event Action<string>? OutputReceived;
    public event Action<string>? Closed;

    public TerminalClientService(ITransport transport)
    {
        _transport = transport;
        _transport.MessageReceived += OnTransportMessageReceived;
    }

    public async Task<string> StartTerminalAsync(ShellType shell = ShellType.PowerShell, int columns = 80, int rows = 25, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        string sessionId = Guid.NewGuid().ToString("N");
        _activeSessionId = sessionId;

        var startMsg = new TerminalStartMessage
        {
            SessionId = sessionId,
            Shell = shell,
            Columns = columns,
            Rows = rows
        };

        byte[] payload = ProtocolSerializer.Serialize(startMsg);
        var envelope = new ProtocolEnvelope(DataChannels.Terminal, MessageType.TerminalStart, payload);
        await _transport.SendMessageAsync(DataChannels.Terminal, envelope.ToBytes(), reliable: true, ct).ConfigureAwait(false);

        _logger.Information("Sent TerminalStart request: Shell={Shell}, Session={SessionId}", shell, sessionId);
        return sessionId;
    }

    public async ValueTask SendInputAsync(string inputData, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrEmpty(_activeSessionId) || string.IsNullOrEmpty(inputData)) return;

        var inputMsg = new TerminalInputMessage
        {
            SessionId = _activeSessionId,
            InputData = inputData
        };

        byte[] payload = ProtocolSerializer.Serialize(inputMsg);
        var envelope = new ProtocolEnvelope(DataChannels.Terminal, MessageType.TerminalInput, payload);
        await _transport.SendMessageAsync(DataChannels.Terminal, envelope.ToBytes(), reliable: true, ct).ConfigureAwait(false);
    }

    public async ValueTask ResizeAsync(int columns, int rows, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrEmpty(_activeSessionId)) return;

        var resizeMsg = new TerminalResizeMessage
        {
            SessionId = _activeSessionId,
            Columns = columns,
            Rows = rows
        };

        byte[] payload = ProtocolSerializer.Serialize(resizeMsg);
        var envelope = new ProtocolEnvelope(DataChannels.Terminal, MessageType.TerminalResize, payload);
        await _transport.SendMessageAsync(DataChannels.Terminal, envelope.ToBytes(), reliable: true, ct).ConfigureAwait(false);
    }

    public async Task CloseTerminalAsync(string reason = "User closed terminal", CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_activeSessionId)) return;

        var closeMsg = new TerminalCloseMessage
        {
            SessionId = _activeSessionId,
            Reason = reason
        };

        byte[] payload = ProtocolSerializer.Serialize(closeMsg);
        var envelope = new ProtocolEnvelope(DataChannels.Terminal, MessageType.TerminalClose, payload);
        await _transport.SendMessageAsync(DataChannels.Terminal, envelope.ToBytes(), reliable: true, ct).ConfigureAwait(false);

        _activeSessionId = null;
        Closed?.Invoke(reason);
    }

    private void OnTransportMessageReceived(byte channel, ReadOnlyMemory<byte> rawData)
    {
        if (channel != DataChannels.Terminal) return;

        if (!ProtocolEnvelope.TryDecode(rawData.Span, out var envelope, out _)) return;

        switch (envelope.MessageType)
        {
            case MessageType.TerminalOutput:
                try
                {
                    var outMsg = ProtocolSerializer.Deserialize<TerminalOutputMessage>(envelope.Payload);
                    OutputReceived?.Invoke(outMsg.OutputData);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to deserialize terminal output message.");
                }
                break;

            case MessageType.TerminalClose:
                try
                {
                    var closeMsg = ProtocolSerializer.Deserialize<TerminalCloseMessage>(envelope.Payload);
                    _activeSessionId = null;
                    Closed?.Invoke(closeMsg.Reason);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to deserialize terminal close message.");
                }
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _transport.MessageReceived -= OnTransportMessageReceived;
    }
}
