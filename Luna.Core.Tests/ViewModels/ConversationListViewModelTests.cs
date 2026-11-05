using Luna.Core.Storage;
using Luna.Core.Tests.Support;
using Luna.Core.ViewModels;
using Luna.Protocol.Messaging;

namespace Luna.Core.Tests.ViewModels;

/// <summary>
/// The conversation list: stored summaries joined with cached friend names, failures that degrade instead of throwing,
/// and the version counter that lets only the latest refresh assign the list in one atomic dispatcher step.
/// </summary>
public sealed class ConversationListViewModelTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private const string FakeUserA = "fake-user-1001";
    private const string FakeUserB = "fake-user-1002";
    private const string FakeGroup = "fake-group-2001";
    private const string FakeNameA = "Fake Name Alpha";

    private readonly FakeMessageStore _store = new();
    private readonly FakeContactStore _contacts = new();

    private ConversationListViewModel Create(IUiDispatcher? ui = null, int limit = ConversationListViewModel.DefaultLimit) =>
        new(_store, _contacts, ui ?? new ImmediateDispatcher(), limit);

    private static ConversationSummary Summary(ThreadType type, string id, long ts, int count = 1) => new(type, id, ts, count);

    [Fact]
    public async Task Starts_empty_and_not_loaded()
    {
        ConversationListViewModel list = Create();

        Assert.Empty(list.Items);
        Assert.False(list.IsLoaded);
        Assert.False(list.HasError);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Refresh_maps_summaries_in_store_order_and_names_private_threads_from_the_contacts()
    {
        _contacts.Seed((FakeUserA, FakeNameA));
        _store.Conversations =
        [
            Summary(ThreadType.User, FakeUserA, 300, 5),
            Summary(ThreadType.Group, FakeGroup, 200, 2),
            Summary(ThreadType.User, FakeUserB, 100, 1),
        ];
        ConversationListViewModel list = Create(limit: 50);

        await list.RefreshAsync();

        Assert.True(list.IsLoaded);
        Assert.False(list.HasError);
        Assert.Equal(50, _store.LastListLimit);
        Assert.Collection(
            list.Items,
            first =>
            {
                Assert.Equal(FakeUserA, first.ThreadId);
                Assert.Equal(FakeNameA, first.Name);
                Assert.Equal(300, first.LastTimestampMs);
                Assert.Equal(5, first.MessageCount);
            },
            second =>
            {
                Assert.Equal(ThreadType.Group, second.ThreadType);
                Assert.Null(second.Name);
            },
            third => Assert.Null(third.Name));
    }

    [Fact]
    public async Task A_group_never_takes_its_name_from_a_contact_with_the_same_id()
    {
        _contacts.Seed((FakeGroup, FakeNameA));
        _store.Conversations = [Summary(ThreadType.Group, FakeGroup, 1)];
        ConversationListViewModel list = Create();

        await list.RefreshAsync();

        Assert.Null(Assert.Single(list.Items).Name);
    }

    [Fact]
    public async Task Unreadable_contacts_degrade_to_unnamed_rows_and_the_list_still_loads()
    {
        _contacts.GetException = new IOException("fake contacts failure");
        _store.Conversations = [Summary(ThreadType.User, FakeUserA, 1)];
        ConversationListViewModel list = Create();

        await list.RefreshAsync();

        Assert.True(list.IsLoaded);
        Assert.False(list.HasError);
        Assert.Null(Assert.Single(list.Items).Name);
    }

    [Fact]
    public async Task A_failing_store_sets_the_error_keeps_the_old_items_and_a_later_refresh_recovers()
    {
        _store.Conversations = [Summary(ThreadType.User, FakeUserA, 1)];
        ConversationListViewModel list = Create();
        await list.RefreshAsync();
        _store.ListException = new IOException("fake store failure");

        await list.RefreshAsync();

        Assert.True(list.HasError);
        Assert.Single(list.Items);

        _store.ListException = null;
        _store.Conversations = [Summary(ThreadType.User, FakeUserA, 1), Summary(ThreadType.User, FakeUserB, 2)];
        await list.RefreshAsync();

        Assert.False(list.HasError);
        Assert.Equal(2, list.Items.Count);
    }

    [Fact]
    public async Task A_failing_first_refresh_is_an_error_and_not_loaded()
    {
        _store.ListException = new InvalidOperationException("fake store failure");
        ConversationListViewModel list = Create();

        await list.RefreshAsync();

        Assert.True(list.HasError);
        Assert.False(list.IsLoaded);
        Assert.Empty(list.Items);
    }

    [Fact]
    public async Task A_cancelled_refresh_assigns_nothing_and_does_not_throw()
    {
        _store.Conversations = [Summary(ThreadType.User, FakeUserA, 1)];
        TaskCompletionSource gate = _store.GateNextList();
        ConversationListViewModel list = Create();
        using var cts = new CancellationTokenSource();

        Task refresh = list.RefreshAsync(cts.Token);
        await cts.CancelAsync();
        await refresh.WaitAsync(Patience);

        Assert.Empty(list.Items);
        Assert.False(list.IsLoaded);
        Assert.False(list.HasError);
        gate.TrySetResult();
    }

    [Fact]
    public async Task Of_two_overlapping_refreshes_only_the_latest_may_assign_even_when_the_earlier_finishes_last()
    {
        IReadOnlyList<ConversationSummary> older = [Summary(ThreadType.User, FakeUserA, 1)];
        IReadOnlyList<ConversationSummary> newer = [Summary(ThreadType.User, FakeUserA, 1), Summary(ThreadType.User, FakeUserB, 2)];
        TaskCompletionSource gateOlder = _store.GateNextList(older);
        TaskCompletionSource gateNewer = _store.GateNextList(newer);
        ConversationListViewModel list = Create();

        Task first = list.RefreshAsync();
        Task second = list.RefreshAsync();
        gateNewer.SetResult();
        await second.WaitAsync(Patience);
        Assert.Equal(2, list.Items.Count);

        gateOlder.SetResult();
        await first.WaitAsync(Patience);

        Assert.Equal(2, list.Items.Count);
    }

    [Fact]
    public async Task An_earlier_refresh_that_finishes_first_is_replaced_by_the_later_one()
    {
        IReadOnlyList<ConversationSummary> older = [Summary(ThreadType.User, FakeUserA, 1)];
        IReadOnlyList<ConversationSummary> newer = [Summary(ThreadType.User, FakeUserA, 1), Summary(ThreadType.User, FakeUserB, 2)];
        TaskCompletionSource gateOlder = _store.GateNextList(older);
        TaskCompletionSource gateNewer = _store.GateNextList(newer);
        ConversationListViewModel list = Create();

        Task first = list.RefreshAsync();
        Task second = list.RefreshAsync();
        gateOlder.SetResult();
        await first.WaitAsync(Patience);
        gateNewer.SetResult();
        await second.WaitAsync(Patience);

        Assert.Equal(2, list.Items.Count);
    }

    [Fact]
    public async Task A_stale_failure_does_not_raise_the_error_over_a_newer_success()
    {
        TaskCompletionSource gateOlder = _store.GateNextList();
        TaskCompletionSource gateNewer = _store.GateNextList([Summary(ThreadType.User, FakeUserA, 1)]);
        ConversationListViewModel list = Create();

        Task first = list.RefreshAsync();
        Task second = list.RefreshAsync();
        gateNewer.SetResult();
        await second.WaitAsync(Patience);
        _store.ListException = new IOException("fake store failure");
        gateOlder.SetResult();
        await first.WaitAsync(Patience);

        Assert.False(list.HasError);
        Assert.Single(list.Items);
    }

    [Fact]
    public async Task The_whole_result_is_assigned_in_one_dispatcher_step_and_items_change_once()
    {
        _store.Conversations = [Summary(ThreadType.User, FakeUserA, 1), Summary(ThreadType.User, FakeUserB, 2)];
        var ui = new QueueingDispatcher();
        ConversationListViewModel list = Create(ui);
        int itemsChanged = 0;
        list.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConversationListViewModel.Items))
            {
                itemsChanged++;
            }
        };

        await list.RefreshAsync();

        Assert.Equal(1, ui.Pending);
        Assert.Empty(list.Items);
        Assert.False(list.IsLoaded);
        ui.RunAll();
        Assert.Equal(2, list.Items.Count);
        Assert.True(list.IsLoaded);
        Assert.Equal(1, itemsChanged);
    }

    [Fact]
    public async Task The_refresh_command_runs_a_refresh()
    {
        _store.Conversations = [Summary(ThreadType.User, FakeUserA, 1)];
        ConversationListViewModel list = Create();

        await list.RefreshCommand.ExecuteAsync(null);

        Assert.Single(list.Items);
    }

    [Fact]
    public void ToString_of_the_item_and_the_view_model_print_no_ids_or_names()
    {
        var item = new ConversationItem(ThreadType.User, FakeUserA, FakeNameA, 1, 3);
        ConversationListViewModel list = Create();

        Assert.DoesNotContain(FakeUserA, item.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(FakeNameA, item.ToString(), StringComparison.Ordinal);
        Assert.Equal(nameof(ConversationListViewModel), list.ToString());
    }
}
