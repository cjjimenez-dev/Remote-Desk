using System.Security.Cryptography;
using System.Text;

namespace RemoteDesk.Core.Security;

public static class SessionChallengeAuth
{
    private const int Iterations = 100_000;
    private const int KeyLength = 32;

    public static byte[] DeriveKey(string password, byte[] salt)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            KeyLength);
    }

    public static (byte[] Salt, byte[] StoredKey) HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = DeriveKey(password, salt);
        return (salt, key);
    }

    public static byte[] GenerateNonce()
    {
        return RandomNumberGenerator.GetBytes(32);
    }

    public static byte[] ComputeResponse(byte[] derivedKey, byte[] nonce, string sessionId)
    {
        var sessionBytes = Encoding.UTF8.GetBytes(sessionId);
        var data = new byte[nonce.Length + sessionBytes.Length];
        Buffer.BlockCopy(nonce, 0, data, 0, nonce.Length);
        Buffer.BlockCopy(sessionBytes, 0, data, nonce.Length, sessionBytes.Length);

        return HMACSHA256.HashData(derivedKey, data);
    }

    public static bool VerifyResponse(byte[] storedKey, byte[] nonce, string sessionId, byte[] response)
    {
        var expected = ComputeResponse(storedKey, nonce, sessionId);
        return CryptographicOperations.FixedTimeEquals(expected, response);
    }
}
