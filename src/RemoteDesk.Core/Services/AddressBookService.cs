using System.Text.Json;
using RemoteDesk.Core.Models;
using Serilog;

namespace RemoteDesk.Core.Services;

public sealed class AddressBookService
{
    private readonly string _storageFilePath;
    private readonly ILogger _logger = Log.ForContext<AddressBookService>();
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public AddressBookService(string? customPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            _storageFilePath = customPath;
        }
        else
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string dir = Path.Combine(appData, "RemoteDesk");
            Directory.CreateDirectory(dir);
            _storageFilePath = Path.Combine(dir, "addressbook.json");
        }
    }

    public async Task<List<AddressBookEntry>> GetAllEntriesAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!File.Exists(_storageFilePath))
            {
                return [];
            }
        }

        try
        {
            await using var stream = new FileStream(_storageFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            var entries = await JsonSerializer.DeserializeAsync<List<AddressBookEntry>>(stream, JsonOptions, ct).ConfigureAwait(false);
            return entries ?? [];
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to read address book from {Path}", _storageFilePath);
            return [];
        }
    }

    public async Task UpsertEntryAsync(AddressBookEntry entry, CancellationToken ct = default)
    {
        var entries = await GetAllEntriesAsync(ct).ConfigureAwait(false);

        int index = entries.FindIndex(e => string.Equals(e.DeviceId, entry.DeviceId, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            entries[index] = entry;
        }
        else
        {
            entries.Add(entry);
        }

        await SaveEntriesAsync(entries, ct).ConfigureAwait(false);
    }

    public async Task<bool> DeleteEntryAsync(string deviceId, CancellationToken ct = default)
    {
        var entries = await GetAllEntriesAsync(ct).ConfigureAwait(false);
        int removedCount = entries.RemoveAll(e => string.Equals(e.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));

        if (removedCount > 0)
        {
            await SaveEntriesAsync(entries, ct).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    public async Task<bool> ToggleFavoriteAsync(string deviceId, CancellationToken ct = default)
    {
        var entries = await GetAllEntriesAsync(ct).ConfigureAwait(false);
        var entry = entries.Find(e => string.Equals(e.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
        if (entry != null)
        {
            entry.IsFavorite = !entry.IsFavorite;
            await SaveEntriesAsync(entries, ct).ConfigureAwait(false);
            return entry.IsFavorite;
        }

        return false;
    }

    public async Task RecordConnectionAsync(string deviceId, CancellationToken ct = default)
    {
        var entries = await GetAllEntriesAsync(ct).ConfigureAwait(false);
        var entry = entries.Find(e => string.Equals(e.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
        if (entry != null)
        {
            entry.LastConnectedUtc = DateTime.UtcNow;
            await SaveEntriesAsync(entries, ct).ConfigureAwait(false);
        }
        else
        {
            await UpsertEntryAsync(new AddressBookEntry
            {
                DeviceId = deviceId,
                Alias = deviceId,
                LastConnectedUtc = DateTime.UtcNow
            }, ct).ConfigureAwait(false);
        }
    }

    private async Task SaveEntriesAsync(List<AddressBookEntry> entries, CancellationToken ct)
    {
        string? dir = Path.GetDirectoryName(_storageFilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tempPath = _storageFilePath + ".tmp";
        await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, entries, JsonOptions, ct).ConfigureAwait(false);
        }

        lock (_lock)
        {
            File.Move(tempPath, _storageFilePath, overwrite: true);
        }
    }
}
