using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using RemoteDesk.Core.Models;
using RemoteDesk.Updater.Security;
using Serilog;

namespace RemoteDesk.Updater;

public enum UpdateState
{
    Idle,
    Checking,
    UpdateAvailable,
    Downloading,
    Verifying,
    ReadyToApply,
    UpToDate,
    Failed
}

public sealed class UpdateManager : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly byte[]? _releasePublicKey;
    private readonly ILogger _logger = Log.ForContext<UpdateManager>();
    private bool _disposed;

    public UpdateState State { get; private set; } = UpdateState.Idle;
    public event Action<UpdateState, string?>? StateChanged;
    public event Action<double>? DownloadProgressChanged;

    public UpdateManager(byte[]? releasePublicKey = null, HttpClient? httpClient = null)
    {
        _releasePublicKey = releasePublicKey;
        _ownsHttpClient = httpClient == null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(string feedUrl, Version currentVersion, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SetState(UpdateState.Checking, "Checking for updates...");

        try
        {
            var url = $"{feedUrl.TrimEnd('/')}/api/updates/check?version={currentVersion}";
            var result = await _httpClient.GetFromJsonAsync<UpdateCheckResult>(url, cancellationToken: ct).ConfigureAwait(false);

            if (result != null && result.IsUpdateAvailable && result.LatestRelease != null)
            {
                SetState(UpdateState.UpdateAvailable, $"New version {result.LatestRelease.Version} available.");
                return result;
            }

            SetState(UpdateState.UpToDate, "Application is up to date.");
            return new UpdateCheckResult { IsUpdateAvailable = false, CurrentVersion = currentVersion.ToString() };
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to check for updates from {FeedUrl}", feedUrl);
            SetState(UpdateState.Failed, $"Update check failed: {ex.Message}");
            return new UpdateCheckResult { IsUpdateAvailable = false, CurrentVersion = currentVersion.ToString() };
        }
    }

    public async Task<bool> DownloadAndVerifyUpdateAsync(
        UpdateReleaseManifest release,
        string destinationFilePath,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SetState(UpdateState.Downloading, $"Downloading version {release.Version}...");

        try
        {
            var dir = Path.GetDirectoryName(destinationFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using (var response = await _httpClient.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? release.PackageSizeBytes;
                long totalRead = 0;
                var buffer = new byte[81920]; // 80 KB buffer

                await using var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var fileStream = new FileStream(destinationFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                int bytesRead;
                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                    totalRead += bytesRead;

                    if (totalBytes > 0)
                    {
                        double percent = (double)totalRead / totalBytes * 100.0;
                        DownloadProgressChanged?.Invoke(Math.Min(100.0, percent));
                    }
                }
            }

            // Verify integrity and authenticity
            SetState(UpdateState.Verifying, "Verifying package integrity...");

            // 1. Check SHA-256
            string actualSha256 = await ComputeFileSha256Async(destinationFilePath, ct).ConfigureAwait(false);
            if (!string.Equals(actualSha256, release.Sha256Checksum, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Error("SHA-256 mismatch for update package. Expected: {Expected}, Actual: {Actual}",
                    release.Sha256Checksum, actualSha256);
                SetState(UpdateState.Failed, "Integrity verification failed: SHA-256 checksum mismatch.");
                return false;
            }

            // 2. Cryptographic signature check
            if (_releasePublicKey != null && release.Signature != null)
            {
                bool valid = UpdateSigner.VerifyManifestSignature(release.Version, release.Sha256Checksum, release.Signature, _releasePublicKey);
                if (!valid)
                {
                    _logger.Error("Cryptographic signature validation failed for release {Version}", release.Version);
                    SetState(UpdateState.Failed, "Security verification failed: invalid digital signature.");
                    return false;
                }
            }

            SetState(UpdateState.ReadyToApply, $"Version {release.Version} downloaded and verified successfully.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Download failed for release {Version}", release.Version);
            SetState(UpdateState.Failed, $"Download failed: {ex.Message}");
            return false;
        }
    }

    public static async Task<string> ComputeFileSha256Async(string filePath, CancellationToken ct = default)
    {
        using var sha256 = SHA256.Create();
        await using var stream = File.OpenRead(filePath);
        var hash = await sha256.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool ApplyUpdateAndHandover(string installerPath, string? arguments = null, bool terminateCurrentProcess = true)
    {
        if (!File.Exists(installerPath)) return false;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = arguments ?? "/SILENT",
                UseShellExecute = true
            };

            Process.Start(psi);

            if (terminateCurrentProcess)
            {
                Environment.Exit(0);
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to launch update installer at {Path}", installerPath);
            return false;
        }
    }

    private void SetState(UpdateState state, string? message)
    {
        State = state;
        _logger.Information("Update state: {State} - {Message}", state, message);
        StateChanged?.Invoke(state, message);
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
