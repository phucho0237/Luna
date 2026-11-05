using Luna.Core.Auth;
using Luna.Protocol.Api;

namespace Luna.Core.Friends;

/// <summary>
/// Thin adapter over <see cref="ApiClient.GetFriendListAsync"/> for a signed-in session. It adds no logic: the throttle
/// guard, the single-request rule and the error mapping live in <see cref="FriendDirectory"/>. The client is built with
/// options from <see cref="CreateOptions"/> (no endpoint interval, NO trace action), so nothing is written to any log.
/// </summary>
public sealed class ApiFriendListSource : IFriendListSource, IDisposable
{
    private readonly ApiClient _client;

    public ApiFriendListSource(AuthSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _client = new ApiClient(session.Context, session.Info, CreateOptions());
    }

    /// <summary>
    /// The options of the underlying client: NO per-endpoint interval and NO trace action. The throttle guard in
    /// <see cref="FriendDirectory"/> is the single source of truth for the 10-minute rule. The client interval is per
    /// instance and counted from a slightly later mark, so it could throw a client-side <see cref="RateLimitedException"/>
    /// (not a real HTTP 429) right after the guard allowed the call, and the directory would wrongly record a 60-minute
    /// block. A <see cref="RateLimitedException"/> from this source therefore means a real 429 (or the client's own
    /// cooldown after one).
    /// </summary>
    public static ApiClientOptions CreateOptions() => new()
    {
        EndpointMinIntervals = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase),
        Trace = null,
    };

    /// <inheritdoc />
    public Task<FriendListResult> GetAsync(CancellationToken cancellationToken = default) =>
        _client.GetFriendListAsync(onElement: null, cancellationToken);

    public void Dispose() => _client.Dispose();

    /// <summary>Never prints the session.</summary>
    public override string ToString() => nameof(ApiFriendListSource);
}
