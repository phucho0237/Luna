using System.Net;
using Xunit;
using Luna.Protocol.Api;
using Luna.Protocol.Tests.Support;

namespace Luna.Protocol.Tests.Api;

/// <summary>Cooldown after HTTP 429: while in cooldown no request is sent. Uses a fake clock, no real waiting.</summary>
public sealed class RateLimitCooldownTests
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(1);

    private static ApiClient NewClient(CountingHandler handler, ApiTestKit.FakeTime time, params string[] hosts) =>
        new(ApiTestKit.Context(), ApiTestKit.Login(hosts.Length == 0 ? ["https://h1.zalo.me"] : hosts), time.Options(), handler);

    private static Task Call(ApiClient client, CancellationToken ct = default) =>
        client.GetEncryptedAsync("profile", "/api/test/path", "{}", ct);

    /// <summary>Sends one call that gets a 429, then returns the handler to the 200 state.</summary>
    private static async Task Trigger429(ApiClient client, CountingHandler handler)
    {
        handler.Status = _ => HttpStatusCode.TooManyRequests;
        await Assert.ThrowsAsync<RateLimitedException>(() => Call(client));
        handler.Status = _ => HttpStatusCode.OK;
    }

    [Fact]
    public async Task Call_after_429_throws_without_sending_any_request()
    {
        var handler = new CountingHandler();
        var time = new ApiTestKit.FakeTime();
        using ApiClient client = NewClient(handler, time);

        await Trigger429(client, handler);
        await Assert.ThrowsAsync<RateLimitedException>(() => Call(client));

        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task Call_succeeds_again_once_cooldown_has_elapsed()
    {
        var handler = new CountingHandler();
        var time = new ApiTestKit.FakeTime();
        using ApiClient client = NewClient(handler, time);

        await Trigger429(client, handler);
        time.Now += TimeSpan.FromSeconds(59);
        await Assert.ThrowsAsync<RateLimitedException>(() => Call(client));
        time.Now += Step;
        await Call(client);

        Assert.Equal(2, handler.Count);
    }

    [Fact]
    public async Task Two_consecutive_429_give_120_second_cooldown()
    {
        var handler = new CountingHandler();
        var time = new ApiTestKit.FakeTime();
        using ApiClient client = NewClient(handler, time);

        await Trigger429(client, handler);
        time.Now += TimeSpan.FromSeconds(60);
        await Trigger429(client, handler);

        time.Now += TimeSpan.FromSeconds(119);
        await Assert.ThrowsAsync<RateLimitedException>(() => Call(client));
        time.Now += Step;
        await Call(client);

        Assert.Equal(3, handler.Count);
    }

    [Fact]
    public async Task Cooldown_is_capped_at_15_minutes()
    {
        var handler = new CountingHandler();
        var time = new ApiTestKit.FakeTime();
        using ApiClient client = NewClient(handler, time);

        // 60, 120, 240, 480, 960 (clamped to 900), 1920 (clamped to 900).
        for (int i = 0; i < 6; i++)
        {
            await Trigger429(client, handler);
            time.Now += TimeSpan.FromHours(1);
        }

        await Trigger429(client, handler);
        time.Now += TimeSpan.FromSeconds(899);
        await Assert.ThrowsAsync<RateLimitedException>(() => Call(client));
        time.Now += Step;
        await Call(client);
    }

    [Fact]
    public async Task Success_resets_cooldown_to_60_seconds()
    {
        var handler = new CountingHandler();
        var time = new ApiTestKit.FakeTime();
        using ApiClient client = NewClient(handler, time);

        await Trigger429(client, handler);
        time.Now += TimeSpan.FromSeconds(60);
        await Call(client); // success: resets the counter
        await Trigger429(client, handler);

        time.Now += TimeSpan.FromSeconds(59);
        await Assert.ThrowsAsync<RateLimitedException>(() => Call(client));
        time.Now += Step;
        await Call(client);
    }

    [Fact]
    public async Task Failover_attempt_returning_429_starts_cooldown()
    {
        var handler = new CountingHandler { Status = req => req.RequestUri!.Host == "h1.zalo.me" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.TooManyRequests };
        var time = new ApiTestKit.FakeTime();
        using ApiClient client = NewClient(handler, time, "https://h1.zalo.me", "https://h2.zalo.me");

        await Assert.ThrowsAsync<RateLimitedException>(() => Call(client));
        Assert.Equal(2, handler.Count);

        await Assert.ThrowsAsync<RateLimitedException>(() => Call(client));
        Assert.Equal(2, handler.Count);
    }

    [Fact]
    public async Task Cancelled_token_still_cancels_during_cooldown()
    {
        var handler = new CountingHandler();
        var time = new ApiTestKit.FakeTime();
        using ApiClient client = NewClient(handler, time);
        await Trigger429(client, handler);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Call(client, cts.Token));
    }

    [Fact]
    public async Task Non_429_errors_do_not_start_cooldown()
    {
        var handler = new CountingHandler { Status = _ => HttpStatusCode.InternalServerError };
        var time = new ApiTestKit.FakeTime();
        using ApiClient client = NewClient(handler, time);

        await Assert.ThrowsAsync<HttpRequestException>(() => Call(client));
        handler.Status = _ => HttpStatusCode.OK;
        handler.Body = "{\"error_code\":112,\"error_message\":\"x\"}";
        await Assert.ThrowsAsync<ApiException>(() => Call(client));
        handler.Body = ApiTestKit.OkEnvelope();
        await Call(client);

        Assert.Equal(3, handler.Count);
    }

    [Fact]
    public async Task Clock_going_backwards_does_not_extend_cooldown()
    {
        var handler = new CountingHandler();
        var time = new ApiTestKit.FakeTime();
        using ApiClient client = NewClient(handler, time);
        await Trigger429(client, handler);

        time.Now -= TimeSpan.FromHours(1);
        await Assert.ThrowsAsync<RateLimitedException>(() => Call(client));
        Assert.Equal(1, handler.Count);

        // Cooldown is computed from the remaining time clamped to [0, 60s]: advancing 60s from the backdated mark is enough.
        time.Now += TimeSpan.FromSeconds(60);
        await Call(client);
    }

    [Fact]
    public async Task Clock_moving_forward_past_cooldown_allows_calls()
    {
        var handler = new CountingHandler();
        var time = new ApiTestKit.FakeTime();
        using ApiClient client = NewClient(handler, time);
        await Trigger429(client, handler);

        time.Now += TimeSpan.FromHours(2);
        await Call(client);

        Assert.Equal(2, handler.Count);
    }

    [Fact]
    public async Task Blocked_call_fails_immediately_while_another_request_is_in_flight()
    {
        var handler = new CountingHandler();
        var time = new ApiTestKit.FakeTime();
        using ApiClient client = NewClient(handler, time);
        await Trigger429(client, handler);
        DateTimeOffset afterTrigger = time.Now;
        time.Now += TimeSpan.FromSeconds(61);

        var release = new TaskCompletionSource();
        handler.Pause = release.Task;
        Task inFlight = Call(client); // holds _gate, hangs in the handler
        await handler.Started.WaitAsync(TimeSpan.FromSeconds(10));

        time.Now = afterTrigger + TimeSpan.FromSeconds(10); // clock goes backwards: it is back inside the cooldown
        await Assert.ThrowsAsync<RateLimitedException>(() => Call(client)).WaitAsync(TimeSpan.FromSeconds(5));

        release.SetResult();
        await inFlight.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Waiting_call_rechecks_cooldown_after_acquiring_gate()
    {
        var handler = new CountingHandler();
        var time = new ApiTestKit.FakeTime();
        using ApiClient client = NewClient(handler, time);

        var release = new TaskCompletionSource();
        handler.Pause = release.Task;
        handler.Status = _ => HttpStatusCode.TooManyRequests;
        Task first = Call(client); // is sending, will get a 429
        await handler.Started.WaitAsync(TimeSpan.FromSeconds(10));
        Task second = Call(client); // waits for _gate

        release.SetResult();
        await Assert.ThrowsAsync<RateLimitedException>(() => first).WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<RateLimitedException>(() => second).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task RetryAfter_is_remaining_cooldown_and_message_has_no_url_or_data()
    {
        var handler = new CountingHandler();
        var time = new ApiTestKit.FakeTime();
        using ApiClient client = NewClient(handler, time);
        await Trigger429(client, handler);
        time.Now += TimeSpan.FromSeconds(20);

        var ex = await Assert.ThrowsAsync<RateLimitedException>(() => Call(client));

        Assert.Equal(TimeSpan.FromSeconds(40), ex.RetryAfter);
        Assert.DoesNotContain("zalo.me", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/api/", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("params", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RetryAfter_of_the_429_itself_is_the_full_cooldown()
    {
        var handler = new CountingHandler { Status = _ => HttpStatusCode.TooManyRequests };
        var time = new ApiTestKit.FakeTime();
        using ApiClient client = NewClient(handler, time);

        var ex = await Assert.ThrowsAsync<RateLimitedException>(() => Call(client));

        Assert.Equal(TimeSpan.FromSeconds(60), ex.RetryAfter);
    }

    [Fact]
    public async Task RetryAfter_is_clamped_when_clock_goes_backwards()
    {
        var handler = new CountingHandler();
        var time = new ApiTestKit.FakeTime();
        using ApiClient client = NewClient(handler, time);
        await Trigger429(client, handler);
        time.Now -= TimeSpan.FromHours(1);

        var ex = await Assert.ThrowsAsync<RateLimitedException>(() => Call(client));

        Assert.True(ex.RetryAfter <= TimeSpan.FromSeconds(60), $"RetryAfter: {ex.RetryAfter}");
    }

    [Fact]
    public void Parameterless_constructor_gives_zero_RetryAfter()
    {
        Assert.Equal(TimeSpan.Zero, new RateLimitedException().RetryAfter);
    }

    [Fact]
    public void Negative_RetryAfter_is_clamped_to_zero()
    {
        Assert.Equal(TimeSpan.Zero, new RateLimitedException(TimeSpan.FromSeconds(-5)).RetryAfter);
    }

    /// <summary>Fake server that counts requests actually sent, picks the status code per request, and can hang via <see cref="Pause"/>.</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public Func<HttpRequestMessage, HttpStatusCode> Status { get; set; } = _ => HttpStatusCode.OK;

        public string Body { get; set; } = ApiTestKit.OkEnvelope();

        public Task? Pause { get; set; }

        public Task Started => _started.Task;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            _started.TrySetResult();
            if (Pause is { } pause)
            {
                await pause.ConfigureAwait(false);
            }

            return new HttpResponseMessage(Status(request)) { Content = new StringContent(Body) };
        }
    }
}
