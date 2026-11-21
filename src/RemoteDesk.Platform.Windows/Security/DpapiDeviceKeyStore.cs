using System.IO;
using System.Security.Cryptography;
using RemoteDesk.Core.Security;

namespace RemoteDesk.Platform.Windows.Security;

public sealed class DpapiDeviceKeyStore : IDeviceKeyStore
{
    private readonly string _filePath;
    private readonly DataProtectionScope _scope;
    private readonly byte[]? _entropy;

    public DpapiDeviceKeyStore(string? filePath = null, DataProtectionScope scope = DataProtectionScope.CurrentUser)
    {
        _scope = scope;
        _entropy = "RemoteDesk-DeviceKey-Entropy-v1"u8.ToArray();

        if (string.IsNullOrEmpty(filePath))
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteDesk");
            Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, "device.key");
        }
        else
        {
            _filePath = filePath;
        }
    }

    public bool HasKey() => File.Exists(_filePath);

    public byte[]? LoadKey()
    {
        if (!File.Exists(_filePath)) return null;

        try
        {
            var encrypted = File.ReadAllBytes(_filePath);
            return ProtectedData.Unprotect(encrypted, _entropy, _scope);
        }
        catch
        {
            return null;
        }
    }

    public void SaveKey(byte[] keyBytes)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var encrypted = ProtectedData.Protect(keyBytes, _entropy, _scope);
        File.WriteAllBytes(_filePath, encrypted);
    }
}
