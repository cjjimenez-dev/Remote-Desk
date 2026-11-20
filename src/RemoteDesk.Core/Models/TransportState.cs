namespace RemoteDesk.Core.Models;

public enum TransportState
{
    New,
    Connecting,
    Connected,
    Reconnecting,
    Disconnected,
    Failed,
    Closed
}
