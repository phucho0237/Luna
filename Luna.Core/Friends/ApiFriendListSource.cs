using Luna.Core.Auth;
using Luna.Protocol.Api;

namespace Luna.Core.Friends;

/// <summary>
/// Thin adapter over <see cref="ApiClient.GetFriendListAsync"/> for a signed-in session. It adds no logic: the throttle
/// guard, the single-request rule and the error mapping live in <see cref="FriendDirectory"/>. The client is built with
/// default options and NO trace action, so nothing is written to any log.
/// </summary>
public sealed class ApiFriendListSource : IFriendListSource, IDisposable
{
    private readonly ApiClient _client;

    public ApiFriendListSource(AuthSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _client = new ApiClient(session.Context, session.Info);
    }

    /// <inheritdoc />
    public Task<FriendListResult> GetAsync(CancellationToken cancellationToken = default) =>
        _client.GetFriendListAsync(onElement: null, cancellationToken);

    public void Dispose() => _client.Dispose();

    /// <summary>Never prints the session.</summary>
    public override string ToString() => nameof(ApiFriendListSource);
}
