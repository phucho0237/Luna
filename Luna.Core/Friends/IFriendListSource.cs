using Luna.Protocol.Api;

namespace Luna.Core.Friends;

/// <summary>
/// Fetches the friend list from the server. One call is exactly ONE network request; the source never retries and never
/// pages. The caller (<see cref="FriendDirectory"/>) owns every safety rule around it.
/// </summary>
public interface IFriendListSource
{
    /// <exception cref="ApiException">The server answered with a non-zero error code.</exception>
    /// <exception cref="RateLimitedException">HTTP 429, or the client is in its own cooldown.</exception>
    /// <exception cref="HttpRequestException">Network failure.</exception>
    /// <exception cref="InvalidDataException">The response was malformed.</exception>
    Task<FriendListResult> GetAsync(CancellationToken cancellationToken = default);
}
