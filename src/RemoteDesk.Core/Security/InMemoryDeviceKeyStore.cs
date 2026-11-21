namespace RemoteDesk.Core.Security;

public sealed class InMemoryDeviceKeyStore : IDeviceKeyStore
{
    private byte[]? _keyBytes;

    public bool HasKey() => _keyBytes != null;

    public byte[]? LoadKey() => _keyBytes?.ToArray();

    public void SaveKey(byte[] keyBytes)
    {
        _keyBytes = keyBytes.ToArray();
    }
}
