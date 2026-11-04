using System.Security.Cryptography;
using System.Text;

namespace Luna.Protocol.Crypto;

/// <summary>
/// MD5 is used only because the protocol requires it (signkey and key derivation), never for any other security purpose.
/// </summary>
internal static class Md5Hex
{
#pragma warning disable CA5351 // Broken cryptographic algorithm: required by the protocol.
    public static string Lower(string text) =>
        Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(text)));
#pragma warning restore CA5351

    public static string Upper(string text) => Lower(text).ToUpperInvariant();
}
