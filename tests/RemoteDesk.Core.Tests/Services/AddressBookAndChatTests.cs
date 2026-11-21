using FluentAssertions;
using RemoteDesk.Core.Interfaces;
using RemoteDesk.Core.Models;
using RemoteDesk.Core.Protocol;
using RemoteDesk.Core.Services;
using Xunit;

namespace RemoteDesk.Core.Tests.Services;

public class AddressBookAndChatTests : IDisposable
{
    private readonly string _testAddressBookPath;

    public AddressBookAndChatTests()
    {
        _testAddressBookPath = Path.Combine(Path.GetTempPath(), $"RemoteDesk_AddrBook_{Guid.NewGuid():N}.json");
    }

    [Fact]
    public async Task AddressBook_UpsertRetrieveAndDelete_WorksCorrectly()
    {
        var service = new AddressBookService(_testAddressBookPath);

        // 1. Initially empty
        var initial = await service.GetAllEntriesAsync();
        initial.Should().BeEmpty();

        // 2. Add entry
        var entry1 = new AddressBookEntry
        {
            DeviceId = "111-222",
            Alias = "Office Workstation",
            Notes = "Windows 11 Pro desktop",
            IsFavorite = false
        };
        await service.UpsertEntryAsync(entry1);

        var list = await service.GetAllEntriesAsync();
        list.Should().HaveCount(1);
        list[0].DeviceId.Should().Be("111-222");
        list[0].Alias.Should().Be("Office Workstation");

        // 3. Toggle favorite
        bool fav = await service.ToggleFavoriteAsync("111-222");
        fav.Should().BeTrue();

        var updatedList = await service.GetAllEntriesAsync();
        updatedList[0].IsFavorite.Should().BeTrue();

        // 4. Record connection
        await service.RecordConnectionAsync("111-222");
        var reloaded = await service.GetAllEntriesAsync();
        reloaded[0].LastConnectedUtc.Should().NotBeNull();

        // 5. Delete entry
        bool deleted = await service.DeleteEntryAsync("111-222");
        deleted.Should().BeTrue();

        var emptyList = await service.GetAllEntriesAsync();
        emptyList.Should().BeEmpty();
    }

    [Fact]
    public async Task ChatService_SendsAndReceivesMessagesEndToEnd()
    {
        var (t1, t2) = InProcessTransportPair.CreatePair();
        using var hostChat = new ChatService(t1, "host-1", "Host Machine");
        using var viewerChat = new ChatService(t2, "viewer-1", "Remote Operator");

        var receivedTcs = new TaskCompletionSource<SessionChatMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        hostChat.MessageReceived += msg =>
        {
            receivedTcs.TrySetResult(msg);
        };

        // Act
        var sentMsg = await viewerChat.SendMessageAsync("Hello host, need any assistance?");

        // Assert
        var received = await receivedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        received.Should().NotBeNull();
        received.MessageId.Should().Be(sentMsg.MessageId);
        received.SenderName.Should().Be("Remote Operator");
        received.Text.Should().Be("Hello host, need any assistance?");
    }

    public void Dispose()
    {
        try { if (File.Exists(_testAddressBookPath)) File.Delete(_testAddressBookPath); } catch { }
    }

    private sealed class InProcessTransportPair : ITransport
    {
        public InProcessTransportPair? Peer { get; set; }
        public TransportState State => TransportState.Connected;
        public ConnectionPath ConnectionPath => ConnectionPath.DirectLocal;

        public event Action<TransportState>? StateChanged;
        public event Action<ConnectionPath>? ConnectionPathChanged;
        public event Action<byte, ReadOnlyMemory<byte>>? MessageReceived;
        public event Action<ReadOnlyMemory<byte>>? VideoPacketReceived;
        public event Action<string>? ErrorOccurred;

        public static (InProcessTransportPair, InProcessTransportPair) CreatePair()
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
                var copy = payload.ToArray();
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    Peer.MessageReceived?.Invoke(channelId, copy);
                });
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask SendVideoPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken ct = default) => ValueTask.CompletedTask;
        public Task CloseAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
