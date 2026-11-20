using RemoteDesk.Core.Models;

namespace RemoteDesk.Core.Interfaces;

public interface ITransport : IAsyncDisposable
{
    TransportState State { get; }

    event Action<TransportState>? StateChanged;
    event Action<byte, ReadOnlyMemory<byte>>? MessageReceived;
    event Action<ReadOnlyMemory<byte>>? VideoPacketReceived;
    event Action<string>? ErrorOccurred;

    ValueTask SendMessageAsync(byte channelId, ReadOnlyMemory<byte> payload, bool reliable = true, CancellationToken ct = default);
    ValueTask SendVideoPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken ct = default);
    Task CloseAsync();
}
