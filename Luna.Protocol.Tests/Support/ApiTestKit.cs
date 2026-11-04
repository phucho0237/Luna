using Luna.Protocol.Api;
using Luna.Protocol.Auth;
using Luna.Protocol.Crypto;

namespace Luna.Protocol.Tests.Support;

/// <summary>Shared fakes for ApiClient tests. Every value is fake.</summary>
internal static class ApiTestKit
{
    /// <summary>16 byte 00..0f, Base64.</summary>
    public const string FakeEnk = "AAECAwQFBgcICQoLDA0ODw==";

    public const string FakeImei = "11111111-2222-3333-4444-555555555555-0123456789abcdef0123456789abcdef";

    public const string FakeUid = "uid-fake-424242";

    public static LoginInfo Login(params string[] hosts) =>
        new(FakeUid, FakeEnk, new Dictionary<string, IReadOnlyList<string>> { ["profile"] = hosts }, []);

    public static ClientContext Context() => new(FakeImei, "UA-test");

    /// <summary>Builds a valid two-layer response around <paramref name="innerJson"/> (the inner layer).</summary>
    public static string Envelope(string innerJson) =>
        $"{{\"error_code\":0,\"error_message\":\"Successful.\",\"data\":\"{new SessionCipher(FakeEnk).Encrypt(innerJson)}\"}}";

    public static string OkEnvelope(string dataJson = "[]") => Envelope($"{{\"error_code\":0,\"data\":{dataJson}}}");

    public static string ParamsOf(Uri uri)
    {
        foreach (string part in uri.Query.TrimStart('?').Split('&'))
        {
            if (part.StartsWith("params=", StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(part["params=".Length..]);
            }
        }

        throw new InvalidOperationException("The URL has no params.");
    }

    /// <summary>Fake clock and Delay: Delay only records the wait and then advances the clock, no real waiting.</summary>
    public sealed class FakeTime
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public List<TimeSpan> Delays { get; } = [];

        public Func<TimeSpan, CancellationToken, Task>? DelayOverride { get; set; }

        public ApiClientOptions Options(
            Action<string>? trace = null,
            int maxHostAttempts = 3,
            IReadOnlyDictionary<string, TimeSpan>? endpointIntervals = null) => new()
        {
            MinInterval = TimeSpan.FromSeconds(1), // Explicit: tests do not depend on the options default.
            // Explicit: by default there is no per-endpoint spacing; a test that needs it passes it in.
            EndpointMinIntervals = endpointIntervals ?? new Dictionary<string, TimeSpan>(),
            MaxHostAttempts = maxHostAttempts,
            Trace = trace,
            Clock = () => Now,
            Delay = async (span, token) =>
            {
                Delays.Add(span);
                if (DelayOverride is { } custom)
                {
                    await custom(span, token).ConfigureAwait(false);
                }

                Now += span;
            },
        };
    }
}
