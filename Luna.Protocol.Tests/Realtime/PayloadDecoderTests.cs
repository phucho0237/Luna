using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using Luna.Protocol.Realtime;

namespace Luna.Protocol.Tests.Realtime;

public class PayloadDecoderTests
{
    private static readonly byte[] Key = Convert.FromBase64String(Vectors.CipherKey);

    [Theory]
    [InlineData(1, Vectors.Encrypt1Zlib)]
    [InlineData(1, Vectors.Encrypt1Gzip)]
    [InlineData(1, Vectors.Encrypt1RawDeflate)]
    [InlineData(2, Vectors.Encrypt2Gzip)]
    [InlineData(2, Vectors.Encrypt2Zlib)]
    [InlineData(3, Vectors.Encrypt3)]
    [InlineData(3, Vectors.Encrypt3UrlEncoded)]
    public void Decodes_reference_vectors(int encryptType, string data)
    {
        byte[] decoded = PayloadDecoder.Decode(Key, encryptType, data);

        Assert.Equal(Vectors.Plain, Encoding.UTF8.GetString(decoded));
    }

    [Fact]
    public void Type_zero_returns_the_text_as_is()
    {
        Assert.Equal("{\"a\":1}", Encoding.UTF8.GetString(PayloadDecoder.Decode(null, 0, "{\"a\":1}")));
    }

    [Fact]
    public void Encrypted_types_require_a_cipher_key()
    {
        Assert.Throws<InvalidDataException>(() => PayloadDecoder.Decode(null, 3, Vectors.Encrypt3));
    }

    [Fact]
    public void Rejects_unknown_encrypt_type()
    {
        Assert.Throws<InvalidDataException>(() => PayloadDecoder.Decode(Key, 9, "x"));
    }

    [Fact]
    public void Rejects_blob_that_is_too_short()
    {
        string tooShort = Convert.ToBase64String(new byte[20]);

        Assert.Throws<InvalidDataException>(() => PayloadDecoder.Decode(Key, 3, tooShort));
    }

    [Fact]
    public void Tampered_blob_fails_authentication()
    {
        byte[] blob = Convert.FromBase64String(Vectors.Encrypt3);
        blob[^1] ^= 0xFF;

        Assert.Throws<CryptographicException>(() => PayloadDecoder.Decode(Key, 3, Convert.ToBase64String(blob)));
    }

    [Fact]
    public void Wrong_key_fails_authentication()
    {
        byte[] wrongKey = new byte[32];

        Assert.Throws<CryptographicException>(() => PayloadDecoder.Decode(wrongKey, 3, Vectors.Encrypt3));
    }

    [Fact]
    public void DecodeEnvelope_returns_the_decrypted_json()
    {
        using JsonDocument envelope = JsonDocument.Parse("{\"encrypt\":3,\"data\":\"" + Vectors.Encrypt3 + "\"}");

        JsonElement payload = PayloadDecoder.DecodeEnvelope(envelope.RootElement, Key);

        string? content = payload.GetProperty("data").GetProperty("msgs")[0].GetProperty("content").GetString();
        Assert.Equal("xin chào", content);
    }

    [Fact]
    public void DecodeEnvelope_without_data_returns_the_envelope_itself()
    {
        using JsonDocument envelope = JsonDocument.Parse("{\"encrypt\":0,\"error_code\":0}");

        JsonElement payload = PayloadDecoder.DecodeEnvelope(envelope.RootElement, null);

        Assert.Equal(0, payload.GetProperty("error_code").GetInt32());
    }

    [Fact]
    public void DecodeEnvelope_keeps_large_ids_exact()
    {
        using JsonDocument envelope = JsonDocument.Parse(
            "{\"encrypt\":0,\"data\":\"{\\\"id\\\":9007199254740993123}\"}");

        JsonElement payload = PayloadDecoder.DecodeEnvelope(envelope.RootElement, null);

        Assert.Equal("9007199254740993123", payload.GetProperty("id").GetRawText());
    }
}