using System.Net;
using System.Text.Json;
using Xunit;
using Luna.Protocol.Api;
using Luna.Protocol.Auth;
using Luna.Protocol.Crypto;
using Luna.Protocol.Tests.Support;

namespace Luna.Protocol.Tests.Api;

public sealed class SendTextTests
{
    // Independent vector produced with crypto-js: key ApiTestKit.FakeEnk, imei ApiTestKit.FakeImei, Clock = 1700000000000 ms.
    private const string ToId = "7000000000000000002";
    private const long FixedClientId = 1700000000000;

    private const string VectorPayload =
        "{\"message\":\"Hello 123\",\"clientId\":1700000000000,\"imei\":\"11111111-2222-3333-4444-555555555555-0123456789abcdef0123456789abcdef\",\"ttl\":0,\"toid\":\"7000000000000000002\"}";

    private const string VectorParams =
        "nJ/I+p3M2QTsgh6qDHUKFNYV9mVWlpm4c4ZjaUSsU8/5LHxtopnMt0z95v413THpX1yVr+WoW8EOL3KmcUjCysmQBx1HSj4LDRAfO/AcSzy4yagoNXSalYLUaZeHg6544IHONpN2V9wqhL5qdn/vMhOAucPLACw1Xz1OzpfJpmtnxojJ2S3DjLCJYPuPrfHD6eFOWFBHDkqSf3xsH2JtpbAg65zOXVHCqVvMkkce7Uo=";

    private const string ResponseNumberMsgId =
        "{\"error_code\":0,\"error_message\":\"Successful.\",\"data\":\"xM8stAIW28htiNwUylMQII+ZEcy5PtoJNUEwlHjMVSjutauKPhispl7C2vGZCv5VeGgBvlSkn135V/yD92hNAl3inSwbP59kgDYw3nJYwfkisMq0JkUXRBwD26PHZebx\"}";

    private const string ResponseStringMsgId =
        "{\"error_code\":0,\"error_message\":\"Successful.\",\"data\":\"xM8stAIW28htiNwUylMQII+ZEcy5PtoJNUEwlHjMVSjutauKPhispl7C2vGZCv5V6eb78y16i3N6dYVAIVKSieK3X1jGiUYWkh7fHa0CYIdttmLfQJX6PHeejZQi9txZ\"}";

    private static LoginInfo ChatLogin(params string[] hosts) =>
        new(
            ApiTestKit.FakeUid,
            ApiTestKit.FakeEnk,
            new Dictionary<string, IReadOnlyList<string>> { ["chat"] = hosts.Length == 0 ? ["https://chat1.zalo.me"] : hosts },
            []);

    private static ApiTestKit.FakeTime FixedTime() =>
        new() { Now = DateTimeOffset.FromUnixTimeMilliseconds(FixedClientId) };

    private static ApiClient NewClient(RoutingHandler handler, ApiTestKit.FakeTime? time = null, params string[] hosts) =>
        new(ApiTestKit.Context(), ChatLogin(hosts), (time ?? FixedTime()).Options(), handler);

    private static string FormParams(string body)
    {
        foreach (string part in body.Split('&'))
        {
            if (part.StartsWith("params=", StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(part["params=".Length..].Replace('+', ' '));
            }
        }

        throw new InvalidOperationException("The form body has no params.");
    }

    private static string DecryptedPayload(RoutingHandler handler) =>
        new SessionCipher(ApiTestKit.FakeEnk).Decrypt(FormParams(handler.Calls[0].Body));

    [Fact]
    public async Task Matches_independent_crypto_js_vector_with_numeric_msgId()
    {
        var handler = new RoutingHandler((_, _) => ResponseNumberMsgId);
        using ApiClient client = NewClient(handler);

        SentTextResult result = await client.SendTextAsync(ToId, "Hello 123");

        Assert.Equal("8000000000000000001", result.MsgId);
        Assert.Equal(FixedClientId, result.ClientId);
        (HttpMethod method, Uri uri, string body) = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("https://chat1.zalo.me/api/message/sms?nretry=0&zpw_ver=694&zpw_type=30", uri.ToString());
        Assert.DoesNotContain("signkey", uri.Query);
        Assert.Equal(VectorPayload, DecryptedPayload(handler));
        Assert.Equal(VectorParams, FormParams(body));
    }

    [Fact]
    public async Task Reads_string_msgId_verbatim()
    {
        var handler = new RoutingHandler((_, _) => ResponseStringMsgId);
        using ApiClient client = NewClient(handler);

        SentTextResult result = await client.SendTextAsync(ToId, "Hello 123");

        Assert.Equal("8000000000000000001", result.MsgId);
    }

    [Theory]
    [InlineData("{\"error_code\":0}")]
    [InlineData("{\"error_code\":0,\"data\":{}}")]
    [InlineData("{\"error_code\":0,\"data\":null}")]
    [InlineData("{\"error_code\":0,\"data\":{\"msgId\":{\"a\":1}}}")]
    [InlineData("{\"error_code\":0,\"data\":[]}")]
    public async Task Missing_data_or_msgId_still_succeeds_with_null_MsgId(string inner)
    {
        var handler = new RoutingHandler((_, _) => ApiTestKit.Envelope(inner));
        using ApiClient client = NewClient(handler);

        SentTextResult result = await client.SendTextAsync(ToId, "hi");

        Assert.Null(result.MsgId);
        Assert.Equal(FixedClientId, result.ClientId);
    }

    [Fact]
    public async Task Vietnamese_text_emoji_and_newlines_round_trip()
    {
        const string Text = "Xin chào, Việt Nam 🇻🇳 😀\r\ndòng hai\ttab \"quote\" \\ <b>&</b>";
        var handler = new RoutingHandler((_, _) => ApiTestKit.Envelope("{\"error_code\":0,\"data\":{\"msgId\":1}}"));
        using ApiClient client = NewClient(handler);

        await client.SendTextAsync(ToId, Text);

        using JsonDocument doc = JsonDocument.Parse(DecryptedPayload(handler));
        Assert.Equal(Text, doc.RootElement.GetProperty("message").GetString());
        Assert.Equal(ToId, doc.RootElement.GetProperty("toid").GetString());
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\r\nb")]
    [InlineData("a\tb")]
    public async Task Allows_LF_CR_and_tab(string text)
    {
        var handler = new RoutingHandler((_, _) => ApiTestKit.Envelope("{\"error_code\":0,\"data\":{}}"));
        using ApiClient client = NewClient(handler);

        await client.SendTextAsync(ToId, text);

        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Accepts_text_of_exactly_2000_utf16_units()
    {
        var handler = new RoutingHandler((_, _) => ApiTestKit.Envelope("{\"error_code\":0,\"data\":{}}"));
        using ApiClient client = NewClient(handler);

        await client.SendTextAsync(ToId, new string('a', 2000));

        Assert.Single(handler.Calls);
    }

    public static TheoryData<string, string> InvalidInputs => new()
    {
        { "", "hi" },
        { new string('1', 65), "hi" },
        { "abc def", "hi" },
        { "abc/def", "hi" },
        { "abc.def", "hi" },
        { "tên", "hi" },
        { "１２３", "hi" }, // full-width digits are not ASCII
        { ToId, "" },
        { ToId, new string('a', 2001) },
        { ToId, "a\0b" },
        { ToId, "a\u001bb" },
        { ToId, "a\u007fb" },
        { ToId, "a\u0085b" },
    };

    [Theory]
    [MemberData(nameof(InvalidInputs))]
    public async Task Invalid_input_throws_ArgumentException_without_sending(string toId, string text)
    {
        var handler = new RoutingHandler((_, _) => ApiTestKit.Envelope("{\"error_code\":0,\"data\":{}}"));
        using ApiClient client = NewClient(handler);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.SendTextAsync(toId, text));

        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Lone_surrogates_throw_without_sending()
    {
        // Not a Theory: xUnit replaces a lone surrogate with U+FFFD when serializing test data.
        var handler = new RoutingHandler((_, _) => ApiTestKit.Envelope("{\"error_code\":0,\"data\":{}}"));
        using ApiClient client = NewClient(handler);

        foreach (string text in new[] { "x\ud800y", "x\udc00y", "x\ud800", "\ud83d", "\udc00\ud83d" })
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(() => client.SendTextAsync(ToId, text));
        }

        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Null_arguments_throw_without_sending()
    {
        var handler = new RoutingHandler((_, _) => "x");
        using ApiClient client = NewClient(handler);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.SendTextAsync(null!, "hi"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.SendTextAsync(ToId, null!));

        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Accepts_toId_of_exactly_64_valid_characters()
    {
        var handler = new RoutingHandler((_, _) => ApiTestKit.Envelope("{\"error_code\":0,\"data\":{}}"));
        using ApiClient client = NewClient(handler);

        await client.SendTextAsync("aZ09_-" + new string('x', 58), "hi");

        Assert.Single(handler.Calls);
    }

    [Theory]
    [InlineData("{\"error_code\":112,\"error_message\":\"m\"}", 112)]
    [InlineData("{\"error_code\":-5}", -5)]
    public async Task Inner_error_code_throws_ApiException(string inner, int code)
    {
        var handler = new RoutingHandler((_, _) => ApiTestKit.Envelope(inner));
        using ApiClient client = NewClient(handler);

        ApiException ex = await Assert.ThrowsAsync<ApiException>(() => client.SendTextAsync(ToId, "hi"));

        Assert.Equal(code, ex.ErrorCode);
    }

    [Fact]
    public async Task Outer_error_code_throws_ApiException()
    {
        var handler = new RoutingHandler((_, _) => "{\"error_code\":114,\"error_message\":\"m\"}");
        using ApiClient client = NewClient(handler);

        ApiException ex = await Assert.ThrowsAsync<ApiException>(() => client.SendTextAsync(ToId, "hi"));

        Assert.Equal(114, ex.ErrorCode);
    }

    [Fact]
    public async Task Sends_exactly_one_request_even_with_many_hosts_and_on_failure()
    {
        var handler = new RoutingHandler((_, _) => throw new HttpRequestException("boom"));
        using ApiClient client = NewClient(
            handler, hosts: ["https://chat1.zalo.me", "https://chat2.zalo.me", "https://chat3.zalo.me"]);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendTextAsync(ToId, "hi"));

        Assert.Single(handler.Calls); // RoutingHandler records the request before throwing.
    }

    [Fact]
    public async Task Uses_first_host_only_and_second_send_within_60s_is_blocked_by_default_options()
    {
        var handler = new RoutingHandler((_, _) => ApiTestKit.Envelope("{\"error_code\":0,\"data\":{}}"));
        var time = FixedTime();
        using var client = new ApiClient(
            ApiTestKit.Context(),
            ChatLogin("https://chat1.zalo.me", "https://chat2.zalo.me"),
            new ApiClientOptions { Clock = () => time.Now, Delay = (_, _) => Task.CompletedTask },
            handler);

        await client.SendTextAsync(ToId, "one");
        await Assert.ThrowsAsync<RateLimitedException>(() => client.SendTextAsync(ToId, "two"));

        Assert.Single(handler.Calls);
        Assert.Equal("chat1.zalo.me", handler.Calls[0].Uri.Host);
    }

    [Fact]
    public void SentTextResult_ToString_prints_neither_msgId_nor_clientId()
    {
        var result = new SentTextResult("8000000000000000001", 1700000000000);

        string text = result.ToString();

        Assert.DoesNotContain("8000000000000000001", text);
        Assert.DoesNotContain("1700000000000", text);
    }

    [Fact]
    public async Task Trace_and_errors_do_not_contain_text_toId_or_clientId()
    {
        const string Secret = "SECRET-MESSAGE-424242";
        var traces = new List<string>();
        var handler = new RoutingHandler((_, _) => throw new HttpRequestException("boom"));
        var time = FixedTime();
        using var client = new ApiClient(ApiTestKit.Context(), ChatLogin(), time.Options(traces.Add), handler);

        Exception ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.SendTextAsync(ToId, Secret));

        Assert.NotEmpty(traces);
        foreach (string text in traces.Append(ex.ToString()))
        {
            Assert.DoesNotContain(Secret, text);
            Assert.DoesNotContain(ToId, text);
            Assert.DoesNotContain("1700000000000", text);
        }
    }
}
