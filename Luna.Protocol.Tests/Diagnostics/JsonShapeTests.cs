using System.Text.Json;
using Xunit;
using Luna.Protocol.Diagnostics;

namespace Luna.Protocol.Tests.Diagnostics;

public class JsonShapeTests
{
    private static string Shape(string json, int maxDepth = 6)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return JsonShape.Describe(doc.RootElement, maxDepth);
    }

    private static JsonElement Parse(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void DescribeEmbeddedOneLine_MultilineObject_BecomesSingleLine()
    {
        string line = JsonShape.DescribeEmbeddedOneLine(Parse("""{"a":1,"b":{"c":"x"}}"""));
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain("  ", line);
        Assert.Contains("a: number", line);
        Assert.Contains("c: string", line);
    }

    [Fact]
    public void DescribeEmbeddedOneLine_LongShape_IsTruncatedWithEllipsis()
    {
        var props = Enumerable.Range(0, 80).Select(i => $"\"field{(char)('a' + i % 26)}{(char)('a' + i / 26)}\":1");
        string line = JsonShape.DescribeEmbeddedOneLine(Parse("{" + string.Join(",", props) + "}"));
        Assert.Equal(303, line.Length);
        Assert.EndsWith("...", line);
    }

    [Fact]
    public void DescribeEmbedded_BracelessString_YieldsKeyNames()
    {
        string text = "\"uid\":\"1\",\"ts\":2";
        JsonElement data = Parse(JsonSerializer.Serialize(text));
        string shape = JsonShape.DescribeEmbedded(data);
        Assert.Contains("uid: string", shape);
        Assert.Contains("ts: number", shape);
    }

    [Fact]
    public void DescribeEmbedded_JsonObjectString_IsParsed()
    {
        JsonElement data = Parse(JsonSerializer.Serialize("""{"groupId":"1","members":[{"id":"2"}]}"""));
        string shape = JsonShape.DescribeEmbedded(data);
        Assert.Contains("groupId: string", shape);
        Assert.Contains("id: string", shape);
    }

    [Fact]
    public void DescribeEmbedded_NotJson_ReturnsUnparsed()
    {
        Assert.Equal("string(unparsed)", JsonShape.DescribeEmbedded(Parse("\"khong phai json\"")));
    }

    [Fact]
    public void DescribeEmbedded_SecretValues_AreNeverPrinted()
    {
        const string uid = "84901234567";
        const string secret = "bi-mat-rat-dai-khong-duoc-lo-ra-ngoai-0123456789abcdef";
        string embedded = "\"uid\":\"" + uid + "\",\"note\":\"" + secret + "\"";
        string input = JsonSerializer.Serialize(embedded);
        // The secret must be PRESENT in the input for "absent from the output" to mean anything.
        Assert.Contains(uid, embedded);
        Assert.Contains(secret, embedded);

        string output = JsonShape.DescribeEmbeddedOneLine(Parse(input));
        Assert.Contains("uid: string", output);
        Assert.DoesNotContain(uid, output);
        Assert.DoesNotContain(secret, output);
    }

    [Theory]
    [InlineData("\"\\ud83d\"")]
    [InlineData("\"\"")]
    [InlineData("\"{\"")]
    [InlineData("\"[\"")]
    public void DescribeEmbedded_EdgeInputs_NeverThrow(string json)
    {
        string shape = JsonShape.DescribeEmbedded(Parse(json));
        Assert.False(string.IsNullOrEmpty(shape));
    }

    [Fact]
    public void DescribeEmbedded_VeryDeepNesting_NeverThrows()
    {
        string deep = new string('[', 200) + new string(']', 200);
        string shape = JsonShape.DescribeEmbedded(Parse(JsonSerializer.Serialize(deep)));
        Assert.False(string.IsNullOrEmpty(shape));
    }
    [Theory]
    [InlineData("group")]
    [InlineData("file_done")]
    [InlineData("a.b-c_1")]
    public void SafeToken_KeepsValidValues(string value)
    {
        Assert.Equal(value, JsonShape.SafeToken(value));
    }

    [Theory]
    [InlineData("Group")]
    [InlineData("nhóm")]
    [InlineData("+84901234567")]
    [InlineData("abcdefghijklmnopqrstuvwxyzabcdefghijklmno")]
    [InlineData("act12345")]
    [InlineData("a1b2c3d4")]
    [InlineData("deadbeefdeadbeef")]
    [InlineData("group\n")]
    [InlineData("")]
    public void SafeToken_ReplacesUnsafeValues(string value)
    {
        // The input must really contain the bad value (except the empty case) for the test to mean anything.
        string json = JsonSerializer.Serialize(new { act = value });
        using JsonDocument doc = JsonDocument.Parse(json);
        string input = doc.RootElement.GetProperty("act").GetString()!;
        Assert.Equal(value, input);
        if (value.Length > 0)
        {
            Assert.NotEmpty(input);
        }

        string output = JsonShape.SafeToken(input);
        Assert.Equal("<v>", output);
        if (value.Length > 0)
        {
            Assert.DoesNotContain(value, output);
        }
    }

    [Fact]
    public void SafeToken_Null_ReturnsPlaceholder()
    {
        Assert.Equal("<v>", JsonShape.SafeToken(null));
    }

    [Fact]
    public void Describe_DoesNotLeakSecretValues()
    {
        string shape = Shape("{\"zpw_sek\":\"SECRET-COOKIE-VALUE\",\"n\":[{\"content\":\"tin nhan bi mat\"}]}");
        Assert.DoesNotContain("SECRET-COOKIE-VALUE", shape);
        Assert.DoesNotContain("tin nhan bi mat", shape);
        Assert.Contains("zpw_sek: string", shape);
    }

    [Fact]
    public void Describe_NestedObject()
    {
        Assert.Equal("{\n  a: {\n    b: string\n  }\n}", Shape("{\"a\":{\"b\":\"x\"}}"));
    }

    [Fact]
    public void Describe_EmptyArrayAndObject()
    {
        Assert.Equal("[]", Shape("[]"));
        Assert.Equal("{}", Shape("{}"));
    }

    [Fact]
    public void Describe_ArrayUsesFirstElementOnly()
    {
        string shape = Shape("[{\"a\":1},{\"b\":\"zzz-second\"}]");
        Assert.Equal("[{\n  a: number\n}]", shape);
        Assert.DoesNotContain("zzz-second", shape);
    }

    [Fact]
    public void Describe_Scalars()
    {
        Assert.Equal("null", Shape("null"));
        Assert.Equal("number", Shape("12345678901234567890"));
        Assert.Equal("boolean", Shape("true"));
        Assert.Equal("boolean", Shape("false"));
        Assert.Equal("string", Shape("\"abc\""));
    }

    [Fact]
    public void Describe_BeyondMaxDepth_ReturnsTypeNameOnly()
    {
        string shape = Shape("{\"a\":{\"b\":{\"secretKey\":\"v\"}},\"c\":[{\"d\":1}]}", maxDepth: 1);
        Assert.Equal("{\n  a: object,\n  c: array\n}", shape);
        Assert.DoesNotContain("secretKey", shape);
    }

    [Fact]
    public void Describe_DefaultElement_DoesNotThrow()
    {
        Assert.Equal("undefined", JsonShape.Describe(default));
    }

    [Fact]
    public void Describe_UnsafeKeys_AreReplacedAndMerged()
    {
        const string Guid = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";
        string json = "{\"9988776655443322\":{\"a\":1},\"1122334455\":{\"a\":2},\"" + Guid + "\":{\"a\":3},"
            + "\"deadbeefdeadbeef\":{\"a\":4},\"data\":{\"a\":5},\"1234\":{\"a\":6}}";
        string shape = Shape(json);
        Assert.DoesNotContain("9988776655443322", shape);
        Assert.DoesNotContain("1122334455", shape);
        Assert.DoesNotContain(Guid, shape);
        Assert.DoesNotContain("deadbeefdeadbeef", shape);
        Assert.DoesNotContain("1234", shape);
        Assert.Contains("data:", shape);
        Assert.Single(shape.Split('\n'), l => l.StartsWith("  <key>:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("u_9988776655")]
    [InlineData("+84901234567")]
    [InlineData("Nguyễn Văn A")]
    [InlineData("abcdefghijklmnopqrstuvwxyzabcdefghijklmn")]
    [InlineData("a-b")]
    public void Describe_UnsafeKey_NeverAppearsInOutput(string key)
    {
        string json = "{\"" + key + "\":{\"a\":1}}";
        Assert.Contains(key, json); // the key must be present in the input data
        string shape = Shape(json);
        Assert.DoesNotContain(key, shape);
        Assert.Contains("<key>:", shape);
        Assert.Equal("<key>", JsonShape.SafeName(key));
    }

    [Theory]
    [InlineData("lastActionId")]
    [InlineData("topOutImprTimeOut")]
    [InlineData("ctrl_qCmds")]
    [InlineData("data")]
    public void Describe_KnownFieldNames_AreKept(string key)
    {
        Assert.Equal(key, JsonShape.SafeName(key));
        Assert.Contains(key + ":", Shape("{\"" + key + "\":1}"));
    }

    [Theory]
    [InlineData("msgId", true)]
    [InlineData("name_of_a_person", true)]
    [InlineData("error_code", true)]
    [InlineData("page2", true)]
    [InlineData("Abcdefghijklmnopqrstuvwx", true)]
    [InlineData("Abcdefghijklmnopqrstuvwxy", false)]
    [InlineData("uid7000001", false)]
    [InlineData("a1b2c3", false)]
    [InlineData("v12", false)]
    [InlineData("_name", false)]
    [InlineData("7days", false)]
    [InlineData("first name", false)]
    [InlineData("first-name", false)]
    [InlineData("chat.photo", false)]
    [InlineData("tên", false)]
    [InlineData("", false)]
    public void SafeNameStrict_keeps_only_short_names_with_at_most_one_digit(string name, bool kept)
    {
        Assert.Equal(kept ? name : "<key>", JsonShape.SafeNameStrict(name));
    }

    [Fact]
    public void Strict_describe_masks_names_that_the_default_mode_keeps()
    {
        JsonElement element = Parse("""{"a1b2c3":1,"msgId":"x","inner":{"k9x8":true}}""");

        string strict = JsonShape.Describe(element, 6, strict: true);
        string normal = JsonShape.Describe(element, 6, strict: false);

        Assert.Contains("<key>: number", strict);
        Assert.Contains("msgId: string", strict);
        Assert.Contains("<key>: boolean", strict);
        Assert.DoesNotContain("a1b2c3", strict);
        Assert.DoesNotContain("k9x8", strict);
        Assert.Contains("a1b2c3: number", normal);
        Assert.Equal(JsonShape.Describe(element), normal);
    }

    [Theory]
    [InlineData(30, false)]
    [InlineData(31, true)]
    [InlineData(40, true)]
    public void Strict_describe_prints_only_the_key_count_of_an_object_with_more_than_thirty_keys(int count, bool collapsed)
    {
        string map = string.Join(",", Enumerable.Range(0, count).Select(i => $"\"fakeword{(char)('a' + i % 26)}{(char)('a' + i / 26)}\":1"));
        JsonElement element = Parse("{\"wrap\":{" + map + "}}");

        string strict = JsonShape.Describe(element, 6, strict: true);

        Assert.Equal(collapsed, strict.Contains($"wrap: object({count} keys)", StringComparison.Ordinal));
        Assert.Equal(!collapsed, strict.Contains("fakeword", StringComparison.Ordinal));
        Assert.Contains("fakeword", JsonShape.Describe(element, 6), StringComparison.Ordinal);
    }
}
