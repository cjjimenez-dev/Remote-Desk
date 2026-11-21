using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using RemoteDesk.Core.Models;
using RemoteDesk.Core.Protocol;
using RemoteDesk.Updater;
using RemoteDesk.Updater.Security;
using Xunit;

namespace RemoteDesk.Core.Tests.Updater;

public class UpdaterSecurityTests : IDisposable
{
    private readonly string _tempFile;

    public UpdaterSecurityTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"update_test_{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(_tempFile, Encoding.UTF8.GetBytes("RemoteDesk-Binary-Update-Payload-2026"));
    }

    [Fact]
    public void Manifest_SerializationRoundTrip_Succeeds()
    {
        var manifest = new UpdateReleaseManifest
        {
            Version = "1.2.0",
            ReleaseDateUtc = DateTime.UtcNow,
            DownloadUrl = "https://updates.remotedesk.net/RemoteDesk-Setup-1.2.0.exe",
            Sha256Checksum = "a1b2c3d4e5f6",
            IsMandatory = true,
            ReleaseNotes = "Performance improvements & bug fixes",
            MinSupportedVersion = "1.0.0",
            PackageSizeBytes = 10485760
        };

        var bytes = ProtocolSerializer.Serialize(manifest);
        var deser = ProtocolSerializer.Deserialize<UpdateReleaseManifest>(bytes);

        deser.Version.Should().Be("1.2.0");
        deser.DownloadUrl.Should().Be("https://updates.remotedesk.net/RemoteDesk-Setup-1.2.0.exe");
        deser.Sha256Checksum.Should().Be("a1b2c3d4e5f6");
        deser.IsMandatory.Should().BeTrue();
        deser.PackageSizeBytes.Should().Be(10485760);
    }

    [Fact]
    public void UpdateSigner_GeneratesAndVerifiesSignature_Successfully()
    {
        var (pubKey, privKey) = UpdateSigner.GenerateSigningKeyPair();

        string version = "1.2.0";
        string sha256 = "6b86b273ff34fce19d6b804eff5a3f5747ada4eaa22f1d49c01e52ddb7875b4b";

        var signature = UpdateSigner.SignManifest(version, sha256, privKey);
        signature.Should().NotBeNullOrEmpty();

        // Valid verification
        bool isValid = UpdateSigner.VerifyManifestSignature(version, sha256, signature, pubKey);
        isValid.Should().BeTrue();

        // Tampered checksum detection
        string tamperedSha = "0000000000000000000000000000000000000000000000000000000000000000";
        bool tamperedShaResult = UpdateSigner.VerifyManifestSignature(version, tamperedSha, signature, pubKey);
        tamperedShaResult.Should().BeFalse();

        // Tampered version detection
        string tamperedVer = "1.3.0";
        bool tamperedVerResult = UpdateSigner.VerifyManifestSignature(tamperedVer, sha256, signature, pubKey);
        tamperedVerResult.Should().BeFalse();
    }

    [Fact]
    public async Task ComputeFileSha256Async_CalculatesExpectedHash()
    {
        // Expected SHA-256 for "RemoteDesk-Binary-Update-Payload-2026"
        using var sha = SHA256.Create();
        var expectedHash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes("RemoteDesk-Binary-Update-Payload-2026"))).ToLowerInvariant();

        var computedHash = await UpdateManager.ComputeFileSha256Async(_tempFile);
        computedHash.Should().Be(expectedHash);
    }

    [Fact]
    public void VersionComparison_DetectsAvailableUpdate()
    {
        var currentVer = new Version(1, 0, 0);
        var newerVer = new Version(1, 1, 0);
        var olderVer = new Version(0, 9, 0);

        (newerVer > currentVer).Should().BeTrue();
        (olderVer > currentVer).Should().BeFalse();
        (currentVer.CompareTo(new Version(1, 0, 0)) == 0).Should().BeTrue();
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile))
        {
            try { File.Delete(_tempFile); } catch { }
        }
    }
}
