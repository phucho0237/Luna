using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Luna.Protocol.Crypto;

/// <summary>
/// ParamCipher v2: encrypts parameters for requests that have no session key <c>zpw_enk</c> yet (login, getServerInfo...).
/// The key <see cref="EncryptKey"/> is used for both the outgoing <c>params</c> and the <c>data</c> in the matching response.
/// </summary>
public sealed class ParamsEncryptor
{
    public const string EncVer = "v2";

    // The client's static key, used as-is as 32 ASCII characters for the AES-256 key.
    private const string ParamCipherKey = "3FC4F0D2AB50057BCE0D90D9187A22B1";

    private readonly byte[] _keyBytes;

    /// <param name="apiType">Value of <c>zpw_type</c>, for example "30".</param>
    /// <param name="imei">Device identifier.</param>
    /// <param name="firstLaunchTime">Time the app was first opened (Unix ms).</param>
    /// <param name="zcidExt">Only pass when a reproducible result is needed (tests). Leave empty to generate randomly.</param>
    public ParamsEncryptor(string apiType, string imei, long firstLaunchTime, string? zcidExt = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(apiType);
        ArgumentException.ThrowIfNullOrEmpty(imei);
        ArgumentOutOfRangeException.ThrowIfZero(firstLaunchTime);

        string message = string.Create(CultureInfo.InvariantCulture, $"{apiType},{imei},{firstLaunchTime}");
        byte[] cipher = ProtocolAes.EncryptRaw(Encoding.UTF8.GetBytes(ParamCipherKey), Encoding.UTF8.GetBytes(message));

        Zcid = Convert.ToHexString(cipher); // uppercase
        ZcidExt = zcidExt ?? RandomHex();
        EncryptKey = DeriveKey(Zcid, ZcidExt);
        _keyBytes = Encoding.UTF8.GetBytes(EncryptKey);
    }

    public string Zcid { get; }

    public string ZcidExt { get; }

    /// <summary>32 ASCII characters, used directly as the AES-256 key.</summary>
    public string EncryptKey { get; }

    /// <summary>Encrypts the JSON payload to Base64; this is the value of the <c>params</c> field.</summary>
    public string EncryptPayload(string payloadJson) => ProtocolAes.EncryptToBase64(_keyBytes, payloadJson);

    /// <summary>Decrypts the <c>data</c> field (Base64) of the matching response.</summary>
    public string DecryptResponse(string dataBase64) =>
     ProtocolAes.DecryptFromBase64(_keyBytes, Uri.UnescapeDataString(dataBase64));

    /// <summary>
    /// Builds the parameter set of a request: <c>zcid, zcid_ext, enc_ver, params, type, client_version, signkey</c>.
    /// <c>signkey</c> signs only the first 6 fields. Parameters added later (<c>nretry</c>, <c>ref</c>...) must be appended to the URL
    /// after the signature is computed.
    /// </summary>
    /// <param name="tag">Normalized command name, see <see cref="SignKey.NormalizeTag"/>.</param>
    public IReadOnlyDictionary<string, string> BuildRequest(
        string apiType, string apiVersion, string payloadJson, string tag)
    {
        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["zcid"] = Zcid,
            ["zcid_ext"] = ZcidExt,
            ["enc_ver"] = EncVer,
            ["params"] = EncryptPayload(payloadJson),
            ["type"] = apiType,
            ["client_version"] = apiVersion,
        };

        fields["signkey"] = SignKey.Compute(tag, fields);
        return fields;
    }

    private static string DeriveKey(string zcid, string zcidExt)
    {
        string md5Upper = Md5Hex.Upper(zcidExt);

        string fromMd5 = PickEvery2(md5Upper, 0)[..8];
        string fromZcidEven = PickEvery2(zcid, 0)[..12];
        char[] odd = PickEvery2(zcid, 1).ToCharArray();
        Array.Reverse(odd);
        string fromZcidOdd = new string(odd)[..12];

        return fromMd5 + fromZcidEven + fromZcidOdd;
    }

    private static string PickEvery2(string s, int start)
    {
        var sb = new StringBuilder(s.Length / 2 + 1);
        for (int i = start; i < s.Length; i += 2)
        {
            sb.Append(s[i]);
        }

        return sb.ToString();
    }

    /// <summary>Lowercase hex string 6 to 12 characters long, like the original.</summary>
    private static string RandomHex()
    {
        int length = RandomNumberGenerator.GetInt32(6, 13);
        return Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))[..length];
    }
}
