using System.Reflection;
using Luna.Core.Friends;
using Luna.Core.Storage;
using Luna.Core.Tests.Support;
using Luna.Core.Throttling;
using Luna.Core.ViewModels;
using Luna.Protocol;
using Luna.Protocol.Api;

namespace Luna.Core.Tests.Friends;

/// <summary>
/// Every safety rule of <see cref="FriendDirectory"/>. The throttle guard uses a temp file named luna-core-tests-* and a
/// fake clock, never the real file. No test touches the network.
/// </summary>
public sealed class FriendDirectoryTests : IDisposable
{
    private const string Key = "getfriends";
    private static readonly TimeSpan Ten = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private const string FakeUidA = "fake-friend-uid-1001";
    private const string FakeUidB = "fake-friend-uid-1002";
    private const string FakeUidC = "fake-friend-uid-1003";
    private const string FakeNameA = "Fake Alpha Name";
    private const string FakeNameB = "Fake Beta Name";
    private const string FakeServerText = "fake-server-error-text-marker";

    private readonly TempFolder _folder = new();
    private DateTimeOffset _now = new(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeFriendListSource _source = new();
    private readonly FakeContactStore _contacts = new();

    public void Dispose() => _folder.Dispose();

    private ApiThrottleGuard NewGuard() => new(Path.Combine(_folder.Path, "api-throttle.json"), () => _now);

    private FriendDirectory NewDirectory(ApiThrottleGuard guard, IUiDispatcher? ui = null) =>
        new(_source, guard, _contacts, ui ?? new ImmediateDispatcher());

    // ---- Allowed path -------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_allowed_load_makes_exactly_one_request_and_ends_loaded()
    {
        _source.Result = FakeFriends.List(FakeFriends.Create(FakeUidA, displayName: FakeNameA));
        ApiThrottleGuard guard = NewGuard();
        FriendDirectory directory = NewDirectory(guard);

        await directory.LoadAsync();

        Assert.Equal(1, _source.Calls);
        Assert.Equal(FriendsState.Loaded, directory.State);
        Assert.Equal(FriendsFailure.None, directory.Failure);
        Assert.Null(directory.RetryMinutes);
        Assert.Null(directory.ServerErrorCode);
        Assert.NotNull(guard.MinutesUntilAllowed(Key, Ten));
    }

    [Fact]
    public async Task The_call_is_recorded_BEFORE_the_source_is_entered()
    {
        ApiThrottleGuard guard = NewGuard();
        int? minutesWhenEntered = null;
        _source.OnEnter = () => minutesWhenEntered = guard.MinutesUntilAllowed(Key, Ten);
        FriendDirectory directory = NewDirectory(guard);

        await directory.LoadAsync();

        Assert.Equal(1, _source.Calls);
        Assert.NotNull(minutesWhenEntered);
    }

    [Fact]
    public async Task A_failed_request_still_counts_and_is_never_retried()
    {
        _source.Exception = new HttpRequestException("fake network failure");
        FriendDirectory directory = NewDirectory(NewGuard());

        await directory.LoadAsync();
        Assert.Equal(FriendsState.Failed, directory.State);

        await directory.LoadAsync();

        Assert.Equal(1, _source.Calls);
        Assert.Equal(FriendsState.Throttled, directory.State);
    }

    [Fact]
    public async Task It_may_load_again_once_the_minimum_interval_has_passed()
    {
        FriendDirectory directory = NewDirectory(NewGuard());
        await directory.LoadAsync();

        _now += Ten + TimeSpan.FromSeconds(1);
        await directory.LoadAsync();

        Assert.Equal(2, _source.Calls);
        Assert.Equal(FriendsState.Loaded, directory.State);
    }

    [Fact]
    public async Task The_initial_state_is_not_loaded()
    {
        FriendDirectory directory = NewDirectory(NewGuard());

        Assert.Equal(FriendsState.NotLoaded, directory.State);
        Assert.Equal(FriendsFailure.None, directory.Failure);
        await Task.CompletedTask;
    }

    // ---- Refused by the guard ----------------------------------------------------------------------------------

    [Fact]
    public async Task Within_the_minimum_interval_the_source_is_never_called_and_the_cache_is_kept()
    {
        ApiThrottleGuard guard = NewGuard();
        _now -= TimeSpan.FromMinutes(3);
        guard.RecordCall(Key);
        _now += TimeSpan.FromMinutes(3);
        FriendDirectory directory = NewDirectory(guard);

        await directory.LoadAsync();

        Assert.Equal(0, _source.Calls);
        Assert.Equal(FriendsState.Throttled, directory.State);
        Assert.Equal(7, directory.RetryMinutes);
        Assert.Empty(_contacts.Replaced);
    }

    [Fact]
    public async Task During_the_cooldown_after_a_429_the_source_is_never_called()
    {
        ApiThrottleGuard guard = NewGuard();
        guard.RecordBlock();
        FriendDirectory directory = NewDirectory(guard);

        await directory.LoadAsync();

        Assert.Equal(0, _source.Calls);
        Assert.Equal(FriendsState.CoolingDown, directory.State);
        Assert.Equal(60, directory.RetryMinutes);
        Assert.Empty(_contacts.Replaced);
    }

    [Fact]
    public async Task The_cooldown_is_checked_before_the_minimum_interval()
    {
        ApiThrottleGuard guard = NewGuard();
        guard.RecordCall(Key);
        guard.RecordBlock();
        FriendDirectory directory = NewDirectory(guard);

        await directory.LoadAsync();

        Assert.Equal(0, _source.Calls);
        Assert.Equal(FriendsState.CoolingDown, directory.State);
    }

    // ---- HTTP 429 -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_429_records_the_block_stops_and_sends_no_second_request()
    {
        _source.Exception = new RateLimitedException(TimeSpan.FromSeconds(60));
        ApiThrottleGuard guard = NewGuard();
        FriendDirectory directory = NewDirectory(guard);

        await directory.LoadAsync();

        Assert.Equal(1, _source.Calls);
        Assert.Equal(FriendsState.CoolingDown, directory.State);
        Assert.Equal(FriendsFailure.None, directory.Failure);
        Assert.Equal(60, directory.RetryMinutes);
        Assert.Equal(60, guard.MinutesRemaining());
        Assert.Empty(_contacts.Replaced);

        await directory.LoadAsync();

        Assert.Equal(1, _source.Calls);
        Assert.Equal(FriendsState.CoolingDown, directory.State);
    }

    [Fact]
    public async Task The_block_survives_a_new_directory_on_the_same_guard_file()
    {
        _source.Exception = new RateLimitedException();
        await NewDirectory(NewGuard()).LoadAsync();
        _source.Exception = null;
        FriendDirectory second = NewDirectory(NewGuard());

        await second.LoadAsync();

        Assert.Equal(1, _source.Calls);
        Assert.Equal(FriendsState.CoolingDown, second.State);
    }

    // ---- Other failures -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_server_error_keeps_only_its_numeric_code()
    {
        _source.Exception = new ApiException(123, FakeServerText);
        FriendDirectory directory = NewDirectory(NewGuard());

        await directory.LoadAsync();

        Assert.Equal(FriendsState.Failed, directory.State);
        Assert.Equal(FriendsFailure.Server, directory.Failure);
        Assert.Equal(123, directory.ServerErrorCode);
        Assert.Empty(_contacts.Replaced);
    }

    [Fact]
    public async Task A_network_error_maps_to_network()
    {
        _source.Exception = new HttpRequestException("fake");
        FriendDirectory directory = NewDirectory(NewGuard());

        await directory.LoadAsync();

        Assert.Equal(FriendsState.Failed, directory.State);
        Assert.Equal(FriendsFailure.Network, directory.Failure);
        Assert.Null(directory.ServerErrorCode);
    }

    [Fact]
    public async Task An_http_timeout_that_is_not_our_cancellation_maps_to_network()
    {
        _source.Exception = new TaskCanceledException("fake timeout");
        FriendDirectory directory = NewDirectory(NewGuard());

        await directory.LoadAsync();

        Assert.Equal(FriendsState.Failed, directory.State);
        Assert.Equal(FriendsFailure.Network, directory.Failure);
    }

    [Fact]
    public async Task A_malformed_response_maps_to_unexpected_response()
    {
        _source.Exception = new InvalidDataException("fake");
        FriendDirectory directory = NewDirectory(NewGuard());

        await directory.LoadAsync();

        Assert.Equal(FriendsState.Failed, directory.State);
        Assert.Equal(FriendsFailure.UnexpectedResponse, directory.Failure);
    }

    [Fact]
    public async Task A_failure_to_cache_the_names_maps_to_storage()
    {
        _source.Result = FakeFriends.List(FakeFriends.Create(FakeUidA, displayName: FakeNameA));
        _contacts.ReplaceException = new IOException("fake disk error");
        FriendDirectory directory = NewDirectory(NewGuard());

        await directory.LoadAsync();

        Assert.Equal(FriendsState.Failed, directory.State);
        Assert.Equal(FriendsFailure.Storage, directory.Failure);
    }

    [Fact]
    public async Task A_new_load_clears_the_previous_failure()
    {
        _source.Exception = new ApiException(55, FakeServerText);
        FriendDirectory directory = NewDirectory(NewGuard());
        await directory.LoadAsync();
        Assert.Equal(55, directory.ServerErrorCode);

        _source.Exception = null;
        _now += Ten + TimeSpan.FromSeconds(1);
        await directory.LoadAsync();

        Assert.Equal(FriendsState.Loaded, directory.State);
        Assert.Equal(FriendsFailure.None, directory.Failure);
        Assert.Null(directory.ServerErrorCode);
    }

    // ---- Cancellation and single flight -------------------------------------------------------------------------

    [Fact]
    public async Task A_cancellation_leaves_the_state_unchanged_and_caches_nothing()
    {
        _source.Block = true;
        FriendDirectory directory = NewDirectory(NewGuard());
        using var cts = new CancellationTokenSource();

        Task load = directory.LoadAsync(cts.Token);
        await _source.Entered.WaitAsync(Patience);
        Assert.Equal(FriendsState.Loading, directory.State);
        await cts.CancelAsync();
        await load.WaitAsync(Patience);

        Assert.Equal(FriendsState.NotLoaded, directory.State);
        Assert.Equal(FriendsFailure.None, directory.Failure);
        Assert.Empty(_contacts.Replaced);
    }

    [Fact]
    public async Task A_cancellation_after_a_good_load_keeps_the_loaded_state()
    {
        FriendDirectory directory = NewDirectory(NewGuard());
        await directory.LoadAsync();
        Assert.Equal(FriendsState.Loaded, directory.State);

        _now += Ten + TimeSpan.FromSeconds(1);
        _source.Block = true;
        using var cts = new CancellationTokenSource();
        Task load = directory.LoadAsync(cts.Token);
        await _source.Entered.WaitAsync(Patience);
        await cts.CancelAsync();
        await load.WaitAsync(Patience);

        Assert.Equal(FriendsState.Loaded, directory.State);
    }

    [Fact]
    public async Task A_second_load_while_one_runs_sends_no_request()
    {
        _source.Block = true;
        FriendDirectory directory = NewDirectory(NewGuard());

        Task first = directory.LoadAsync();
        await _source.Entered.WaitAsync(Patience);
        await directory.LoadAsync().WaitAsync(Patience);

        Assert.Equal(1, _source.Calls);
        _source.Release();
        await first.WaitAsync(Patience);
        Assert.Equal(FriendsState.Loaded, directory.State);
        Assert.Equal(1, _source.Calls);
    }

    // ---- Names --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_name_is_display_name_then_profile_name_then_username()
    {
        _source.Result = FakeFriends.List(
            FakeFriends.Create(FakeUidA, "user-a", "Display A", "Profile A"),
            FakeFriends.Create(FakeUidB, "user-b", null, "Profile B"),
            FakeFriends.Create(FakeUidC, "user-c", "  ", ""));
        FriendDirectory directory = NewDirectory(NewGuard());

        await directory.LoadAsync();

        IReadOnlyList<ContactName> saved = Assert.Single(_contacts.Replaced);
        Assert.Equal(
            [(FakeUidA, "Display A"), (FakeUidB, "Profile B"), (FakeUidC, "user-c")],
            saved.Select(c => (c.UserId, c.Name)).ToArray());
    }

    [Fact]
    public async Task Friends_without_any_name_are_skipped()
    {
        _source.Result = FakeFriends.List(
            FakeFriends.Create(FakeUidA, displayName: FakeNameA),
            FakeFriends.Create(FakeUidB, "  ", "", null));
        FriendDirectory directory = NewDirectory(NewGuard());

        await directory.LoadAsync();

        ContactName only = Assert.Single(Assert.Single(_contacts.Replaced));
        Assert.Equal(FakeUidA, only.UserId);
    }

    [Fact]
    public async Task A_long_name_is_capped_at_100_characters()
    {
        _source.Result = FakeFriends.List(FakeFriends.Create(FakeUidA, displayName: new string('x', 150)));
        FriendDirectory directory = NewDirectory(NewGuard());

        await directory.LoadAsync();

        Assert.Equal(100, Assert.Single(Assert.Single(_contacts.Replaced)).Name.Length);
    }

    [Fact]
    public async Task The_cap_never_cuts_a_surrogate_pair_in_half()
    {
        // 99 letters then an emoji (two UTF-16 units): a cut at 100 would leave a lone high surrogate.
        string name = new string('x', 99) + "\U0001F600" + "tail";
        _source.Result = FakeFriends.List(FakeFriends.Create(FakeUidA, displayName: name));
        FriendDirectory directory = NewDirectory(NewGuard());

        await directory.LoadAsync();

        string saved = Assert.Single(Assert.Single(_contacts.Replaced)).Name;
        Assert.Equal(99, saved.Length);
        Assert.False(char.IsHighSurrogate(saved[^1]));
    }

    [Fact]
    public async Task An_empty_friend_list_replaces_the_cache_with_an_empty_set()
    {
        _source.Result = FakeFriends.List();
        FriendDirectory directory = NewDirectory(NewGuard());

        await directory.LoadAsync();

        Assert.Empty(Assert.Single(_contacts.Replaced));
        Assert.Equal(FriendsState.Loaded, directory.State);
    }

    // ---- Dispatcher and secrecy ---------------------------------------------------------------------------------

    [Fact]
    public async Task State_changes_are_applied_only_through_the_dispatcher()
    {
        var queue = new QueueingDispatcher();
        FriendDirectory directory = NewDirectory(NewGuard(), queue);

        await directory.LoadAsync();
        Assert.Equal(FriendsState.NotLoaded, directory.State);

        queue.RunAll();

        Assert.Equal(FriendsState.Loaded, directory.State);
    }

    [Fact]
    public async Task Nothing_sensitive_reaches_the_state_or_ToString()
    {
        _source.Exception = new ApiException(321, FakeServerText + FakeUidA + FakeNameA);
        FriendDirectory directory = NewDirectory(NewGuard());
        Assert.Contains(FakeServerText, ((ApiException)_source.Exception).Message, StringComparison.Ordinal);

        await directory.LoadAsync();

        var texts = new List<string> { directory.ToString() ?? "" };
        foreach (PropertyInfo property in typeof(FriendDirectory).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            texts.Add(property.GetValue(directory)?.ToString() ?? "");
        }

        Assert.Equal(nameof(FriendDirectory), directory.ToString());
        foreach (string text in texts)
        {
            Assert.DoesNotContain(FakeServerText, text, StringComparison.Ordinal);
            Assert.DoesNotContain(FakeUidA, text, StringComparison.Ordinal);
            Assert.DoesNotContain(FakeNameA, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_loaded_directory_prints_no_friend_data()
    {
        _source.Result = FakeFriends.List(FakeFriends.Create(FakeUidB, displayName: FakeNameB));
        FriendDirectory directory = NewDirectory(NewGuard());
        Assert.Equal(FakeNameB, _source.Result.Friends[0].DisplayName);

        await directory.LoadAsync();

        string text = directory.ToString() ?? "";
        Assert.DoesNotContain(FakeNameB, text, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeUidB, text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_constructor_rejects_null_arguments()
    {
        ApiThrottleGuard guard = NewGuard();
        Assert.Throws<ArgumentNullException>(() => new FriendDirectory(null!, guard, _contacts, new ImmediateDispatcher()));
        Assert.Throws<ArgumentNullException>(() => new FriendDirectory(_source, null!, _contacts, new ImmediateDispatcher()));
        Assert.Throws<ArgumentNullException>(() => new FriendDirectory(_source, guard, null!, new ImmediateDispatcher()));
        Assert.Throws<ArgumentNullException>(() => new FriendDirectory(_source, guard, _contacts, null!));
    }
}
