using MessagePack;

namespace RemoteDesk.Core.Models;

[MessagePackObject]
public sealed class AddressBookEntry
{
    [Key(0)]
    public string DeviceId { get; set; } = string.Empty;

    [Key(1)]
    public string Alias { get; set; } = string.Empty;

    [Key(2)]
    public string? Notes { get; set; }

    [Key(3)]
    public bool IsFavorite { get; set; }

    [Key(4)]
    public DateTime? LastConnectedUtc { get; set; }

    [Key(5)]
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
