namespace RemoteDesk.Core.Interfaces;

public interface ITerminalSession : IAsyncDisposable
{
    string SessionId { get; }
    bool IsRunning { get; }

    event Action<string>? OutputReceived;
    event Action<int>? Exited;

    ValueTask WriteInputAsync(string input, CancellationToken ct = default);
    void Resize(int columns, int rows);
    Task TerminateAsync();
}
