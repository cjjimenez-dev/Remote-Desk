using RemoteDesk.Core.Interfaces;
using RemoteDesk.Core.Protocol;
using Serilog;

namespace RemoteDesk.Core.Services;

public sealed class ChatService : IDisposable
{
    private readonly ITransport _transport;
    private readonly string _localSenderId;
    private readonly string _localSenderName;
    private readonly ILogger _logger = Log.ForContext<ChatService>();
    private bool _disposed;

    public event Action<SessionChatMessage>? MessageReceived;

    public ChatService(ITransport transport, string localSenderId, string localSenderName)
    {
        _transport = transport;
        _localSenderId = localSenderId;
        _localSenderName = localSenderName;

        _transport.MessageReceived += OnTransportMessageReceived;
    }

    public async Task<SessionChatMessage> SendMessageAsync(string text, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Chat message text cannot be empty.", nameof(text));
        }

        var message = new SessionChatMessage
        {
            MessageId = Guid.NewGuid().ToString("N"),
            SenderId = _localSenderId,
            SenderName = _localSenderName,
            Text = text,
            TimestampUtc = DateTime.UtcNow
        };

        byte[] payload = ProtocolSerializer.Serialize(message);
        var envelope = new ProtocolEnvelope(DataChannels.Chat, MessageType.SessionChat, payload);
        byte[] packet = envelope.ToBytes();

        await _transport.SendMessageAsync(DataChannels.Chat, packet, reliable: true, ct).ConfigureAwait(false);
        _logger.Debug("Sent chat message {MessageId} from {Sender}: {Text}", message.MessageId, _localSenderName, text);

        return message;
    }

    private void OnTransportMessageReceived(byte channel, ReadOnlyMemory<byte> rawData)
    {
        if (channel != DataChannels.Chat) return;

        if (ProtocolEnvelope.TryDecode(rawData.Span, out var envelope, out _) &&
            envelope.MessageType == MessageType.SessionChat)
        {
            try
            {
                var msg = ProtocolSerializer.Deserialize<SessionChatMessage>(envelope.Payload);
                _logger.Debug("Received chat message from {Sender}: {Text}", msg.SenderName, msg.Text);
                MessageReceived?.Invoke(msg);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to deserialize incoming chat message.");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _transport.MessageReceived -= OnTransportMessageReceived;
    }
}
