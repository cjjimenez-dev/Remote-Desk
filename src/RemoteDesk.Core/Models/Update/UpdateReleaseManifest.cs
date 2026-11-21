using MessagePack;

namespace RemoteDesk.Core.Models;

[MessagePackObject]
public sealed class UpdateReleaseManifest
{
    [Key(0)] public string Version { get; set; } = "1.0.0";
    [Key(1)] public DateTime ReleaseDateUtc { get; set; } = DateTime.UtcNow;
    [Key(2)] public string DownloadUrl { get; set; } = string.Empty;
    [Key(3)] public string Sha256Checksum { get; set; } = string.Empty;
    [Key(4)] public byte[]? Signature { get; set; }
    [Key(5)] public bool IsMandatory { get; set; }
    [Key(6)] public string ReleaseNotes { get; set; } = string.Empty;
    [Key(7)] public string MinSupportedVersion { get; set; } = "1.0.0";
    [Key(8)] public long PackageSizeBytes { get; set; }
}

[MessagePackObject]
public sealed class UpdateCheckResult
{
    [Key(0)] public bool IsUpdateAvailable { get; set; }
    [Key(1)] public UpdateReleaseManifest? LatestRelease { get; set; }
    [Key(2)] public string CurrentVersion { get; set; } = "1.0.0";
}
