using System.Text;

namespace Luna.Protocol.Crypto;

/// <summary>
/// Encryption/decryption for post-login data APIs, using the session key <c>zpw_enk</c>.
/// <c>zpw_enk</c> is the Base64 string of the AES key (usually 16 bytes).
/// </summary>
public sealed class SessionCipher
{
    private readonly byte[] _key;

    public SessionCipher(string zpwEnk)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zpwEnk);
        _key = Convert.FromBase64String(zpwEnk.Trim());
        if (_key.Length is not (16 or 32))
        {
            throw new ArgumentException($"zpw_enk decodes from Base64 to {_key.Length} bytes, expected 16 or 32.", nameof(zpwEnk));
        }
    }

    /// <summary>Encrypts the JSON payload to Base64 to send in the <c>params</c> field.</summary>
    public string Encrypt(string payloadJson) => ProtocolAes.EncryptToBase64(_key, payloadJson);

    /// <summary>Decrypts the <c>data</c> field of the response. The data may be URL-encoded, so it is decoded first.</summary>
    public string Decrypt(string data) =>
        Encoding.UTF8.GetString(ProtocolAes.DecryptRaw(_key, Convert.FromBase64String(Uri.UnescapeDataString(data).Trim())));
}
