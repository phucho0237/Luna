namespace Luna.Core.Realtime;

/// <summary>Where the live connection stands. The view maps it to localized text. Never carries server text.</summary>
public enum LiveSyncState
{
    /// <summary>Not running: never started, or stopped.</summary>
    Stopped,

    Connecting,

    /// <summary>Connected and authenticated.</summary>
    Connected,

    /// <summary>
    /// The connection is down. While the source is still running it retries by itself; when the source has ended
    /// (it failed) nothing retries until an explicit <see cref="LiveSyncService.Reconnect"/>.
    /// </summary>
    Disconnected,

    /// <summary>The account is open elsewhere. Nothing reconnects until an explicit <see cref="LiveSyncService.Reconnect"/>.</summary>
    DuplicateConnection,
}
