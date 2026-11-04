using Xunit;
using Luna.Protocol.Crypto;

namespace Luna.Protocol.Tests.Crypto;

public class SignKeyTests
{
    [Fact]
    public void Sorts_keys_and_concatenates_values()
    {
        // Deliberately passed in a scrambled order: the result must follow the sorted key-name order.
        var parameters = new Dictionary<string, string>
        {
            ["zcid"] = "A",
            ["enc_ver"] = "v2",
            ["type"] = "30",
            ["params"] = "P",
            ["client_version"] = "693",
            ["zcid_ext"] = "B",
        };

        // md5("zsecureTAG693v2P30AB"), computed independently with Python.
        Assert.Equal("c1c5979a8fb4574016d39ac7e59b7b26", SignKey.Compute("TAG", parameters));
    }

    [Fact]
    public void Normalizes_tag_to_lowercase_without_dashes()
    {
        Assert.Equal("getlogininfo", SignKey.NormalizeTag("getLoginInfo"));
        Assert.Equal("getlastmsgs", SignKey.NormalizeTag("get-last-msgs"));
    }
}
