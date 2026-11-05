namespace Luna.Core.Friends;

/// <summary>Where the friend-list load stands. The view maps it to localized text.</summary>
public enum FriendsState
{
    /// <summary>No load has finished or been refused yet.</summary>
    NotLoaded,

    Loading,

    /// <summary>The list was fetched and the names were cached.</summary>
    Loaded,

    /// <summary>Not asked: the minimum interval since the last request has not passed. The cached names stay in use.</summary>
    Throttled,

    /// <summary>Not asked: the cooldown after an HTTP 429 is running (or just started).</summary>
    CoolingDown,

    Failed,
}

/// <summary>Why a load ended in <see cref="FriendsState.Failed"/>. Never carries server text.</summary>
public enum FriendsFailure
{
    None,
    Network,
    Server,
    UnexpectedResponse,

    /// <summary>The list was fetched but caching the names on disk failed.</summary>
    Storage,
}
