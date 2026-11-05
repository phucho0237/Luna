using Luna.Protocol.Messaging;

namespace Luna.Core.Realtime;

/// <summary>
/// The live message feed. One <see cref="RunAsync"/> call is at most ONE WebSocket connection at a time (it may reconnect
/// by itself inside the call). It returns when cancelled or when the account is opened elsewhere, and it never
/// restarts itself after that.
/// </summary>
public interface IRealtimeSource
{
    /// <summary>
    /// Runs until <paramref name="cancellationToken"/> is cancelled or the run ends by itself. Both callbacks are called
    /// on background threads and must return quickly. A cancellation ends the call normally (no exception).
    /// </summary>
    Task RunAsync(
        Action<IReadOnlyList<ChatMessage>> onMessages,
        Action<LiveSyncState> onState,
        CancellationToken cancellationToken = default);
}
