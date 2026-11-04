using System.Net;
using Xunit;
using Luna.Protocol.Api;
using Luna.Protocol.Tests.Support;

namespace Luna.Protocol.Tests.Api;

/// <summary>Minimum interval per endpoint: a call that is too early throws without sending anything. Fake clock, no real waiting.</summary>
public sealed class EndpointIntervalTests
{
    private const string Heavy = "/api/social/friend/getfriends";
    private const string Light = "/api/test/light";
    private static readonly TimeSpan Ten = TimeSpan.FromMinutes(10);

    /// <summary>Answers by script: <paramref name="respond"/> receives the request number (from 0) and the host.</summary>
    private sealed class ScriptedHandler(Func<int, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Hosts { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int index = Hosts.Count;
            Hosts.Add(request.RequestUri!.Host);
            return Task.FromResult(respond(index, request.RequestUri.Host));
        }
    }

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK) { Content = new StringContent(ApiTestKit.OkEnvelope()) };

    private static ApiClient NewClient(
        ScriptedHandler handler, ApiTestKit.FakeTime time, params string[] hosts) =>
        new(
            ApiTestKit.Context(),
            ApiTestKit.Login(hosts.Length == 0 ? ["https://h1.zalo.me"] : hosts),
            time.Options(endpointIntervals: new Dictionary<string, TimeSpan> { [Heavy] = Ten }),
            handler);

    private static Task Call(ApiClient client, string path = Heavy, CancellationToken ct = default) =>
        client.GetEncryptedAsync("profile", path, "{}", ct);

    [Fact]
    public void Default_options_limit_getfriends_to_one_call_per_ten_minutes()
    {
        var options = new ApiClientOptions();

        Assert.Equal(Ten, options.EndpointMinIntervals[Heavy]);
        Assert.Equal(Ten, options.EndpointMinIntervals[Heavy.ToUpperInvariant()]);
    }

    [Fact]
    public async Task Second_call_within_interval_throws_without_sending()
    {
        var handler = new ScriptedHandler((_, _) => Ok());
        var time = new ApiTestKit.FakeTime();
        using ApiClient client = NewClient(handler, time);

        await Call(client);
        var ex = await Assert.ThrowsAsync<RateLimitedException>(() => Call(client));

        Assert.Single(handler.Hosts);
        Assert.Equal(Ten, ex.RetryAfter);
    }

    [Fact]
    public async Task Retry_after_shrinks_as_time_passes()
    {
        var handler = new ScriptedHandler((_, _) => Ok());
        var time = new ApiTestKit.FakeTime();
        using ApiClient client = NewClient(handler, time);

        await Call(client);
        time.Now += TimeSpan.FromMinutes(4);
        var ex = await Assert.ThrowsAsync<RateLimitedException>(() => Call(client));

        Assert.Equal(TimeSpan.FromMinutes(6), ex.RetryAfter);
    }

    [Fact]
    public async Task Call_is_allowed_again_after_interval()
    {
        var handler = new ScriptedHandler((_, _) => Ok());
        var time = new ApiTestKit.FakeTime();
        using ApiClient client = NewClient(handler, time);

        await Call(client);
        time.Now += Ten - TimeSpan.FromSeconds(1);
        await Assert.ThrowsAsync<RateLimitedException>(() => Call(client));
        time.Now += TimeSpan.FromSeconds(1);
        await Call(client);

        Assert.Equal(2, handler.Hosts.Count);
    }

    [Fact]
    public async Task Other_paths_are_not_affected()
    {
        var handler = new ScriptedHandler((_, _) => Ok());
        var time = new ApiTestKit.FakeTime();
        using ApiClient client = NewClient(handler, time);

        await Call(client);
        await Call(client, Light);
        await Call(client, Light);

        Assert.Equal(3, handler.Hosts.Count);
    }

    [Fact]
    public async Task Path_match_ignores_case()
    {
        var handler = new ScriptedHandler((_, _) => Ok());
        var time = new ApiTestKit.FakeTime();
        using ApiClient client = NewClient(handler, time);

        await Call(client);
        await Assert.ThrowsAsync<RateLimitedException>(() => Call(client, Heavy.ToUpperInvariant()));

        Assert.Single(handler.Hosts);
    }

    [Fact]
    public async Task Blocked_call_is_not_a_429_and_does_not_start_cooldown()
    {
        var handler = new ScriptedHandler((_, _) => Ok());
        var time = new ApiTestKit.FakeTime();
        using ApiClient client = NewClient(handler, time);

        await Call(client);
        await Assert.ThrowsAsync<RateLimitedException>(() => Call(client));
        await Call(client, Light); // If this were counted as a 429, this call would be blocked by the 60-second cooldown.

        Assert.Equal(2, handler.Hosts.Count);
    }

    [Fact]
    public async Task Clock_set_back_one_hour_never_gives_retry_after_above_interval()
    {
        var handler = new ScriptedHandler((_, _) => Ok());
        var time = new ApiTestKit.FakeTime();
        using ApiClient client = NewClient(handler, time);

        await Call(client);
        time.Now -= TimeSpan.FromHours(1);
        var ex = await Assert.ThrowsAsync<RateLimitedException>(() => Call(client));
        time.Now += Ten;
        await Call(client); // Re-anchored: it can be called exactly 10 minutes after the time it was detected.

        Assert.Equal(Ten, ex.RetryAfter);
        Assert.Equal(2, handler.Hosts.Count);
    }

    [Fact]
    public async Task Network_error_on_first_send_still_counts_as_sent()
    {
        var handler = new ScriptedHandler((_, _) => throw new HttpRequestException("mang"));
        var time = new ApiTestKit.FakeTime();
        using ApiClient client = NewClient(handler, time);

        await Assert.ThrowsAsync<HttpRequestException>(() => Call(client));
        await Assert.ThrowsAsync<RateLimitedException>(() => Call(client));

        Assert.Single(handler.Hosts);
    }

    [Fact]
    public async Task Failover_within_one_call_is_not_blocked_by_its_own_mark_but_next_call_is()
    {
        var handler = new ScriptedHandler((index, _) =>
            index == 0 ? throw new HttpRequestException("mang") : Ok());
        var time = new ApiTestKit.FakeTime();
        using ApiClient client = NewClient(handler, time, "https://h1.zalo.me", "https://h2.zalo.me");

        await Call(client);
        Assert.Equal(["h1.zalo.me", "h2.zalo.me"], handler.Hosts);

        await Assert.ThrowsAsync<RateLimitedException>(() => Call(client));
        Assert.Equal(2, handler.Hosts.Count);
    }

    [Fact]
    public async Task Cancel_while_waiting_for_min_interval_does_not_record_a_mark()
    {
        var handler = new ScriptedHandler((_, _) => Ok());
        var time = new ApiTestKit.FakeTime();
        using ApiClient client = NewClient(handler, time);
        using var cts = new CancellationTokenSource();

        await Call(client, Light); // Sets the MinInterval mark so the next call has to wait.
        time.DelayOverride = (_, token) =>
        {
            cts.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Call(client, Heavy, cts.Token));
        time.DelayOverride = null;
        await Call(client, Heavy); // The cancelled call sent nothing, so it leaves no mark.

        Assert.Equal(2, handler.Hosts.Count);
    }

    [Fact]
    public async Task Already_cancelled_token_does_not_record_a_mark()
    {
        var handler = new ScriptedHandler((_, _) => Ok());
        var time = new ApiTestKit.FakeTime();
        using ApiClient client = NewClient(handler, time);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Call(client, Heavy, cts.Token));
        await Call(client, Heavy);

        Assert.Single(handler.Hosts);
    }
}
