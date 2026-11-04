using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace Luna.Protocol.Realtime;

/// <summary>
/// Decodes the <c>data</c> field of a WebSocket frame by the <c>encrypt</c> type:
/// 0 plain, 1 base64 + compressed, 2 AES-GCM + compressed, 3 AES-GCM.
/// </summary>
public static class PayloadDecoder
{
    private const int GcmIvLength = 16;
    private const int GcmAadLength = 16;
    private const int GcmTagBits = 128;
    private const int MinGcmBlob = GcmIvLength + GcmAadLength + 16;

    /// <summary>Decodes <paramref name="data"/> into JSON bytes.</summary>
    public static byte[] Decode(byte[]? cipherKey, int encryptType, string data)
    {
        ArgumentNullException.ThrowIfNull(data);

        switch (encryptType)
        {
            case 0:
                return Encoding.UTF8.GetBytes(data);
            case 1:
                return Inflate(Convert.FromBase64String(data));
            case 2:
                return Inflate(DecryptGcm(RequireKey(cipherKey), DecodeBase64Url(data)));
            case 3:
                return DecryptGcm(RequireKey(cipherKey), DecodeBase64Url(data));
            default:
                throw new InvalidDataException($"Encryption type {encryptType} is not supported.");
        }
    }

    /// <summary>
    /// Gets the real content of an <c>{encrypt, data}</c> envelope. Large integers (message id, uid) are kept
    /// verbatim as text inside the <see cref="JsonElement"/>; read them with <c>GetRawText()</c>, do not convert to double.
    /// </summary>
    public static JsonElement DecodeEnvelope(JsonElement envelope, byte[]? cipherKey)
    {
        if (envelope.ValueKind != JsonValueKind.Object
            || !envelope.TryGetProperty("data", out JsonElement dataElement)
            || dataElement.ValueKind != JsonValueKind.String)
        {
            return envelope.Clone();
        }

        int encrypt = 0;
        if (envelope.TryGetProperty("encrypt", out JsonElement encryptElement)
            && encryptElement.ValueKind == JsonValueKind.Number)
        {
            encrypt = encryptElement.TryGetInt32(out int parsed) ? parsed : -1;
        }

        string data = dataElement.GetString() ?? "";
        if (encrypt == 0)
        {
            try
            {
                using JsonDocument plain = JsonDocument.Parse(data);
                return plain.RootElement.Clone();
            }
            catch (JsonException)
            {
                return envelope.Clone();
            }
        }

        using JsonDocument decoded = JsonDocument.Parse(Decode(cipherKey, encrypt, data));
        return decoded.RootElement.Clone();
    }

    private static byte[] RequireKey(byte[]? cipherKey) =>
        cipherKey ?? throw new InvalidDataException("No cipher key yet from the authentication step.");

    /// <summary>The Base64 may be URL-encoded (%2B, %2F, %3D); decode that first, then decode the base64.</summary>
    private static byte[] DecodeBase64Url(string data) => Convert.FromBase64String(Uri.UnescapeDataString(data));

#pragma warning disable CS0618 // AesEngine may be marked obsolete in newer BouncyCastle versions; it is still usable.
    /// <summary>AES-GCM with a 16-byte IV: <c>iv(16) | aad(16) | ciphertext||tag(16)</c>.</summary>
    private static byte[] DecryptGcm(byte[] key, byte[] blob)
    {
        if (blob.Length < MinGcmBlob)
        {
            throw new InvalidDataException($"The AES-GCM block is too short ({blob.Length} bytes).");
        }

        byte[] iv = blob[..GcmIvLength];
        byte[] aad = blob[GcmIvLength..(GcmIvLength + GcmAadLength)];
        byte[] cipherText = blob[(GcmIvLength + GcmAadLength)..];

        try
        {
            var gcm = new GcmBlockCipher(new AesEngine());
            gcm.Init(false, new AeadParameters(new KeyParameter(key), GcmTagBits, iv, aad));

            byte[] output = new byte[gcm.GetOutputSize(cipherText.Length)];
            int length = gcm.ProcessBytes(cipherText, 0, cipherText.Length, output, 0);
            length += gcm.DoFinal(output, length);
            return output[..length];
        }
        catch (Org.BouncyCastle.Crypto.InvalidCipherTextException ex)
        {
            throw new CryptographicException("AES-GCM decryption failed (wrong key or corrupt data).", ex);
        }
    }
#pragma warning restore CS0618

    /// <summary>The server sends gzip, an older version sent zlib, and there is also raw deflate. Detected automatically.</summary>
    private static byte[] Inflate(byte[] input)
    {
        using var source = new MemoryStream(input);
        using Stream decoder = IsGzip(input)
            ? new GZipStream(source, CompressionMode.Decompress)
            : LooksLikeZlib(input)
                ? new ZLibStream(source, CompressionMode.Decompress)
                : new DeflateStream(source, CompressionMode.Decompress);

        using var output = new MemoryStream();
        decoder.CopyTo(output);
        return output.ToArray();
    }

    private static bool IsGzip(byte[] input) => input.Length >= 2 && input[0] == 0x1F && input[1] == 0x8B;

    private static bool LooksLikeZlib(byte[] input) =>
        input.Length >= 2 && (input[0] & 0x0F) == 8 && ((input[0] << 8) | input[1]) % 31 == 0;
}