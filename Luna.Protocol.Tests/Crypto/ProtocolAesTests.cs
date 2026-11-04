using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using Luna.Protocol.Crypto;

namespace Luna.Protocol.Tests.Crypto;

public class ProtocolAesTests
{
    // A real response returned by the server (taken from the ctdozlite project's tests).
    // Decrypting to valid JSON means the use of key, IV and PKCS7 is correct byte for byte.
    private const string CapturedKey = "0FCB6C6570D6E640AC3600DB0FED524F";
    private const string CapturedData = "/AUIVJkkEIKTFZRhudPnX2ACqczgaUoCGbNa1ghAj/LnlDAFGlXg4ph8xovT2dKOY9sodSJji03BTHxGufRR5tjZsREykolY59EYa2/5xkoPmePwPeeIF3DgDZ4CD3xlopudNCKuD1K16Kre9yIUm+j1uhXbXbnwVvrYA8UAQnpMME3tjTAle2W4HaNd8YKt";

    [Fact]
    public void Decrypts_real_server_response_with_utf8_32_byte_key()
    {
        string json = ProtocolAes.DecryptFromBase64(Encoding.UTF8.GetBytes(CapturedKey), CapturedData);

        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal(18022, doc.RootElement.GetProperty("error_code").GetInt32());
        Assert.Equal("Invalid Signature", doc.RootElement.GetProperty("error_message").GetString());
    }

    [Fact]
    public void Roundtrips_with_aes128_and_aes256_keys()
    {
        const string text = "{\"hello\":\"luna\",\"vi\":\"xin chào\"}";
        foreach (int size in new[] { 16, 32 })
        {
            byte[] key = Encoding.ASCII.GetBytes(new string('k', size));
            string cipher = ProtocolAes.EncryptToBase64(key, text);
            Assert.Equal(text, ProtocolAes.DecryptFromBase64(key, cipher));
        }
    }

    [Fact]
    public void Rejects_invalid_key_length()
    {
        Assert.Throws<ArgumentException>(() => ProtocolAes.EncryptToBase64(new byte[24], "x"));
    }

    [Fact]
    public void Wrong_key_fails_to_decrypt()
    {
        byte[] right = Encoding.ASCII.GetBytes(new string('a', 32));
        byte[] wrong = Encoding.ASCII.GetBytes(new string('b', 32));
        string cipher = ProtocolAes.EncryptToBase64(right, "payload that is long enough to span blocks 0123456789");
        Assert.ThrowsAny<CryptographicException>(() => ProtocolAes.DecryptFromBase64(wrong, cipher));
    }
}
