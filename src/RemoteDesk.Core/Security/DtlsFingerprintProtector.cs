using System.Text.RegularExpressions;

namespace RemoteDesk.Core.Security;

public static partial class DtlsFingerprintProtector
{
    [GeneratedRegex(@"a=fingerprint:\s*([^\s]+)\s+([^\r\n]+)", RegexOptions.IgnoreCase)]
    private static partial Regex FingerprintRegex();

    public static string? ExtractFingerprint(string sdp)
    {
        if (string.IsNullOrEmpty(sdp)) return null;

        var match = FingerprintRegex().Match(sdp);
        if (match.Success)
        {
            var hashAlgo = match.Groups[1].Value.Trim().ToLowerInvariant();
            var fingerprint = match.Groups[2].Value.Trim().ToUpperInvariant();
            return $"{hashAlgo} {fingerprint}";
        }

        return null;
    }

    public static (string Fingerprint, byte[] Signature, byte[] PublicKey, long Timestamp) SignSdp(
        DeviceIdentity identity,
        string sessionId,
        string targetConnectionId,
        string sdp)
    {
        var fingerprint = ExtractFingerprint(sdp) ?? string.Empty;
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload = BuildPayload(sessionId, identity.DeviceId, targetConnectionId, fingerprint, timestamp);
        var signature = identity.Sign(payload);

        return (fingerprint, signature, identity.PublicKeyBytes, timestamp);
    }

    public static bool VerifySignedSdp(
        string sdp,
        string sessionId,
        string senderDeviceId,
        string targetConnectionId,
        string reportedFingerprint,
        byte[] signature,
        byte[] senderPublicKey,
        long timestamp,
        TimeSpan? maxClockSkew = null)
    {
        maxClockSkew ??= TimeSpan.FromMinutes(5);

        // 1. Verify Timestamp Freshness
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(now - timestamp) > maxClockSkew.Value.TotalSeconds)
        {
            return false; // Expired or future timestamp
        }

        // 2. Verify that the SDP actually contains the reported fingerprint
        var actualFingerprint = ExtractFingerprint(sdp);
        if (string.IsNullOrEmpty(actualFingerprint) ||
            !actualFingerprint.Equals(reportedFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            return false; // Fingerprint mismatch: SDP was tampered with!
        }

        // 3. Verify Cryptographic Signature with Sender's Public Key
        var payload = BuildPayload(sessionId, senderDeviceId, targetConnectionId, reportedFingerprint, timestamp);
        return DeviceIdentity.VerifySignature(senderPublicKey, payload, signature);
    }

    private static string BuildPayload(string sessionId, string senderDeviceId, string targetConnId, string fingerprint, long timestamp)
    {
        return $"{sessionId}:{senderDeviceId}:{targetConnId}:{fingerprint}:{timestamp}";
    }
}
