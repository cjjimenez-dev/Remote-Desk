using System.Security.Cryptography;
using FluentAssertions;
using RemoteDesk.Core.Interfaces;
using RemoteDesk.Core.Models;
using RemoteDesk.Core.Services;
using Xunit;

namespace RemoteDesk.Core.Tests.Services;

public class FileTransferTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _downloadDirectory;

    public FileTransferTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "RemoteDeskTests_Source_" + Guid.NewGuid().ToString("N"));
        _downloadDirectory = Path.Combine(Path.GetTempPath(), "RemoteDeskTests_Dest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        Directory.CreateDirectory(_downloadDirectory);
    }

    [Fact]
    public async Task SendFileAsync_TransfersFileEndToEnd_MatchesChecksum()
    {
        // Arrange
        var (senderTransport, receiverTransport) = InProcessTransportPair.CreatePair();
        using var senderService = new FileTransferService(senderTransport);
        using var receiverService = new FileTransferService(receiverTransport, _downloadDirectory);

        // Generate 128 KB random test file
        string testFilePath = Path.Combine(_tempDirectory, "test_document.dat");
        byte[] originalBytes = new byte[128 * 1024];
        Random.Shared.NextBytes(originalBytes);
        await File.WriteAllBytesAsync(testFilePath, originalBytes);

        var completionTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiverService.ProgressUpdated += progress =>
        {
            if (progress.IsCompleted)
            {
                completionTcs.TrySetResult(true);
            }
            else if (!string.IsNullOrEmpty(progress.ErrorMessage))
            {
                completionTcs.TrySetException(new Exception(progress.ErrorMessage));
            }
        };

        // Act
        string transferId = await senderService.SendFileAsync(testFilePath);

        // Assert
        var completed = await completionTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        completed.Should().BeTrue();

        string receivedPath = Path.Combine(_downloadDirectory, "test_document.dat");
        File.Exists(receivedPath).Should().BeTrue();

        byte[] receivedBytes = await File.ReadAllBytesAsync(receivedPath);
        receivedBytes.Should().Equal(originalBytes);

        string originalHash = Convert.ToHexString(SHA256.HashData(originalBytes));
        string receivedHash = Convert.ToHexString(SHA256.HashData(receivedBytes));
        receivedHash.Should().Be(originalHash);
    }

    [Fact]
    public async Task SendFileAsync_WithExistingPartFile_ResumesTransfer()
    {
        // Arrange
        var (senderTransport, receiverTransport) = InProcessTransportPair.CreatePair();
        using var senderService = new FileTransferService(senderTransport);
        using var receiverService = new FileTransferService(receiverTransport, _downloadDirectory);

        // Create 64 KB test file
        string testFilePath = Path.Combine(_tempDirectory, "resumable.bin");
        byte[] fullBytes = new byte[64 * 1024];
        Random.Shared.NextBytes(fullBytes);
        await File.WriteAllBytesAsync(testFilePath, fullBytes);

        // Pre-create .part file with first 32 KB
        string partFilePath = Path.Combine(_downloadDirectory, "resumable.bin.part");
        byte[] existingPartBytes = fullBytes[..(32 * 1024)];
        await File.WriteAllBytesAsync(partFilePath, existingPartBytes);

        var completionTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiverService.ProgressUpdated += progress =>
        {
            if (progress.IsCompleted)
            {
                completionTcs.TrySetResult(true);
            }
        };

        // Act
        await senderService.SendFileAsync(testFilePath);
        var completed = await completionTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        completed.Should().BeTrue();
        string receivedPath = Path.Combine(_downloadDirectory, "resumable.bin");
        File.Exists(receivedPath).Should().BeTrue();

        byte[] receivedBytes = await File.ReadAllBytesAsync(receivedPath);
        receivedBytes.Should().Equal(fullBytes);
    }

    [Fact]
    public async Task CancelTransferAsync_CancelsOngoingTransfer()
    {
        var (senderTransport, receiverTransport) = InProcessTransportPair.CreatePair();
        using var senderService = new FileTransferService(senderTransport);
        using var receiverService = new FileTransferService(receiverTransport, _downloadDirectory);

        string testFilePath = Path.Combine(_tempDirectory, "cancel_test.bin");
        byte[] bytes = new byte[10 * 1024 * 1024]; // 10 MB file so it doesn't instantly finish
        await File.WriteAllBytesAsync(testFilePath, bytes);

        var cancelTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        senderService.ProgressUpdated += progress =>
        {
            if (!string.IsNullOrEmpty(progress.ErrorMessage))
            {
                cancelTcs.TrySetResult(progress.ErrorMessage);
            }
        };

        string transferId = await senderService.SendFileAsync(testFilePath);
        await senderService.CancelTransferAsync(transferId);

        var reason = await cancelTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        reason.Should().NotBeNull();
        reason.Should().Contain("Cancelled");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDirectory, true); } catch { }
        try { Directory.Delete(_downloadDirectory, true); } catch { }
    }

    private sealed class InProcessTransportPair : ITransport
    {
        private readonly System.Threading.Channels.Channel<(byte Channel, byte[] Data)> _queue =
            System.Threading.Channels.Channel.CreateUnbounded<(byte, byte[])>(new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });
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
            _ = Task.Run(ProcessQueueAsync);
        }

        private async Task ProcessQueueAsync()
        {
            try
            {
                while (await _queue.Reader.WaitToReadAsync(_cts.Token))
                {
                    while (_queue.Reader.TryRead(out var item))
                    {
                        MessageReceived?.Invoke(item.Channel, item.Data);
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

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
                Peer._queue.Writer.TryWrite((channelId, payload.ToArray()));
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask SendVideoPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken ct = default) => ValueTask.CompletedTask;
        public Task CloseAsync()
        {
            _cts.Cancel();
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            _cts.Cancel();
            return ValueTask.CompletedTask;
        }
    }
}
