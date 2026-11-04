using System.Net;
using System.Text.Json;
using Xunit;
using Luna.Protocol.Api;
using Luna.Protocol.Tests.Support;

namespace Luna.Protocol.Tests.Api;

public sealed class ApiClientTests
{
    private const string Path = "/api/test/path";

    // Vector produced with crypto-js (the library zca-js uses) with the fake key FakeEnk. Decrypted data: an array of 1 element with userId "9999".
    private const string VectorData =
        "xM8stAIW28htiNwUylMQII+ZEcy5PtoJNUEwlHjMVSjutauKPhispl7C2vGZCv5VDzPA8W4bsG3AgQ9ZpGYI5qs8sBVQ1LmV/PCHHGTJJz25nwgMqbBlCqm76fPnP9LXSyO4KuUhxHlJIFym4NCB6A==";

    private const string VectorDataUrlEncoded =
        "xM8stAIW28htiNwUylMQII%2BZEcy5PtoJNUEwlHjMVSjutauKPhispl7C2vGZCv5VDzPA8W4bsG3AgQ9ZpGYI5qs8sBVQ1LmV%2FPCHHGTJJz25nwgMqbBlCqm76fPnP9LXSyO4KuUhxHlJIFym4NCB6A%3D%3D";

    private static ApiClient NewClient(RoutingHandler handler, ApiTestKit.FakeTime time, params string[] hosts) =>
        new(ApiTestKit.Context(), ApiTestKit.Login(hosts), time.Options(), handler);

    private static Task<JsonElement> Call(ApiClient client, CancellationToken ct = default) =>
        client.GetEncryptedAsync("profile", Path, "{}", ct);

    [Theory]
    [InlineData(VectorData)]
    [InlineData(VectorDataUrlEncoded)]
    public async Task Decrypts_reference_vector(string data)
    {
        var handler = new RoutingHandler((_, _) => $"{{\"error_code\":0,\"error_message\":\"Successful.\",\"data\":\"{data}\"}}");
        using ApiClient client = NewClient(handler, new ApiTestKit.FakeTime(), "https://h1.zalo.me");

        JsonElement result = await Call(client);

        Assert.Equal(JsonValueKind.Array, result.ValueKind);
        Assert.Equal(1, result.GetArrayLength());
        Assert.Equal("9999", result[0].GetProperty("userId").GetString());
    }

    [Fact]
    public async Task Builds_url_with_params_version_and_no_signkey()
    {
        var handler = new RoutingHandler((_, _) => ApiTestKit.OkEnvelope());
        var context = ApiTestKit.Context();
        using var client = new ApiClient(context, ApiTestKit.Login("https://h1.zalo.me/"), new ApiTestKit.FakeTime().Options(), handler);

        await Call(client);

        Uri uri = Assert.Single(handler.Calls).Uri;
        Assert.Equal("https://h1.zalo.me" + Path, uri.GetLeftPart(UriPartial.Path));
        Assert.Contains("params=", uri.Query, StringComparison.Ordinal);
        Assert.Contains($"zpw_ver={context.ApiVersion}", uri.Query, StringComparison.Ordinal);
        Assert.Contains($"zpw_type={context.ApiType}", uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("signkey", uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_or_empty_service_throws_InvalidData()
    {
        var handler = new RoutingHandler((_, _) => ApiTestKit.OkEnvelope());
        using var empty = new ApiClient(
            ApiTestKit.Context(), ApiTestKit.Login(), new ApiTestKit.FakeTime().Options(), handler);
        using ApiClient other = NewClient(handler, new ApiTestKit.FakeTime(), "https://h1.zalo.me");

        await Assert.ThrowsAsync<InvalidDataException>(() => Call(empty));
        await Assert.ThrowsAsync<InvalidDataException>(() => other.GetEncryptedAsync("nope", Path, "{}"));
        Assert.Empty(handler.Calls);
    }

    [Theory]
    [InlineData("{\"error_code\":5,\"error_message\":\"loi\"}", 5)]
    [InlineData("{\"error_code\":\"7\",\"error_message\":\"loi\"}", 7)]
    [InlineData("{\"error_code\":-1}", -1)]
    public async Task Outer_error_code_throws_ApiException(string body, int expected)
    {
        var handler = new RoutingHandler((_, _) => body);
        using ApiClient client = NewClient(handler, new ApiTestKit.FakeTime(), "https://h1.zalo.me");

        var ex = await Assert.ThrowsAsync<ApiException>(() => Call(client));

        Assert.Equal(expected, ex.ErrorCode);
    }

    [Fact]
    public async Task Inner_error_code_throws_ApiException()
    {
        var handler = new RoutingHandler((_, _) => ApiTestKit.Envelope("{\"error_code\":114,\"error_message\":\"inner\"}"));
        using ApiClient client = NewClient(handler, new ApiTestKit.FakeTime(), "https://h1.zalo.me");

        var ex = await Assert.ThrowsAsync<ApiException>(() => Call(client));

        Assert.Equal(114, ex.ErrorCode);
        Assert.Equal("inner", ex.ServerMessage);
    }

    [Fact]
    public async Task Inner_without_error_code_throws_InvalidData()
    {
        var handler = new RoutingHandler((_, _) => ApiTestKit.Envelope("{\"data\":{\"a\":1}}"));
        using ApiClient client = NewClient(handler, new ApiTestKit.FakeTime(), "https://h1.zalo.me");

        await Assert.ThrowsAsync<InvalidDataException>(() => Call(client));
    }

    [Theory]
    [InlineData("{\"error_message\":\"thieu code\"}")]
    [InlineData("{\"error_code\":null}")]
    [InlineData("{\"error_code\":1.5}")]
    [InlineData("{\"error_code\":\"abc\"}")]
    [InlineData("{\"error_code\":0}")]
    [InlineData("{\"error_code\":0,\"data\":123}")]
    [InlineData("{\"error_code\":0,\"data\":null}")]
    [InlineData("{\"error_code\":0,\"data\":{\"x\":1}}")]
    [InlineData("{\"error_code\":0,\"data\":\"!!!khong-phai-base64\"}")]
    [InlineData("{\"error_code\":0,\"data\":\"AAAAAAAAAAAAAAAAAAAAAA==\"}")]
    [InlineData("{\"error_code\":0,\"data\":\"AAAA\"}")]
    [InlineData("{\"error_code\":0,\"data\":\"\"}")]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"chuoi\"")]
    public async Task Malformed_responses_throw_InvalidData_never_NullReference(string body)
    {
        var handler = new RoutingHandler((_, _) => body);
        using ApiClient client = NewClient(handler, new ApiTestKit.FakeTime(), "https://h1.zalo.me");

        await Assert.ThrowsAsync<InvalidDataException>(() => Call(client));
    }

    [Theory]
    [InlineData("{\"error_code\":0}")]
    [InlineData("[1,2]")]
    [InlineData("\"x\"")]
    [InlineData("not json")]
    public async Task Malformed_decrypted_payload_throws_InvalidData(string inner)
    {
        var handler = new RoutingHandler((_, _) => ApiTestKit.Envelope(inner));
        using ApiClient client = NewClient(handler, new ApiTestKit.FakeTime(), "https://h1.zalo.me");

        await Assert.ThrowsAsync<InvalidDataException>(() => Call(client));
    }

    [Theory]
    [InlineData("http://profile.chat.zalo.me")]
    [InlineData("https://evil.test")]
    [InlineData("https://evil-zalo.me.attacker.test")]
    [InlineData("https://zalo.me.attacker.test")]
    [InlineData("https://evilzalo.me")]
    [InlineData("https://a@evil.test")]
    [InlineData("https://a@profile.chat.zalo.me")]
    [InlineData("profile.chat.zalo.me")]
    [InlineData("")]
    public async Task Untrusted_host_throws_InvalidData_without_any_request(string badHost)
    {
        var handler = new RoutingHandler((_, _) => ApiTestKit.OkEnvelope());
        // Bad host in the second position: it must still be blocked before the attempt loop, no failover.
        using ApiClient client = NewClient(handler, new ApiTestKit.FakeTime(), "https://h1.zalo.me", badHost);

        await Assert.ThrowsAsync<InvalidDataException>(() => Call(client));

        Assert.Empty(handler.Calls);
    }

    [Theory]
    [InlineData("https://profile.chat.zalo.me")]
    [InlineData("https://x.zaloapp.com")]
    [InlineData("https://zalo.me")]
    [InlineData("https://zaloapp.com")]
    public async Task Trusted_host_is_used(string host)
    {
        var handler = new RoutingHandler((_, _) => ApiTestKit.OkEnvelope());
        using ApiClient client = NewClient(handler, new ApiTestKit.FakeTime(), host);

        await Call(client);

        Assert.Single(handler.Calls);
    }

    [Theory]
    [InlineData("{\"error_code\":0,\"data\":\"\\ud83d\"}")]
    [InlineData("{\"error_code\":\"\\ud83d\"}")]
    [InlineData("{\"error_code\":5,\"error_message\":\"\\ud83d\"}")]
    public async Task Lone_surrogate_strings_throw_intentional_exception_never_InvalidOperation(string body)
    {
        var handler = new RoutingHandler((_, _) => body);
        using ApiClient client = NewClient(handler, new ApiTestKit.FakeTime(), "https://h1.zalo.me");

        Exception ex = await Record.ExceptionAsync(() => Call(client));

        Assert.True(ex is InvalidDataException or ApiException, $"Actual exception type: {ex?.GetType().Name}");
    }

    [Fact]
    public async Task Outer_error_with_corrupt_message_keeps_server_error_code()
    {
        var handler = new RoutingHandler((_, _) => "{\"error_code\":5,\"error_message\":\"\\ud83d\"}");
        using ApiClient client = NewClient(handler, new ApiTestKit.FakeTime(), "https://h1.zalo.me");

        var ex = await Assert.ThrowsAsync<ApiException>(() => Call(client));

        Assert.Equal(5, ex.ErrorCode);
        Assert.Null(ex.ServerMessage);
    }

    [Fact]
    public async Task Inner_error_with_corrupt_message_keeps_server_error_code()
    {
        var handler = new RoutingHandler((_, _) => ApiTestKit.Envelope("{\"error_code\":114,\"error_message\":\"\\ud83d\"}"));
        using ApiClient client = NewClient(handler, new ApiTestKit.FakeTime(), "https://h1.zalo.me");

        var ex = await Assert.ThrowsAsync<ApiException>(() => Call(client));

        Assert.Equal(114, ex.ErrorCode);
        Assert.Null(ex.ServerMessage);
    }

    [Fact]
    public async Task Corrupt_string_error_code_is_still_InvalidData()
    {
        var handler = new RoutingHandler((_, _) => "{\"error_code\":\"\\ud83d\",\"error_message\":\"x\"}");
        using ApiClient client = NewClient(handler, new ApiTestKit.FakeTime(), "https://h1.zalo.me");

        await Assert.ThrowsAsync<InvalidDataException>(() => Call(client));
    }

    [Fact]
    public async Task Network_error_fails_over_to_next_host()
    {
        var handler = new RoutingHandler((req, _) => req.RequestUri!.Host == "h1.zalo.me"
            ? throw new HttpRequestException("mang loi")
            : ApiTestKit.OkEnvelope("[7]"));
        using ApiClient client = NewClient(handler, new ApiTestKit.FakeTime(), "https://h1.zalo.me", "https://h2.zalo.me");

        JsonElement result = await Call(client);

        Assert.Equal(7, result[0].GetInt32());
        Assert.Equal(["h1.zalo.me", "h2.zalo.me"], handler.Calls.Select(c => c.Uri.Host));
    }

    [Fact]
    public async Task Timeout_fails_over_to_next_host()
    {
        var handler = new RoutingHandler((req, _) => req.RequestUri!.Host == "h1.zalo.me"
            ? throw new TaskCanceledException("het gio")
            : ApiTestKit.OkEnvelope());
        using ApiClient client = NewClient(handler, new ApiTestKit.FakeTime(), "https://h1.zalo.me", "https://h2.zalo.me");

        await Call(client);

        Assert.Equal(2, handler.Calls.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Http_5xx_fails_over_to_next_host(HttpStatusCode status)
    {
        var handler = new RoutingHandler((_, _) => ApiTestKit.OkEnvelope());
        handler.Decorate = (req, resp) =>
        {
            if (req.RequestUri!.Host == "h1.zalo.me")
            {
                resp.StatusCode = status;
            }
        };
        using ApiClient client = NewClient(handler, new ApiTestKit.FakeTime(), "https://h1.zalo.me", "https://h2.zalo.me");

        await Call(client);

        Assert.Equal(2, handler.Calls.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Http_4xx_throws_immediately_without_failover(HttpStatusCode status)
    {
        var handler = new RoutingHandler((_, _) => ApiTestKit.OkEnvelope());
        handler.Decorate = (_, resp) => resp.StatusCode = status;
        using ApiClient client = NewClient(handler, new ApiTestKit.FakeTime(), "https://h1.zalo.me", "https://h2.zalo.me");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Call(client));

        Assert.Equal(status, ex.StatusCode);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Http_429_throws_rate_limited_after_single_call()
    {
        var handler = new RoutingHandler((_, _) => ApiTestKit.OkEnvelope());
        handler.Decorate = (_, resp) => resp.StatusCode = HttpStatusCode.TooManyRequests;
        using ApiClient client = NewClient(handler, new ApiTestKit.FakeTime(), "https://h1.zalo.me", "https://h2.zalo.me");

        await Assert.ThrowsAsync<RateLimitedException>(() => Call(client));

        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Api_error_does_not_fail_over()
    {
        var handler = new RoutingHandler((_, _) => "{\"error_code\":9,\"error_message\":\"x\"}");
        using ApiClient client = NewClient(handler, new ApiTestKit.FakeTime(), "https://h1.zalo.me", "https://h2.zalo.me");

        await Assert.ThrowsAsync<ApiException>(() => Call(client));

        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task All_hosts_failing_throws_last_error()
    {
        int n = 0;
        var handler = new RoutingHandler((_, _) => throw new HttpRequestException($"loi-{++n}"));
        using ApiClient client = NewClient(
            handler, new ApiTestKit.FakeTime(), "https://h1.zalo.me", "https://h2.zalo.me", "https://h3.zalo.me");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Call(client));

        Assert.Equal("loi-3", ex.Message);
        Assert.Equal(3, handler.Calls.Count);
    }

    [Fact]
    public async Task MaxHostAttempts_limits_failover()
    {
        var handler = new RoutingHandler((_, _) => throw new HttpRequestException("loi"));
        var time = new ApiTestKit.FakeTime();
        using var client = new ApiClient(
            ApiTestKit.Context(),
            ApiTestKit.Login("https://h1.zalo.me", "https://h2.zalo.me", "https://h3.zalo.me"),
            time.Options(maxHostAttempts: 2),
            handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => Call(client));

        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public void Default_MinInterval_is_3_seconds()
    {
        // [Live] three requests 1 second apart get a 429 on the third request.
        Assert.Equal(TimeSpan.FromSeconds(3), new ApiClientOptions().MinInterval);
    }

    [Fact]
    public async Task Second_call_waits_MinInterval_and_first_does_not()
    {
        var handler = new RoutingHandler((_, _) => ApiTestKit.OkEnvelope());
        var time = new ApiTestKit.FakeTime();
        using ApiClient client = NewClient(handler, time, "https://h1.zalo.me");

        await Call(client);
        Assert.Empty(time.Delays);

        await Call(client);
        Assert.Equal([TimeSpan.FromSeconds(1)], time.Delays);
    }

    [Fact]
    public async Task No_wait_when_interval_already_elapsed()
    {
        var handler = new RoutingHandler((_, _) => ApiTestKit.OkEnvelope());
        var time = new ApiTestKit.FakeTime();
        using ApiClient client = NewClient(handler, time, "https://h1.zalo.me");

        await Call(client);
        time.Now += TimeSpan.FromSeconds(5);
        await Call(client);

        Assert.Empty(time.Delays);
    }

    [Fact]
    public async Task Partial_wait_is_the_remaining_interval()
    {
        var handler = new RoutingHandler((_, _) => ApiTestKit.OkEnvelope());
        var time = new ApiTestKit.FakeTime();
        using ApiClient client = NewClient(handler, time, "https://h1.zalo.me");

        await Call(client);
        time.Now += TimeSpan.FromMilliseconds(400);
        await Call(client);

        Assert.Equal([TimeSpan.FromMilliseconds(600)], time.Delays);
    }

    [Fact]
    public async Task Wait_is_capped_at_MinInterval_when_clock_goes_backwards()
    {
        var handler = new RoutingHandler((_, _) => ApiTestKit.OkEnvelope());
        var time = new ApiTestKit.FakeTime();
        using ApiClient client = NewClient(handler, time, "https://h1.zalo.me");

        await Call(client);
        time.Now -= TimeSpan.FromHours(1);
        await Call(client);

        TimeSpan delay = Assert.Single(time.Delays);
        Assert.True(delay <= TimeSpan.FromSeconds(1), $"Actual delay: {delay}");
    }

    [Fact]
    public async Task Failover_attempt_also_goes_through_rate_limiter()
    {
        var handler = new RoutingHandler((req, _) => req.RequestUri!.Host == "h1.zalo.me"
            ? throw new HttpRequestException("mang loi")
            : ApiTestKit.OkEnvelope());
        var time = new ApiTestKit.FakeTime();
        using ApiClient client = NewClient(handler, time, "https://h1.zalo.me", "https://h2.zalo.me");

        await Call(client);

        Assert.Equal(2, handler.Calls.Count);
        Assert.Equal([TimeSpan.FromSeconds(1)], time.Delays);
    }

    [Fact]
    public async Task Already_cancelled_token_throws_OperationCanceled_without_calling()
    {
        var handler = new RoutingHandler((_, _) => ApiTestKit.OkEnvelope());
        using ApiClient client = NewClient(handler, new ApiTestKit.FakeTime(), "https://h1.zalo.me");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Call(client, cts.Token));

        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Cancel_while_waiting_releases_the_gate()
    {
        var handler = new RoutingHandler((_, _) => ApiTestKit.OkEnvelope());
        var time = new ApiTestKit.FakeTime();
        using ApiClient client = NewClient(handler, time, "https://h1.zalo.me");
        await Call(client);

        using var cts = new CancellationTokenSource();
        time.DelayOverride = async (_, token) =>
        {
            await cts.CancelAsync();
            token.ThrowIfCancellationRequested();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Call(client, cts.Token));

        time.DelayOverride = null;
        JsonElement result = await Call(client).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(JsonValueKind.Array, result.ValueKind);
        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task Caller_cancel_during_request_is_not_treated_as_timeout()
    {
        using var cts = new CancellationTokenSource();
        var handler = new RoutingHandler((_, _) =>
        {
            cts.Cancel();
            throw new TaskCanceledException("nguoi goi huy", null, cts.Token);
        });
        using ApiClient client = NewClient(handler, new ApiTestKit.FakeTime(), "https://h1.zalo.me", "https://h2.zalo.me");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Call(client, cts.Token));

        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Trace_has_only_service_path_index_and_attempt()
    {
        const string ResponseSecret = "RESPONSE-SECRET-VALUE";
        var lines = new List<string>();
        var context = ApiTestKit.Context();
        context.AddCookies("zpw_sek=COOKIE-SECRET-VALUE");
        var handler = new RoutingHandler((_, _) => ApiTestKit.OkEnvelope($"[\"{ResponseSecret}\"]"));
        using var client = new ApiClient(
            context, ApiTestKit.Login("https://h1.zalo.me"), new ApiTestKit.FakeTime().Options(trace: lines.Add), handler);

        await client.GetEncryptedAsync("profile", Path, $"{{\"imei\":\"{ApiTestKit.FakeImei}\",\"uid\":\"{ApiTestKit.FakeUid}\"}}");

        string sentParams = ApiTestKit.ParamsOf(Assert.Single(handler.Calls).Uri);
        Assert.NotEmpty(lines);
        string all = string.Join("\n", lines);
        Assert.Contains("profile", all, StringComparison.Ordinal);
        Assert.Contains(Path, all, StringComparison.Ordinal);
        foreach (string secret in new[]
        {
            ApiTestKit.FakeEnk, sentParams, "COOKIE-SECRET-VALUE", ApiTestKit.FakeUid, ApiTestKit.FakeImei,
            ResponseSecret, "https://", "params=", "zpw_ver",
        })
        {
            Assert.DoesNotContain(secret, all, StringComparison.Ordinal);
        }
    }
}
