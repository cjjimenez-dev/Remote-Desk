using System.Security.Cryptography;
using System.Text;

namespace RemoteDesk.Updater.Security;

public static class UpdateSigner
{
    public static (byte[] PublicKey, byte[] PrivateKey) GenerateSigningKeyPair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (ecdsa.ExportSubjectPublicKeyInfo(), ecdsa.ExportPkcs8PrivateKey());
    }

    public static byte[] SignManifest(string version, string sha256Checksum, byte[] privateKeyBytes)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(privateKeyBytes, out _);

        var dataToSign = Encoding.UTF8.GetBytes($"{version}:{sha256Checksum.ToLowerInvariant()}");
        return ecdsa.SignData(dataToSign, HashAlgorithmName.SHA256);
    }

    public static bool VerifyManifestSignature(string version, string sha256Checksum, byte[] signature, byte[] publicKeyBytes)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKeyBytes, out _);

            var dataToVerify = Encoding.UTF8.GetBytes($"{version}:{sha256Checksum.ToLowerInvariant()}");
            return ecdsa.VerifyData(dataToVerify, signature, HashAlgorithmName.SHA256);
        }
        catch
        {
            return false;
        }
    }
}
