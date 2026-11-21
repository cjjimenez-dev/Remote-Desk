using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using RemoteDesk.Core.Interfaces;
using RemoteDesk.Core.Protocol;
using Serilog;

namespace RemoteDesk.Core.Services;

public sealed class FileTransferService : IFileTransferService
{
    public const int DefaultChunkSize = 32 * 1024; // 32 KB chunks
    public const int MaxInFlightBytes = 256 * 1024; // 256 KB flow-control window

    private readonly ITransport _transport;
    private readonly string _downloadDirectory;
    private readonly ILogger _logger = Log.ForContext<FileTransferService>();

    private readonly ConcurrentDictionary<string, OutgoingTransfer> _outgoingTransfers = new();
    private readonly ConcurrentDictionary<string, IncomingTransfer> _incomingTransfers = new();
    private bool _disposed;

    public event Action<FileTransferProgress>? ProgressUpdated;

    public FileTransferService(ITransport transport, string? downloadDirectory = null)
    {
        _transport = transport;
        _downloadDirectory = string.IsNullOrWhiteSpace(downloadDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "RemoteDesk")
            : downloadDirectory;

        if (!Directory.Exists(_downloadDirectory))
        {
            try
            {
                Directory.CreateDirectory(_downloadDirectory);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to create download directory {Path}", _downloadDirectory);
            }
        }

        _transport.MessageReceived += OnTransportDataReceived;
    }

    public async Task<string> SendFileAsync(string localFilePath, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!File.Exists(localFilePath))
        {
            throw new FileNotFoundException("File to transfer was not found.", localFilePath);
        }

        var fileInfo = new FileInfo(localFilePath);
        string transferId = Guid.NewGuid().ToString("N");
        string fileName = fileInfo.Name;
        long totalBytes = fileInfo.Length;

        _logger.Information("Computing SHA256 checksum for {FileName} ({Bytes} bytes)...", fileName, totalBytes);
        string checksum = await ComputeFileHashAsync(localFilePath, ct).ConfigureAwait(false);

        var outgoing = new OutgoingTransfer(transferId, localFilePath, fileName, totalBytes, checksum, ct);
        _outgoingTransfers[transferId] = outgoing;

        // 1. Send FileTransferRequest
        var requestMsg = new FileTransferRequestMessage
        {
            TransferId = transferId,
            FileName = fileName,
            FileSizeBytes = totalBytes,
            Sha256Checksum = checksum
        };

        await SendProtocolMessageAsync(MessageType.FileTransferRequest, requestMsg).ConfigureAwait(false);

        // 2. Start sender background worker
        _ = Task.Run(() => ProcessOutgoingTransferAsync(outgoing), ct);

        return transferId;
    }

    public async Task CancelTransferAsync(string transferId)
    {
        if (_outgoingTransfers.TryRemove(transferId, out var outgoing))
        {
            outgoing.Cts.Cancel();
            outgoing.Dispose();

            var cancelMsg = new FileTransferCancelMessage
            {
                TransferId = transferId,
                Reason = "Cancelled by user"
            };
            await SendProtocolMessageAsync(MessageType.FileTransferCancel, cancelMsg).ConfigureAwait(false);

            ProgressUpdated?.Invoke(new FileTransferProgress(
                transferId, outgoing.FileName, outgoing.SentBytes, outgoing.TotalBytes, 0, false, "Cancelled by user"));
        }
        else if (_incomingTransfers.TryRemove(transferId, out var incoming))
        {
            incoming.Dispose();
            var cancelMsg = new FileTransferCancelMessage
            {
                TransferId = transferId,
                Reason = "Cancelled by receiver"
            };
            await SendProtocolMessageAsync(MessageType.FileTransferCancel, cancelMsg).ConfigureAwait(false);

            ProgressUpdated?.Invoke(new FileTransferProgress(
                transferId, incoming.FileName, incoming.ReceivedBytes, incoming.TotalBytes, 0, false, "Cancelled by receiver"));
        }
    }

    private async Task ProcessOutgoingTransferAsync(OutgoingTransfer transfer)
    {
        try
        {
            // Wait for acceptance response from receiver
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(transfer.Cts.Token);
            linkedCts.CancelAfter(TimeSpan.FromSeconds(30));

            var accepted = await transfer.AcceptedTcs.Task.WaitAsync(linkedCts.Token).ConfigureAwait(false);
            if (!accepted)
            {
                ProgressUpdated?.Invoke(new FileTransferProgress(
                    transfer.TransferId, transfer.FileName, 0, transfer.TotalBytes, 0, false, transfer.RejectionReason ?? "Transfer rejected"));
                _outgoingTransfers.TryRemove(transfer.TransferId, out _);
                transfer.Dispose();
                return;
            }

            long currentOffset = transfer.ResumeOffset;
            transfer.SentBytes = currentOffset;

            await using var stream = new FileStream(transfer.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
            if (currentOffset > 0)
            {
                stream.Seek(currentOffset, SeekOrigin.Begin);
            }

            byte[] buffer = new byte[DefaultChunkSize];
            var stopwatch = Stopwatch.StartNew();
            long bytesSinceSpeedCheck = 0;
            double currentSpeed = 0;

            while (currentOffset < transfer.TotalBytes && !transfer.Cts.IsCancellationRequested)
            {
                // Flow control: wait if in-flight bytes exceed window
                while ((currentOffset - transfer.AcknowledgedOffset) > MaxInFlightBytes && !transfer.Cts.IsCancellationRequested)
                {
                    await Task.Delay(10, transfer.Cts.Token).ConfigureAwait(false);
                }

                int bytesToRead = (int)Math.Min(buffer.Length, transfer.TotalBytes - currentOffset);
                int bytesRead = await stream.ReadAsync(buffer.AsMemory(0, bytesToRead), transfer.Cts.Token).ConfigureAwait(false);
                if (bytesRead == 0) break;

                bool isLastChunk = (currentOffset + bytesRead) >= transfer.TotalBytes;

                var chunkData = new byte[bytesRead];
                Buffer.BlockCopy(buffer, 0, chunkData, 0, bytesRead);

                var chunkMsg = new FileChunkMessage
                {
                    TransferId = transfer.TransferId,
                    OffsetBytes = currentOffset,
                    Data = chunkData,
                    IsLastChunk = isLastChunk
                };

                await SendProtocolMessageAsync(MessageType.FileChunk, chunkMsg).ConfigureAwait(false);

                currentOffset += bytesRead;
                transfer.SentBytes = currentOffset;
                bytesSinceSpeedCheck += bytesRead;

                if (stopwatch.ElapsedMilliseconds >= 500)
                {
                    currentSpeed = (bytesSinceSpeedCheck * 1000.0) / stopwatch.ElapsedMilliseconds;
                    stopwatch.Restart();
                    bytesSinceSpeedCheck = 0;

                    ProgressUpdated?.Invoke(new FileTransferProgress(
                        transfer.TransferId, transfer.FileName, currentOffset, transfer.TotalBytes, currentSpeed, isLastChunk));
                }
            }

            if (!transfer.Cts.IsCancellationRequested)
            {
                ProgressUpdated?.Invoke(new FileTransferProgress(
                    transfer.TransferId, transfer.FileName, transfer.TotalBytes, transfer.TotalBytes, currentSpeed, true));
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Information("Outgoing transfer {TransferId} was cancelled.", transfer.TransferId);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error during outgoing transfer {TransferId}", transfer.TransferId);
            ProgressUpdated?.Invoke(new FileTransferProgress(
                transfer.TransferId, transfer.FileName, transfer.SentBytes, transfer.TotalBytes, 0, false, ex.Message));
        }
        finally
        {
            _outgoingTransfers.TryRemove(transfer.TransferId, out _);
            transfer.Dispose();
        }
    }

    private void OnTransportDataReceived(byte channel, ReadOnlyMemory<byte> rawData)
    {
        if (channel != DataChannels.Files) return;

        if (!ProtocolEnvelope.TryDecode(rawData.Span, out var envelope, out _))
        {
            return;
        }

        switch (envelope.MessageType)
        {
            case MessageType.FileTransferRequest:
                HandleFileTransferRequest(ProtocolSerializer.Deserialize<FileTransferRequestMessage>(envelope.Payload));
                break;

            case MessageType.FileTransferResponse:
                HandleFileTransferResponse(ProtocolSerializer.Deserialize<FileTransferResponseMessage>(envelope.Payload));
                break;

            case MessageType.FileChunk:
                HandleFileChunk(ProtocolSerializer.Deserialize<FileChunkMessage>(envelope.Payload));
                break;

            case MessageType.FileTransferAck:
                HandleFileTransferAck(ProtocolSerializer.Deserialize<FileTransferAckMessage>(envelope.Payload));
                break;

            case MessageType.FileTransferCancel:
                HandleFileTransferCancel(ProtocolSerializer.Deserialize<FileTransferCancelMessage>(envelope.Payload));
                break;
        }
    }

    private void HandleFileTransferRequest(FileTransferRequestMessage req)
    {
        _logger.Information("Received FileTransferRequest for {FileName} ({Bytes} bytes)", req.FileName, req.FileSizeBytes);

        string safeFileName = Path.GetFileName(req.FileName);
        string finalPath = Path.Combine(_downloadDirectory, safeFileName);
        string partPath = finalPath + ".part";

        long resumeOffset = 0;
        if (File.Exists(partPath))
        {
            var partInfo = new FileInfo(partPath);
            if (partInfo.Length <= req.FileSizeBytes)
            {
                resumeOffset = partInfo.Length;
                _logger.Information("Resuming existing file transfer from byte {Offset}", resumeOffset);
            }
            else
            {
                File.Delete(partPath);
            }
        }

        var incoming = new IncomingTransfer(req.TransferId, finalPath, partPath, safeFileName, req.FileSizeBytes, req.Sha256Checksum, resumeOffset);
        _incomingTransfers[req.TransferId] = incoming;

        var resp = new FileTransferResponseMessage
        {
            TransferId = req.TransferId,
            Accepted = true,
            ResumeOffsetBytes = resumeOffset
        };

        _ = SendProtocolMessageAsync(MessageType.FileTransferResponse, resp);

        ProgressUpdated?.Invoke(new FileTransferProgress(
            req.TransferId, safeFileName, resumeOffset, req.FileSizeBytes, 0, false));
    }

    private void HandleFileTransferResponse(FileTransferResponseMessage resp)
    {
        if (_outgoingTransfers.TryGetValue(resp.TransferId, out var outgoing))
        {
            if (resp.Accepted)
            {
                outgoing.ResumeOffset = resp.ResumeOffsetBytes;
                outgoing.AcceptedTcs.TrySetResult(true);
            }
            else
            {
                outgoing.RejectionReason = resp.RejectReason ?? "Transfer rejected by remote peer.";
                outgoing.AcceptedTcs.TrySetResult(false);
            }
        }
    }

    private void HandleFileChunk(FileChunkMessage chunk)
    {
        if (!_incomingTransfers.TryGetValue(chunk.TransferId, out var incoming))
        {
            _logger.Warning("Received chunk for unknown transfer {TransferId}", chunk.TransferId);
            return;
        }

        try
        {
            lock (incoming.Lock)
            {
                incoming.Stream.Seek(chunk.OffsetBytes, SeekOrigin.Begin);
                incoming.Stream.Write(chunk.Data);
                incoming.ReceivedBytes = Math.Max(incoming.ReceivedBytes, chunk.OffsetBytes + chunk.Data.Length);
            }

            // Send ACK
            var ack = new FileTransferAckMessage
            {
                TransferId = chunk.TransferId,
                AcknowledgedOffsetBytes = incoming.ReceivedBytes
            };
            _ = SendProtocolMessageAsync(MessageType.FileTransferAck, ack);

            double speed = 0;
            if (incoming.Stopwatch.ElapsedMilliseconds >= 500)
            {
                speed = (incoming.ReceivedBytes - incoming.LastSpeedCheckBytes) * 1000.0 / incoming.Stopwatch.ElapsedMilliseconds;
                incoming.Stopwatch.Restart();
                incoming.LastSpeedCheckBytes = incoming.ReceivedBytes;
            }

            if (incoming.ReceivedBytes >= incoming.TotalBytes)
            {
                incoming.Stream.Flush();
                incoming.Stream.Dispose();

                // Verify SHA256 Checksum
                string actualHash = ComputeFileHash(incoming.PartPath);
                bool hashValid = string.Equals(actualHash, incoming.ExpectedSha256, StringComparison.OrdinalIgnoreCase);

                if (hashValid)
                {
                    if (File.Exists(incoming.FinalPath))
                    {
                        File.Delete(incoming.FinalPath);
                    }
                    File.Move(incoming.PartPath, incoming.FinalPath);

                    _logger.Information("File transfer {FileName} completed and verified successfully ({Bytes} bytes).", incoming.FileName, incoming.TotalBytes);
                    ProgressUpdated?.Invoke(new FileTransferProgress(
                        chunk.TransferId, incoming.FileName, incoming.TotalBytes, incoming.TotalBytes, speed, true));
                }
                else
                {
                    _logger.Error("Checksum mismatch for {FileName}: expected {Exp}, got {Act}", incoming.FileName, incoming.ExpectedSha256, actualHash);
                    ProgressUpdated?.Invoke(new FileTransferProgress(
                        chunk.TransferId, incoming.FileName, incoming.ReceivedBytes, incoming.TotalBytes, 0, false, "Checksum verification failed"));
                }

                _incomingTransfers.TryRemove(chunk.TransferId, out _);
                incoming.Dispose();
            }
            else
            {
                ProgressUpdated?.Invoke(new FileTransferProgress(
                    chunk.TransferId, incoming.FileName, incoming.ReceivedBytes, incoming.TotalBytes, speed, false));
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to write chunk for transfer {TransferId}", chunk.TransferId);
            _incomingTransfers.TryRemove(chunk.TransferId, out _);
            incoming.Dispose();
        }
    }

    private void HandleFileTransferAck(FileTransferAckMessage ack)
    {
        if (_outgoingTransfers.TryGetValue(ack.TransferId, out var outgoing))
        {
            outgoing.AcknowledgedOffset = Math.Max(outgoing.AcknowledgedOffset, ack.AcknowledgedOffsetBytes);
        }
    }

    private void HandleFileTransferCancel(FileTransferCancelMessage cancel)
    {
        _logger.Information("Transfer {TransferId} cancelled by remote peer: {Reason}", cancel.TransferId, cancel.Reason);

        if (_outgoingTransfers.TryRemove(cancel.TransferId, out var outgoing))
        {
            outgoing.Cts.Cancel();
            outgoing.Dispose();
            ProgressUpdated?.Invoke(new FileTransferProgress(
                cancel.TransferId, outgoing.FileName, outgoing.SentBytes, outgoing.TotalBytes, 0, false, cancel.Reason));
        }

        if (_incomingTransfers.TryRemove(cancel.TransferId, out var incoming))
        {
            incoming.Dispose();
            ProgressUpdated?.Invoke(new FileTransferProgress(
                cancel.TransferId, incoming.FileName, incoming.ReceivedBytes, incoming.TotalBytes, 0, false, cancel.Reason));
        }
    }

    private async Task SendProtocolMessageAsync<T>(MessageType type, T message)
    {
        byte[] payload = ProtocolSerializer.Serialize(message);
        var envelope = new ProtocolEnvelope(DataChannels.Files, type, payload);
        byte[] packet = envelope.ToBytes();
        await _transport.SendMessageAsync(DataChannels.Files, packet).ConfigureAwait(false);
    }

    private static async Task<string> ComputeFileHashAsync(string filePath, CancellationToken ct)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static string ComputeFileHash(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _transport.MessageReceived -= OnTransportDataReceived;

        foreach (var outgoing in _outgoingTransfers.Values)
        {
            outgoing.Cts.Cancel();
            outgoing.Dispose();
        }
        _outgoingTransfers.Clear();

        foreach (var incoming in _incomingTransfers.Values)
        {
            incoming.Dispose();
        }
        _incomingTransfers.Clear();
    }

    private sealed class OutgoingTransfer : IDisposable
    {
        public string TransferId { get; }
        public string LocalPath { get; }
        public string FileName { get; }
        public long TotalBytes { get; }
        public string Checksum { get; }
        public CancellationTokenSource Cts { get; }
        public TaskCompletionSource<bool> AcceptedTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long ResumeOffset { get; set; }
        public long SentBytes { get; set; }
        public long AcknowledgedOffset { get; set; }
        public string? RejectionReason { get; set; }

        public OutgoingTransfer(string transferId, string localPath, string fileName, long totalBytes, string checksum, CancellationToken ct)
        {
            TransferId = transferId;
            LocalPath = localPath;
            FileName = fileName;
            TotalBytes = totalBytes;
            Checksum = checksum;
            Cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }

        public void Dispose()
        {
            Cts.Dispose();
        }
    }

    private sealed class IncomingTransfer : IDisposable
    {
        public string TransferId { get; }
        public string FinalPath { get; }
        public string PartPath { get; }
        public string FileName { get; }
        public long TotalBytes { get; }
        public string ExpectedSha256 { get; }
        public FileStream Stream { get; }
        public object Lock { get; } = new();
        public Stopwatch Stopwatch { get; } = Stopwatch.StartNew();

        public long ReceivedBytes { get; set; }
        public long LastSpeedCheckBytes { get; set; }

        public IncomingTransfer(string transferId, string finalPath, string partPath, string fileName, long totalBytes, string expectedSha256, long resumeOffset)
        {
            TransferId = transferId;
            FinalPath = finalPath;
            PartPath = partPath;
            FileName = fileName;
            TotalBytes = totalBytes;
            ExpectedSha256 = expectedSha256;
            ReceivedBytes = resumeOffset;
            LastSpeedCheckBytes = resumeOffset;

            Stream = new FileStream(partPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 64 * 1024);
            if (resumeOffset > 0)
            {
                Stream.Seek(resumeOffset, SeekOrigin.Begin);
            }
        }

        public void Dispose()
        {
            try { Stream.Dispose(); } catch { }
        }
    }
}
