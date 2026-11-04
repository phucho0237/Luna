using System.Net;
using Xunit;
using Luna.Protocol.Api;
using Luna.Protocol.Crypto;
using Luna.Protocol.Tests.Support;

namespace Luna.Protocol.Tests.Api;

/// <summary>The WRITE path of ApiClient: exactly one attempt, no failover, no retry.</summary>
public sealed class PostEncryptedTests
{
    private const string Path = "/api/test/write";
    private static readonly TimeSpan Sixty = TimeSpan.FromSeconds(60);

    private static IReadOnlyDictionary<string, TimeSpan> Intervals => new Dictionary<string, TimeSpan> { [Path] = Sixty };

    private static ApiClient NewClient(
        RoutingHandler handler,
        ApiTestKit.FakeTime time,
        Action<string>? trace = null,
        IReadOnlyDictionary<string, TimeSpan>? intervals = null,
        params string[] hosts) =>
        new(
            ApiTestKit.Context(),
            ApiTestKit.Login(hosts.Length == 0 ? ["https://h1.zalo.me", "https://h2.zalo.me", "https://h3.zalo.me"] : hosts),
            time.Options(trace, endpointIntervals: intervals),
            handler);

    private static Task<System.Text.Json.JsonElement> Post(ApiClient client, string payload = "{}", CancellationToken ct = default) =>
        client.PostEncryptedAsync("profile", Path, payload, [new("nretry", "0")], ct);

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

    [Fact]
    public async Task Post_sends_encrypted_params_in_form_body_to_first_host_without_signkey()
    {
        var handler = new RoutingHandler((_, _) => ApiTestKit.OkEnvelope("{\"k\":1}"));
        using ApiClient client = NewClient(handler, new ApiTestKit.FakeTime());

        System.Text.Json.JsonElement data = await Post(client, "{\"a\":\"b\"}");

        Assert.Equal(1, data.GetProperty("k").GetInt32());
        (HttpMethod method, Uri uri, string body) = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal("https://h1.zalo.me/api/test/write?nretry=0&zpw_ver=694&zpw_type=30", uri.ToString());
        Assert.DoesNotContain("signkey", uri.Query);
        Assert.DoesNotContain("params", uri.Query);
        Assert.Equal("{\"a\":\"b\"}", new SessionCipher(ApiTestKit.FakeEnk).Decrypt(FormParams(body)));
    }

    public static TheoryData<string> FailureKinds => new() { "network", "500", "503", "timeout" };

    [Theory]
    [MemberData(nameof(FailureKinds))]
    public async Task Post_failure_sends_exactly_one_request_and_rethrows_as_is(string kind)
    {
        int sent = 0;
        var handler = new CountingThrowHandler(kind, () => sent++);
        using var client = new ApiClient(
            ApiTestKit.Context(),
            ApiTestKit.Login("https://h1.zalo.me", "https://h2.zalo.me", "https://h3.zalo.me"),
            new ApiTestKit.FakeTime().Options(),
            handler);

        Exception ex = await Assert.ThrowsAnyAsync<Exception>(() => Post(client));

        Assert.Equal(1, sent);
        switch (kind)
        {
            case "timeout":
                Assert.IsType<TaskCanceledException>(ex);
                break;
            case "network":
                Assert.Null(Assert.IsType<HttpRequestException>(ex).StatusCode);
                break;
            default:
                Assert.Equal(int.Parse(kind), (int?)Assert.IsType<HttpRequestException>(ex).StatusCode);
                break;
        }
    }

    private sealed class CountingThrowHandler(string kind, Action onSend) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onSend();
            return kind switch
            {
                "network" => throw new HttpRequestException("boom"),
                "timeout" => throw new TaskCanceledException("timeout"),
                _ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)int.Parse(kind)) { Content = new StringContent("x") }),
            };
        }
    }

    [Fact]
    public async Task Post_429_starts_cooldown_and_blocks_next_call_without_sending()
    {
        var handler = new RoutingHandler((_, _) => "x") { Decorate = (_, r) => r.StatusCode = HttpStatusCode.TooManyRequests };
        using ApiClient client = NewClient(handler, new ApiTestKit.FakeTime());

        await Assert.ThrowsAsync<RateLimitedException>(() => Post(client));
        await Assert.ThrowsAsync<RateLimitedException>(() => client.GetEncryptedAsync("profile", "/other", "{}"));

        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Post_second_call_within_interval_throws_without_sending_then_allowed_after()
    {
        var handler = new RoutingHandler((_, _) => ApiTestKit.OkEnvelope("{}"));
        var time = new ApiTestKit.FakeTime();
        using ApiClient client = NewClient(handler, time, intervals: Intervals);

        await Post(client);
        time.Now += TimeSpan.FromSeconds(59);
        await Assert.ThrowsAsync<RateLimitedException>(() => Post(client));
        Assert.Single(handler.Calls);

        time.Now += TimeSpan.FromSeconds(1);
        await Post(client);
        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task Post_failed_call_still_counts_for_endpoint_interval()
    {
        var handler = new RoutingHandler((_, _) => throw new HttpRequestException("boom"));
        using ApiClient client = NewClient(handler, new ApiTestKit.FakeTime(), intervals: Intervals);

        await Assert.ThrowsAsync<HttpRequestException>(() => Post(client));
        await Assert.ThrowsAsync<RateLimitedException>(() => Post(client));
    }

    [Fact]
    public void Default_options_limit_sms_endpoint_to_one_call_per_60_seconds()
    {
        var options = new ApiClientOptions();

        Assert.Equal(Sixty, options.EndpointMinIntervals["/api/message/sms"]);
        Assert.Equal(TimeSpan.FromMinutes(10), options.EndpointMinIntervals["/api/social/friend/getfriends"]);
    }

    [Fact]
    public async Task Post_rejects_untrusted_host_before_sending()
    {
        var handler = new RoutingHandler((_, _) => ApiTestKit.OkEnvelope("{}"));
        using ApiClient client = NewClient(
            handler, new ApiTestKit.FakeTime(), hosts: ["https://h1.zalo.me", "https://evil.example.com"]);

        await Assert.ThrowsAsync<InvalidDataException>(() => Post(client));

        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Post_honors_cancellation_before_sending()
    {
        var handler = new RoutingHandler((_, _) => ApiTestKit.OkEnvelope("{}"));
        using ApiClient client = NewClient(handler, new ApiTestKit.FakeTime());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Post(client, ct: cts.Token));

        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Post_throws_ApiException_for_outer_and_inner_error_codes()
    {
        string inner = ApiTestKit.Envelope("{\"error_code\":112,\"error_message\":\"m\"}");
        string outer = "{\"error_code\":7,\"error_message\":\"m\"}";
        foreach ((string body, int code) in new[] { (inner, 112), (outer, 7) })
        {
            var handler = new RoutingHandler((_, _) => body);
            using ApiClient client = NewClient(handler, new ApiTestKit.FakeTime());

            ApiException ex = await Assert.ThrowsAsync<ApiException>(() => Post(client));

            Assert.Equal(code, ex.ErrorCode);
        }
    }

    [Fact]
    public async Task Post_errors_and_trace_never_expose_params_key_or_payload()
    {
        const string Secret = "SECRET-MESSAGE-424242";
        string payload = $"{{\"message\":\"{Secret}\",\"toid\":\"7000000000000000002\",\"clientId\":1700000000000}}";
        var traces = new List<string>();
        var failures = new List<Exception>();

        foreach (string kind in new[] { "network", "500", "timeout", "429", "bad-json" })
        {
            var handler = new RoutingHandler((_, _) => kind switch
            {
                "network" => throw new HttpRequestException("boom"),
                "timeout" => throw new TaskCanceledException("timeout"),
                "bad-json" => "not json",
                _ => "x",
            });
            handler.Decorate = (_, r) =>
            {
                r.StatusCode = kind switch { "500" => HttpStatusCode.InternalServerError, "429" => HttpStatusCode.TooManyRequests, _ => r.StatusCode };
            };
            using ApiClient client = NewClient(handler, new ApiTestKit.FakeTime(), traces.Add);
            failures.Add(await Assert.ThrowsAnyAsync<Exception>(() => Post(client, payload)));

            // The fake value is PRESENT in the input: assert that it really went into the request body or at least into the encryptor.
            Assert.Contains(Secret, payload);
        }

        string encrypted = new SessionCipher(ApiTestKit.FakeEnk).Encrypt(payload);
        foreach (string text in failures.Select(f => f.ToString()).Concat(failures.Select(f => f.Message)).Concat(traces))
        {
            Assert.DoesNotContain(Secret, text);
            Assert.DoesNotContain("7000000000000000002", text);
            Assert.DoesNotContain("1700000000000", text);
            Assert.DoesNotContain(ApiTestKit.FakeEnk, text);
            Assert.DoesNotContain("params=", text);
            Assert.DoesNotContain(encrypted, text);
        }

        Assert.NotEmpty(traces);
        Assert.All(traces, t => Assert.Matches(@"^api profile /api/test/write host#0 attempt 1/1$", t));
    }
}
