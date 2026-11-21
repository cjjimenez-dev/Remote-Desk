using System.Net.Http.Json;
using RemoteDesk.Core.Models;
using Serilog;

namespace RemoteDesk.Core.Services;

public sealed class FleetClientService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly ILogger _logger = Log.ForContext<FleetClientService>();
    private bool _disposed;

    public string BaseUrl { get; }

    public FleetClientService(string baseUrl, HttpClient? httpClient = null)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        _ownsHttpClient = httpClient == null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task<bool> RegisterDeviceAsync(DeviceRecord device, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{BaseUrl}/api/fleet/devices/register", device, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to register device [{DeviceId}] with Fleet API at {Url}", device.DeviceId, BaseUrl);
            return false;
        }
    }

    public async Task<bool> SendHeartbeatAsync(string deviceId, DeviceStatus status, string? currentViewer = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            var req = new { Status = status, CurrentSessionViewer = currentViewer };
            var response = await _httpClient.PostAsJsonAsync($"{BaseUrl}/api/fleet/devices/{deviceId}/heartbeat", req, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Heartbeat failed for device [{DeviceId}]", deviceId);
            return false;
        }
    }

    public async Task<FleetSummary> GetFleetSummaryAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            var summary = await _httpClient.GetFromJsonAsync<FleetSummary>($"{BaseUrl}/api/fleet/summary", ct).ConfigureAwait(false);
            return summary ?? new FleetSummary();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to get fleet summary.");
            return new FleetSummary();
        }
    }

    public async Task<List<DeviceRecord>> GetDevicesAsync(string? tagFilter = null, string? search = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            var query = new List<string>();
            if (!string.IsNullOrWhiteSpace(tagFilter)) query.Add($"tag={Uri.EscapeDataString(tagFilter)}");
            if (!string.IsNullOrWhiteSpace(search)) query.Add($"search={Uri.EscapeDataString(search)}");

            var url = $"{BaseUrl}/api/fleet/devices" + (query.Count > 0 ? "?" + string.Join("&", query) : "");
            var list = await _httpClient.GetFromJsonAsync<List<DeviceRecord>>(url, ct).ConfigureAwait(false);
            return list ?? [];
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to fetch fleet devices.");
            return [];
        }
    }

    public async Task<DeviceRecord?> GetDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            return await _httpClient.GetFromJsonAsync<DeviceRecord>($"{BaseUrl}/api/fleet/devices/{deviceId}", ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to get device [{DeviceId}].", deviceId);
            return null;
        }
    }

    public async Task<bool> UpdateTagsAsync(string deviceId, List<string> tags, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{BaseUrl}/api/fleet/devices/{deviceId}/tags", tags, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to update tags for device [{DeviceId}].", deviceId);
            return false;
        }
    }

    public async Task<FleetCommand?> QueueCommandAsync(string deviceId, FleetCommandType commandType, string? payload = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            var cmd = new FleetCommand
            {
                DeviceId = deviceId,
                CommandType = commandType,
                Payload = payload
            };
            var response = await _httpClient.PostAsJsonAsync($"{BaseUrl}/api/fleet/devices/{deviceId}/commands", cmd, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<FleetCommand>(cancellationToken: ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to queue command for device [{DeviceId}].", deviceId);
        }
        return null;
    }

    public async Task<List<FleetCommand>> GetPendingCommandsAsync(string deviceId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            var list = await _httpClient.GetFromJsonAsync<List<FleetCommand>>($"{BaseUrl}/api/fleet/devices/{deviceId}/commands/pending", ct).ConfigureAwait(false);
            return list ?? [];
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Failed to check pending commands for [{DeviceId}].", deviceId);
            return [];
        }
    }

    public async Task<bool> ReportCommandResultAsync(string commandId, FleetCommandStatus status, string? resultOutput = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            var req = new { Status = status, ResultOutput = resultOutput };
            var response = await _httpClient.PostAsJsonAsync($"{BaseUrl}/api/fleet/commands/{commandId}/result", req, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to report command [{CommandId}] result.", commandId);
            return false;
        }
    }

    public async Task<List<FleetAuditRecord>> GetAuditLogsAsync(string? deviceId = null, int limit = 50, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            var url = $"{BaseUrl}/api/fleet/audit?limit={limit}" + (!string.IsNullOrWhiteSpace(deviceId) ? $"&deviceId={Uri.EscapeDataString(deviceId)}" : "");
            var list = await _httpClient.GetFromJsonAsync<List<FleetAuditRecord>>(url, ct).ConfigureAwait(false);
            return list ?? [];
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to fetch audit logs.");
            return [];
        }
    }

    public async Task<bool> RecordAuditAsync(FleetAuditRecord audit, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{BaseUrl}/api/fleet/audit", audit, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to record audit log.");
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
