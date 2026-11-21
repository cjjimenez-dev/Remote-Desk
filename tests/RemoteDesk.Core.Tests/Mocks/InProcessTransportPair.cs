using System.Threading.Channels;
using RemoteDesk.Core.Interfaces;
using RemoteDesk.Core.Models;

namespace RemoteDesk.Core.Tests.Mocks;

public sealed class InProcessTransportPair : ITransport
{
    private readonly Channel<(byte ChannelId, byte[] Data)> _channel;
    private readonly CancellationTokenSource _cts = new();

    public InProcessTransportPair? Peer { get; set; }
    public TransportState State => TransportState.Connected;
    public ConnectionPath ConnectionPath => ConnectionPath.DirectLocal;

    public event Action<TransportState>? StateChanged;
    public event Action<ConnectionPath>? ConnectionPathChanged;
    public event Action<byte, ReadOnlyMemory<byte>>? MessageReceived;
    public event Action<ReadOnlyMemory<byte>>? VideoPacketReceived;
    public event Action<string>? ErrorOccurred;

    public InProcessTransportPair()
    {
        _channel = Channel.CreateUnbounded<(byte, byte[])>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = true
        });

        _ = Task.Run(ProcessQueueAsync);
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            var reader = _channel.Reader;
            while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var item))
                {
                    MessageReceived?.Invoke(item.ChannelId, item.Data);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public static (InProcessTransportPair, InProcessTransportPair) CreateConnectedPair()
    {
        var a = new InProcessTransportPair();
        var b = new InProcessTransportPair();
        a.Peer = b;
        b.Peer = a;
        return (a, b);
    }

    public ValueTask SendMessageAsync(byte channelId, ReadOnlyMemory<byte> payload, bool reliable = true, CancellationToken ct = default)
    {
        if (Peer != null)
        {
            Peer._channel.Writer.TryWrite((channelId, payload.ToArray()));
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask SendVideoPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken ct = default) => ValueTask.CompletedTask;
    public Task CloseAsync() => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}
