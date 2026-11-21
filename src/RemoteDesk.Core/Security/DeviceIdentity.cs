using System.Security.Cryptography;
using System.Text;

namespace RemoteDesk.Core.Security;

public sealed class DeviceIdentity : IDisposable
{
    private readonly ECDsa _ecdsa;
    private readonly IDeviceKeyStore? _keyStore;

    public string DeviceId { get; }
    public byte[] PublicKeyBytes { get; }
    public string PublicKeyBase64 => Convert.ToBase64String(PublicKeyBytes);

    public DeviceIdentity(IDeviceKeyStore? keyStore = null)
    {
        _keyStore = keyStore;
        _ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        if (_keyStore != null && _keyStore.HasKey())
        {
            var existingKey = _keyStore.LoadKey();
            if (existingKey != null && existingKey.Length > 0)
            {
                _ecdsa.ImportPkcs8PrivateKey(existingKey, out _);
            }
            else
            {
                SaveCurrentKey();
            }
        }
        else
        {
            SaveCurrentKey();
        }

        PublicKeyBytes = _ecdsa.ExportSubjectPublicKeyInfo();
        DeviceId = ComputeDeviceId(PublicKeyBytes);
    }

    private void SaveCurrentKey()
    {
        if (_keyStore != null)
        {
            var pkcs8 = _ecdsa.ExportPkcs8PrivateKey();
            _keyStore.SaveKey(pkcs8);
        }
    }

    public static string ComputeDeviceId(byte[] publicKeyBytes)
    {
        var hash = SHA256.HashData(publicKeyBytes);
        // Map first 3 bytes to numeric 6-digit string: XXX-YYY
        var num = (uint)((hash[0] << 16) | (hash[1] << 8) | hash[2]) % 900000 + 100000;
        return $"{num / 1000:D3}-{num % 1000:D3}";
    }

    public byte[] Sign(byte[] data)
    {
        return _ecdsa.SignData(data, HashAlgorithmName.SHA256);
    }

    public byte[] Sign(string text)
    {
        return Sign(Encoding.UTF8.GetBytes(text));
    }

    public bool Verify(byte[] data, byte[] signature)
    {
        return _ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256);
    }

    public bool Verify(string text, byte[] signature)
    {
        return Verify(Encoding.UTF8.GetBytes(text), signature);
    }

    public static bool VerifySignature(byte[] publicKeyBytes, byte[] data, byte[] signature)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(publicKeyBytes, out _);
        return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256);
    }

    public static bool VerifySignature(byte[] publicKeyBytes, string text, byte[] signature)
    {
        return VerifySignature(publicKeyBytes, Encoding.UTF8.GetBytes(text), signature);
    }

    public void Dispose()
    {
        _ecdsa.Dispose();
    }
}
