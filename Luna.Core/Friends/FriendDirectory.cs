using CommunityToolkit.Mvvm.ComponentModel;
using Luna.Core.Storage;
using Luna.Core.Throttling;
using Luna.Core.ViewModels;
using Luna.Protocol;
using Luna.Protocol.Api;

namespace Luna.Core.Friends;

/// <summary>
/// Loads the friend list at most once per run of <see cref="LoadAsync"/> and caches the names. Safety rules, in this order:
/// (1) during the cooldown after an HTTP 429 nothing is sent; (2) within the minimum interval since the last
/// <c>getfriends</c> nothing is sent (the cached names stay in use); (3) otherwise the call is recorded BEFORE the request,
/// exactly one request is made, it is never retried and never looped; (4) a 429 records the block and stops.
/// State is numbers and enums only: no server text, name, uid or phone number is ever kept in a public member.
/// Every state change goes through <see cref="IUiDispatcher.Post"/>.
/// </summary>
public sealed partial class FriendDirectory : ObservableObject
{
    /// <summary>The throttle-guard key of the friend list request.</summary>
    public const string ThrottleKey = "getfriends";

    /// <summary>Minimum time between two friend list requests.</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(10);

    /// <summary>Longest name kept per friend (UTF-16 units; a surrogate pair is never cut in half).</summary>
    public const int MaxNameLength = 100;

    private readonly IFriendListSource _source;
    private readonly ApiThrottleGuard _guard;
    private readonly IContactStore _contacts;
    private readonly IUiDispatcher _ui;
    private readonly object _lock = new();

    // The last state handed to the dispatcher. Kept synchronously so a cancelled load can put the previous state back.
    private Snapshot _current = new(FriendsState.NotLoaded, FriendsFailure.None, null, null);

    // 0 = idle, 1 = a load is running. A second call while one runs does nothing.
    private int _running;

    public FriendDirectory(IFriendListSource source, ApiThrottleGuard guard, IContactStore contacts, IUiDispatcher ui)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(contacts);
        ArgumentNullException.ThrowIfNull(ui);
        _source = source;
        _guard = guard;
        _contacts = contacts;
        _ui = ui;
    }

    [ObservableProperty]
    public partial FriendsState State { get; private set; }

    [ObservableProperty]
    public partial FriendsFailure Failure { get; private set; }

    /// <summary>Minutes until a new request is allowed; set for <see cref="FriendsState.Throttled"/> and <see cref="FriendsState.CoolingDown"/>.</summary>
    [ObservableProperty]
    public partial int? RetryMinutes { get; private set; }

    /// <summary>The numeric server error code; set only when <see cref="Failure"/> is <see cref="FriendsFailure.Server"/>.</summary>
    [ObservableProperty]
    public partial int? ServerErrorCode { get; private set; }

    /// <summary>Never prints names, uids or server text.</summary>
    public override string ToString() => nameof(FriendDirectory);

    /// <summary>
    /// Runs the guarded load. Returns without sending anything when the guard refuses, and when a load is already
    /// running. Never throws for server, network or storage problems; those become the state. A cancellation puts the
    /// previous state back and returns quietly.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return;
        }

        Snapshot previous = Current();
        try
        {
            if (_guard.MinutesRemaining() is { } cooldown)
            {
                Apply(new Snapshot(FriendsState.CoolingDown, FriendsFailure.None, cooldown, null));
                return;
            }

            if (_guard.MinutesUntilAllowed(ThrottleKey, MinimumInterval) is { } wait)
            {
                Apply(new Snapshot(FriendsState.Throttled, FriendsFailure.None, wait, null));
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            Apply(new Snapshot(FriendsState.Loading, FriendsFailure.None, null, null));

            // Recorded BEFORE the request: an error midway still counts as a call.
            _guard.RecordCall(ThrottleKey);

            FriendListResult result = await _source.GetAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<ContactName> names = ToContactNames(result);
            await _contacts.ReplaceContactsAsync(names, cancellationToken).ConfigureAwait(false);

            Apply(new Snapshot(FriendsState.Loaded, FriendsFailure.None, null, null));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Apply(previous);
        }
        catch (RateLimitedException)
        {
            _guard.RecordBlock();
            int minutes = _guard.MinutesRemaining() ?? (int)ApiThrottleGuard.BlockDuration.TotalMinutes;
            Apply(new Snapshot(FriendsState.CoolingDown, FriendsFailure.None, minutes, null));
        }
        catch (ApiException ex)
        {
            // Only the numeric code is kept, never the server's message text.
            Apply(new Snapshot(FriendsState.Failed, FriendsFailure.Server, null, ex.ErrorCode));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // HttpClient reports its own timeout as a cancellation while our token is still alive.
            Apply(new Snapshot(FriendsState.Failed, FriendsFailure.Network, null, null));
        }
        catch (InvalidDataException)
        {
            Apply(new Snapshot(FriendsState.Failed, FriendsFailure.UnexpectedResponse, null, null));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException
                                       or Microsoft.Data.Sqlite.SqliteException)
        {
            Apply(new Snapshot(FriendsState.Failed, FriendsFailure.Storage, null, null));
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private static List<ContactName> ToContactNames(FriendListResult result)
    {
        var names = new List<ContactName>(result.Friends.Count);
        foreach (Friend friend in result.Friends)
        {
            string? name = FirstUsable(friend.DisplayName, friend.ProfileName, friend.Username);
            if (name is not null)
            {
                names.Add(new ContactName(friend.UserId, Cap(name)));
            }
        }

        return names;
    }

    private static string? FirstUsable(params string?[] candidates)
    {
        foreach (string? candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string Cap(string name)
    {
        if (name.Length <= MaxNameLength)
        {
            return name;
        }

        int length = MaxNameLength;
        if (char.IsHighSurrogate(name[length - 1]))
        {
            length--;
        }

        return name[..length];
    }

    private Snapshot Current()
    {
        lock (_lock)
        {
            return _current;
        }
    }

    private void Apply(Snapshot snapshot)
    {
        lock (_lock)
        {
            _current = snapshot;
        }

        _ui.Post(() =>
        {
            State = snapshot.State;
            Failure = snapshot.Failure;
            RetryMinutes = snapshot.RetryMinutes;
            ServerErrorCode = snapshot.ServerErrorCode;
        });
    }

    private sealed record Snapshot(FriendsState State, FriendsFailure Failure, int? RetryMinutes, int? ServerErrorCode)
    {
        /// <summary>Numbers and enums only, but kept short anyway.</summary>
        public override string ToString() => nameof(Snapshot);
    }
}
