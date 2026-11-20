using RemoteDesk.Core.Models;

namespace RemoteDesk.Core.Interfaces;

public interface IClipboardService : IDisposable
{
    event Action<ClipboardData>? RemoteClipboardChanged;

    Task SetClipboardAsync(ClipboardData data, CancellationToken ct = default);
    Task<ClipboardData?> GetClipboardAsync(CancellationToken ct = default);
    void StartMonitoring();
    void StopMonitoring();
}
