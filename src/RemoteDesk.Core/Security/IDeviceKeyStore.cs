namespace RemoteDesk.Core.Security;

public interface IDeviceKeyStore
{
    bool HasKey();
    byte[]? LoadKey();
    void SaveKey(byte[] keyBytes);
}
