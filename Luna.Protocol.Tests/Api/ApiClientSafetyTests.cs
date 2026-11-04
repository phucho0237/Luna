using System.Net;
using Xunit;
using Luna.Protocol.Api;
using Luna.Protocol.Tests.Support;

namespace Luna.Protocol.Tests.Api;

public sealed class ApiClientSafetyTests
{
    /// <summary>Handler that counts requests running at the same time; holds the first request until the test allows it.</summary>
    private sealed class ConcurrencyHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _firstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _inFlight;
        private int _calls;

        public int MaxInFlight { get; private set; }

        public int Calls => _calls;

        public Task FirstEntered => _firstEntered.Task;

        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int now = Interlocked.Increment(ref _inFlight);
            MaxInFlight = Math.Max(MaxInFlight, now);
            int call = Interlocked.Increment(ref _calls);
            try
            {
                if (call == 1)
                {
                    _firstEntered.TrySetResult();
                    await _release.Task.ConfigureAwait(false);
                }

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ApiTestKit.OkEnvelope()) };
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    [Fact]
    public async Task Concurrent_calls_are_serialized()
    {
        var handler = new ConcurrencyHandler();
        using var client = new ApiClient(
            ApiTestKit.Context(), ApiTestKit.Login("https://h1.zalo.me"), new ApiTestKit.FakeTime().Options(), handler);

        Task first = client.GetEncryptedAsync("profile", "/a", "{}");
        await handler.FirstEntered.WaitAsync(TimeSpan.FromSeconds(10));
        Task second = client.GetEncryptedAsync("profile", "/b", "{}");

        // Give the second request time to (if there is a bug) get into the handler while the first is still held.
        await Task.Delay(100);
        Assert.Equal(1, handler.Calls);

        handler.Release();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, handler.Calls);
        Assert.Equal(1, handler.MaxInFlight);
    }

    [Fact]
    public async Task Concurrent_calls_still_respect_MinInterval()
    {
        var handler = new RoutingHandler((_, _) => ApiTestKit.OkEnvelope());
        var time = new ApiTestKit.FakeTime();
        using var client = new ApiClient(
            ApiTestKit.Context(), ApiTestKit.Login("https://h1.zalo.me"), time.Options(), handler);

        await Task.WhenAll(
            client.GetEncryptedAsync("profile", "/a", "{}"),
            client.GetEncryptedAsync("profile", "/b", "{}"),
            client.GetEncryptedAsync("profile", "/c", "{}"));

        Assert.Equal(3, handler.Calls.Count);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)], time.Delays);
    }

    [Fact]
    public async Task Exceptions_never_expose_params_or_zpw_enk()
    {
        const string Payload = "{\"imei\":\"11111111-2222-3333-4444-555555555555-0123456789abcdef0123456789abcdef\"}";
        var captured = new List<Exception>();
        string? sentParams = null;

        async Task Run(Func<HttpRequestMessage, string> respond, Action<HttpRequestMessage, HttpResponseMessage>? decorate = null)
        {
            var handler = new RoutingHandler((req, _) =>
            {
                sentParams = ApiTestKit.ParamsOf(req.RequestUri!);
                return respond(req);
            })
            {
                Decorate = decorate,
            };
            using var client = new ApiClient(
                ApiTestKit.Context(),
                ApiTestKit.Login("https://h1.zalo.me", "https://h2.zalo.me"),
                new ApiTestKit.FakeTime().Options(),
                handler);
            try
            {
                await client.GetEncryptedAsync("profile", "/x", Payload);
            }
            catch (Exception ex)
            {
                captured.Add(ex);
                Assert.NotNull(sentParams);
                AssertClean(ex, sentParams!);
            }
        }

        await Run(_ => "{\"error_code\":5,\"error_message\":\"loi\"}");
        await Run(_ => ApiTestKit.Envelope("{\"error_code\":6}"));
        await Run(_ => "not json");
        await Run(_ => "{\"error_code\":0,\"data\":\"AAAA\"}");
        await Run(_ => "{\"error_code\":0,\"data\":\"!!!\"}");
        await Run(_ => ApiTestKit.OkEnvelope(), (_, r) => r.StatusCode = HttpStatusCode.Forbidden);
        await Run(_ => ApiTestKit.OkEnvelope(), (_, r) => r.StatusCode = HttpStatusCode.TooManyRequests);
        await Run(_ => ApiTestKit.OkEnvelope(), (_, r) => r.StatusCode = HttpStatusCode.BadGateway);

        // Make sure every case really throws (no branch silently succeeds).
        Assert.Equal(8, captured.Count);
    }

    private static void AssertClean(Exception ex, string sentParams)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            foreach (string text in new[] { current.Message, current.ToString() })
            {
                Assert.DoesNotContain(ApiTestKit.FakeEnk, text, StringComparison.Ordinal);
                Assert.DoesNotContain(sentParams, text, StringComparison.Ordinal);
                Assert.DoesNotContain("params=", text, StringComparison.Ordinal);
            }
        }
    }
}
