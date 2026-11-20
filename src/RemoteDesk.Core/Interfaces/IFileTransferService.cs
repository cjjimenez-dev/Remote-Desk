namespace RemoteDesk.Core.Interfaces;

public record FileTransferProgress(
    string TransferId,
    string FileName,
    long BytesTransferred,
    long TotalBytes,
    double SpeedBytesPerSecond,
    bool IsCompleted,
    string? ErrorMessage = null);

public interface IFileTransferService : IDisposable
{
    event Action<FileTransferProgress>? ProgressUpdated;

    Task<string> SendFileAsync(string localFilePath, CancellationToken ct = default);
    Task CancelTransferAsync(string transferId);
}
