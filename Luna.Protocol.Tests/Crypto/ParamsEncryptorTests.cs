using Xunit;
using Luna.Protocol.Crypto;

namespace Luna.Protocol.Tests.Crypto;

public class ParamsEncryptorTests
{
    // The expected values below were produced by running zca-js 2.2.0's own ParamsEncryptor (Node.js)
    // with the same input and a fixed zcid_ext.
    private const string Imei = "3f2a9c1e-7b4d-4e8a-9d21-5c6b8a0f1e34-ab12cd34ef56ab12cd34ef56ab12cd34";
    private const long FirstLaunch = 1761900000000;
    private const string Payload =
        "{\"imei\":\"3f2a9c1e\",\"computer_name\":\"Web\",\"language\":\"vi\",\"ts\":1761900000123}";

    private static ParamsEncryptor Create() => new("30", Imei, FirstLaunch, "abcdef0123");

    [Fact]
    public void Zcid_matches_reference_implementation()
    {
        Assert.Equal(
            "B7C2938E17D8D6BFEBD1ADE482BEF3E6B88901A7BE579A6E59D5656CB01BB833D114F9F81F0C3B5C3EB6CA2B81DECB68" +
            "1B0C0C61515CC691ED8B9A34A1ADF707BF7933DFE70D54FE366D9DBED07EE800C6D0DD7DCBFDDAB846856B6A363FAC2D",
            Create().Zcid);
    }

    [Fact]
    public void Encrypt_key_matches_reference_implementation()
    {
        Assert.Equal("34DB9AE8BC981DDBEDAEDCF6AB568ADB", Create().EncryptKey);
    }

    [Fact]
    public void Encrypted_payload_matches_reference_implementation()
    {
        Assert.Equal(
            "0RJrUsEgnga9MuQybiSvvtGpdoh5VQlrIKd52jBCFYnyfuF8N2zlQXoRVZjFlb0mJoLkeF2AzKFCpwNydlS0fNU5iDl+EjVQ2aYiw33Kg+8=",
            Create().EncryptPayload(Payload));
    }

    [Fact]
    public void Request_signkey_matches_reference_implementation()
    {
        var request = Create().BuildRequest("30", "694", Payload, SignKey.NormalizeTag("getLoginInfo"));

        Assert.Equal("da33bb6eb4922396fe24c43658ac9222", request["signkey"]);
        Assert.Equal("v2", request["enc_ver"]);
        Assert.Equal("30", request["type"]);
        Assert.Equal("694", request["client_version"]);
        Assert.Equal("abcdef0123", request["zcid_ext"]);
    }

    [Fact]
    public void Payload_roundtrips()
    {
        ParamsEncryptor enc = Create();
        Assert.Equal(Payload, enc.DecryptResponse(enc.EncryptPayload(Payload)));
    }

    [Fact]
    public void Random_zcid_ext_is_6_to_12_lowercase_hex_chars()
    {
        for (int i = 0; i < 200; i++)
        {
            string ext = new ParamsEncryptor("30", Imei, FirstLaunch).ZcidExt;
            Assert.Matches("^[0-9a-f]{6,12}$", ext);
        }
    }

    [Fact]
    public void Zcid_is_uppercase_hex_and_key_is_32_chars()
    {
        var enc = new ParamsEncryptor("30", Imei, FirstLaunch);
        Assert.Matches("^[0-9A-F]+$", enc.Zcid);
        Assert.Matches("^[0-9A-F]{32}$", enc.EncryptKey);
    }

    [Fact]
    public void Rejects_missing_arguments()
    {
        Assert.Throws<ArgumentException>(() => new ParamsEncryptor("", Imei, FirstLaunch));
        Assert.Throws<ArgumentException>(() => new ParamsEncryptor("30", "", FirstLaunch));
    }
}
