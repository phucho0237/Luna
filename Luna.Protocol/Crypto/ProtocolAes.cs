using System.Security.Cryptography;
using System.Text;

namespace Luna.Protocol.Crypto;

/// <summary>
/// AES-CBC, all-zero IV, PKCS7. This is how the service encrypts <c>params</c> and decrypts <c>data</c>.
/// 16-byte key (AES-128, for example <c>zpw_enk</c>) or 32-byte key (AES-256, for example the derived key of ParamsEncryptor).
/// </summary>
public static class ProtocolAes
{
    private static readonly byte[] ZeroIv = new byte[16];

    public static byte[] EncryptRaw(ReadOnlySpan<byte> key, ReadOnlySpan<byte> plaintext)
    {
        using var aes = CreateAes(key);
        return aes.EncryptCbc(plaintext, ZeroIv, PaddingMode.PKCS7);
    }

    public static byte[] DecryptRaw(ReadOnlySpan<byte> key, ReadOnlySpan<byte> ciphertext)
    {
        using var aes = CreateAes(key);
        return aes.DecryptCbc(ciphertext, ZeroIv, PaddingMode.PKCS7);
    }

    /// <summary>Encrypts a UTF-8 string and returns Base64.</summary>
    public static string EncryptToBase64(ReadOnlySpan<byte> key, string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return Convert.ToBase64String(EncryptRaw(key, Encoding.UTF8.GetBytes(plaintext)));
    }

    /// <summary>Decrypts a Base64 string and returns a UTF-8 string. A wrong key usually throws <see cref="CryptographicException"/>.</summary>
    public static string DecryptFromBase64(ReadOnlySpan<byte> key, string base64)
    {
        ArgumentNullException.ThrowIfNull(base64);
        byte[] cipher = Convert.FromBase64String(base64.Trim());
        return Encoding.UTF8.GetString(DecryptRaw(key, cipher));
    }

    private static Aes CreateAes(ReadOnlySpan<byte> key)
    {
        if (key.Length is not (16 or 32))
        {
            throw new ArgumentException($"The AES key must be 16 or 32 bytes long, got {key.Length}.", nameof(key));
        }

        var aes = Aes.Create();
        aes.Key = key.ToArray();
        return aes;
    }
}
