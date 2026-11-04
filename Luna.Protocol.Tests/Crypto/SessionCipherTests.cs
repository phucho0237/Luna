using System.Text;
using Xunit;
using Luna.Protocol.Crypto;

namespace Luna.Protocol.Tests.Crypto;

public class SessionCipherTests
{
    // Fake zpw_enk: Base64 of the 16 bytes "0123456789abcdef".
    private static readonly string Enk = Convert.ToBase64String(Encoding.ASCII.GetBytes("0123456789abcdef"));

    // The expected value was produced with Node crypto (aes-128-cbc, zero IV, PKCS7, key = Base64-decoded Enk) and cross-checked with crypto-js, the library zca-js uses.
    // The same method reproduced the earlier zca-js 2.2.0 encodeAES vector for the previous plaintext byte for byte.
    private const string Json = "{\"hello\":\"luna\"}";
    private const string ExpectedCipher = "3oNo40OAnY8cAAMepA9kdEjUqHJW7J4j8m1QGTUQYGA=";

    [Fact]
    public void Encrypt_matches_reference_implementation()
    {
        Assert.Equal(ExpectedCipher, new SessionCipher(Enk).Encrypt(Json));
    }

    [Fact]
    public void Decrypt_reverses_encrypt()
    {
        var cipher = new SessionCipher(Enk);
        Assert.Equal(Json, cipher.Decrypt(ExpectedCipher));
    }

    [Fact]
    public void Decrypt_accepts_url_encoded_data()
    {
        string urlEncoded = Uri.EscapeDataString(ExpectedCipher);
        Assert.Equal(Json, new SessionCipher(Enk).Decrypt(urlEncoded));
    }

    [Fact]
    public void Rejects_key_of_wrong_size()
    {
        string tooShort = Convert.ToBase64String(new byte[8]);
        Assert.Throws<ArgumentException>(() => new SessionCipher(tooShort));
    }
}
