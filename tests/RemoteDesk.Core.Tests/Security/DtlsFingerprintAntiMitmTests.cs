using FluentAssertions;
using RemoteDesk.Core.Security;
using Xunit;

namespace RemoteDesk.Core.Tests.Security;

public class DtlsFingerprintAntiMitmTests
{
    private const string SampleSdp =
        "v=0\r\n" +
        "o=- 20518 0 IN IP4 203.0.113.1\r\n" +
        "s=-\r\n" +
        "t=0 0\r\n" +
        "a=fingerprint:sha-256 4A:AD:B9:B1:3F:82:18:3B:54:02:12:DF:3E:5D:49:6B:19:E5:7C:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23\r\n" +
        "m=video 51372 UDP/TLS/RTP/SAVPF 96\r\n";

    [Fact]
    public void ExtractFingerprint_ParsesAlgorithmAndHexHash()
    {
        var fp = DtlsFingerprintProtector.ExtractFingerprint(SampleSdp);

        fp.Should().NotBeNull();
        fp.Should().Be("sha-256 4A:AD:B9:B1:3F:82:18:3B:54:02:12:DF:3E:5D:49:6B:19:E5:7C:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23");
    }

    [Fact]
    public void SignSdp_And_VerifySignedSdp_ValidScenario_Succeeds()
    {
        using var identity = new DeviceIdentity();
        var sessionId = "sess-8899aabb";
        var targetConnId = "conn-viewer-123";

        var (fingerprint, signature, pubKey, timestamp) = DtlsFingerprintProtector.SignSdp(
            identity,
            sessionId,
            targetConnId,
            SampleSdp);

        var verified = DtlsFingerprintProtector.VerifySignedSdp(
            SampleSdp,
            sessionId,
            identity.DeviceId,
            targetConnId,
            fingerprint,
            signature,
            pubKey,
            timestamp);

        verified.Should().BeTrue();
    }

    [Fact]
    public void MaliciousSignalingServer_TamperingWithDtlsFingerprint_IsDetectedAndRejected()
    {
        using var viewerIdentity = new DeviceIdentity();
        var sessionId = "sess-mitm-test";
        var targetHostConnId = "conn-host-456";

        // 1. Viewer creates and cryptographically signs SDP offer
        var (viewerFingerprint, signature, viewerPubKey, timestamp) = DtlsFingerprintProtector.SignSdp(
            viewerIdentity,
            sessionId,
            targetHostConnId,
            SampleSdp);

        // 2. MALICIOUS SIGNALING SERVER INTERCEPTS SDP:
        // Attacker modifies the DTLS fingerprint to point to their own MITM proxy's certificate!
        var attackerFingerprint = "sha-256 00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF";
        var tamperedSdp = SampleSdp.Replace(
            "4A:AD:B9:B1:3F:82:18:3B:54:02:12:DF:3E:5D:49:6B:19:E5:7C:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23",
            "00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF");

        // 3. Host receives tampered SDP. Host checks anti-MITM verification:
        // Case A: Attacker tries to pass the original reported fingerprint with tampered SDP
        var hostVerifiedTamperedSdp = DtlsFingerprintProtector.VerifySignedSdp(
            tamperedSdp,
            sessionId,
            viewerIdentity.DeviceId,
            targetHostConnId,
            viewerFingerprint,
            signature,
            viewerPubKey,
            timestamp);

        hostVerifiedTamperedSdp.Should().BeFalse(
            "Because the SDP content does not match the signed reported fingerprint (tampering detected)");

        // Case B: Attacker forwards the attacker's fingerprint with original signature
        var hostVerifiedAttackerFp = DtlsFingerprintProtector.VerifySignedSdp(
            tamperedSdp,
            sessionId,
            viewerIdentity.DeviceId,
            targetHostConnId,
            attackerFingerprint,
            signature,
            viewerPubKey,
            timestamp);

        hostVerifiedAttackerFp.Should().BeFalse(
            "Because the signature does not match the attacker's fingerprint (cryptographic signature verification failed)");
    }

    [Fact]
    public void VerifySignedSdp_RejectsExpiredTimestamp()
    {
        using var identity = new DeviceIdentity();
        var sessionId = "sess-123";
        var targetConnId = "conn-456";

        var (fingerprint, signature, pubKey, _) = DtlsFingerprintProtector.SignSdp(
            identity,
            sessionId,
            targetConnId,
            SampleSdp);

        // Expired timestamp: 10 minutes in the past
        var expiredTimestamp = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds();

        var verified = DtlsFingerprintProtector.VerifySignedSdp(
            SampleSdp,
            sessionId,
            identity.DeviceId,
            targetConnId,
            fingerprint,
            signature,
            pubKey,
            expiredTimestamp,
            maxClockSkew: TimeSpan.FromMinutes(2));

        verified.Should().BeFalse("Because the timestamp is older than max permitted clock skew");
    }
}
