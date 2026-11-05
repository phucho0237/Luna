using Luna.Core.Realtime;
using Luna.Core.Tests.Support;
using Luna.Protocol.Messaging;

namespace Luna.Core.Tests.Realtime;

/// <summary>
/// Rules of <see cref="LiveSyncService"/>: at most one connection, no automatic restart after a duplicate connection,
/// the socket callback never waits for storage, every state change goes through the dispatcher, and the shutdown order
/// (cancel the source, drain and complete the writer) is safe in every interleaving. No test touches the network.
/// </summary>
public sealed class LiveSyncServiceTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly FakeRealtimeSource _source = new();
    private readonly FakeMessageStore _store = new();

    private LiveSyncService Create(Luna.Core.ViewModels.IUiDispatcher? ui = null) =>
        new(_source, _store, ui ?? new ImmediateDispatcher());

    private static ChatMessage Msg(string id) =>
        FakeMessages.Text(ThreadType.User, "fake-thread-1", id, id + "1", "1700000000" + id);

    private static async Task Eventually(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(Patience);
        while (!condition())
        {
            await Task.Delay(5, cts.Token);
        }
    }

    // ---- One connection ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Start_runs_the_source_once_and_a_second_start_does_nothing()
    {
        using LiveSyncService service = Create();

        service.Start();
        service.Start();
        await _source.WhenRunCalls(1).WaitAsync(Patience);

        Assert.Equal(1, _source.RunCalls);
        Assert.Equal(1, _source.MaxAlive);
        await service.StopAsync();
    }

    [Fact]
    public async Task Concurrent_starts_still_open_one_connection()
    {
        using LiveSyncService service = Create();

        var starts = Enumerable.Range(0, 8)
            .Select(_ => Task.Factory.StartNew(service.Start, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default))
            .ToArray();
        await Task.WhenAll(starts).WaitAsync(Patience);
        await _source.WhenRunCalls(1).WaitAsync(Patience);

        Assert.Equal(1, _source.RunCalls);
        Assert.Equal(1, _source.MaxAlive);
        await service.StopAsync();
    }

    [Fact]
    public async Task Reconnect_while_the_source_is_running_does_not_open_a_second_connection()
    {
        using LiveSyncService service = Create();
        service.Start();
        await _source.WhenRunCalls(1).WaitAsync(Patience);

        _source.EmitState(LiveSyncState.Connected);
        Assert.Equal(LiveSyncState.Connected, service.State);

        service.Reconnect();

        // A second run would announce Connecting at once (the state is posted synchronously under the service lock).
        Assert.Equal(LiveSyncState.Connected, service.State);
        await service.StopAsync();
        Assert.Equal(1, _source.RunCalls);
        Assert.Equal(1, _source.MaxAlive);
    }

    // ---- State ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_state_follows_the_source_only_through_the_dispatcher()
    {
        var ui = new QueueingDispatcher();
        using LiveSyncService service = Create(ui);
        service.Start();
        await _source.WhenRunCalls(1).WaitAsync(Patience);
        ui.RunAll();
        Assert.Equal(LiveSyncState.Connecting, service.State);

        _source.EmitState(LiveSyncState.Connected);

        Assert.Equal(LiveSyncState.Connecting, service.State);
        Assert.True(ui.Pending > 0);
        ui.RunAll();
        Assert.Equal(LiveSyncState.Connected, service.State);
        await service.StopAsync();
        ui.RunAll();
    }

    [Fact]
    public async Task A_duplicate_connection_never_restarts_by_itself_and_reconnect_is_explicit()
    {
        using LiveSyncService service = Create();
        service.Start();
        await _source.WhenRunCalls(1).WaitAsync(Patience);
        _source.EmitState(LiveSyncState.DuplicateConnection);
        _source.EndRun();
        await Eventually(() => service.CanReconnect);
        for (int i = 0; i < 20; i++)
        {
            await Task.Yield();
        }

        Assert.Equal(1, _source.RunCalls);
        Assert.Equal(LiveSyncState.DuplicateConnection, service.State);

        service.Reconnect();
        await _source.WhenRunCalls(2).WaitAsync(Patience);

        Assert.Equal(2, _source.RunCalls);
        Assert.Equal(1, _source.MaxAlive);
        Assert.Equal(LiveSyncState.Connecting, service.State);
        await service.StopAsync();
    }

    [Fact]
    public async Task A_failing_source_ends_as_disconnected_without_throwing_or_restarting()
    {
        _source.RunException = new InvalidOperationException("fake source failure");
        using LiveSyncService service = Create();

        service.Start();
        await _source.WhenRunCalls(1).WaitAsync(Patience);
        await Eventually(() => service.State == LiveSyncState.Disconnected && service.CanReconnect);
        for (int i = 0; i < 20; i++)
        {
            await Task.Yield();
        }

        Assert.Equal(1, _source.RunCalls);

        _source.RunException = null;
        service.Reconnect();
        await _source.WhenRunCalls(2).WaitAsync(Patience);
        Assert.Equal(1, _source.MaxAlive);
        await service.StopAsync();
    }

    // ---- Saving --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Messages_from_the_source_are_saved_and_announced_on_the_dispatcher()
    {
        var ui = new QueueingDispatcher();
        using LiveSyncService service = Create(ui);
        int announced = 0;
        service.MessagesStored += (_, _) => announced++;
        service.Start();
        await _source.WhenRunCalls(1).WaitAsync(Patience);

        _source.EmitMessages(Msg("1001"), Msg("1002"));
        await Eventually(() => _store.Saved.Count == 1);
        await Eventually(() => ui.Pending > 0);
        Assert.Equal(0, announced);
        ui.RunAll();

        Assert.Equal(2, _store.Saved[0].Count);
        Assert.Equal(1, announced);
        await service.StopAsync();
    }

    [Fact]
    public async Task The_source_callback_never_waits_for_the_store()
    {
        _store.BlockSave = true;
        using LiveSyncService service = Create();
        service.Start();
        await _source.WhenRunCalls(1).WaitAsync(Patience);

        // If the callback waited for SaveAsync, this would never finish while the store is blocked.
        await Task.Factory.StartNew(
            () =>
            {
                _source.EmitMessages(Msg("2001"));
                _source.EmitMessages(Msg("2002"));
                _source.EmitMessages(Msg("2003"));
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).WaitAsync(Patience);

        await _store.SaveEntered.WaitAsync(Patience);
        Assert.Empty(_store.Saved);
        _store.ReleaseSave();
        await Eventually(() => _store.Saved.Count == 3);
        Assert.Equal("2001", _store.Saved[0][0].MsgId);
        Assert.Equal("2003", _store.Saved[2][0].MsgId);
        await service.StopAsync();
    }

    [Fact]
    public async Task A_failed_save_is_counted_and_later_batches_are_still_saved()
    {
        _store.SaveException = new IOException("fake disk failure");
        using LiveSyncService service = Create();
        service.Start();
        await _source.WhenRunCalls(1).WaitAsync(Patience);

        _source.EmitMessages(Msg("3001"));
        await Eventually(() => service.SaveFailures == 1);
        _store.SaveException = null;
        _source.EmitMessages(Msg("3002"));
        await Eventually(() => _store.Saved.Count == 1);

        Assert.Equal(1, service.SaveFailures);
        Assert.Equal("3002", _store.Saved[0][0].MsgId);
        await service.StopAsync();
    }

    // ---- Bounded queue -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_full_queue_drops_batches_counts_them_and_the_callback_never_waits()
    {
        _store.BlockSave = true;
        using LiveSyncService service = Create();
        service.Start();
        await _source.WhenRunCalls(1).WaitAsync(Patience);
        ChatMessage message = Msg("9001");

        // The first batch is taken by the writer and blocks in SaveAsync, so the queue itself starts empty.
        _source.EmitMessages(message);
        await _store.SaveEntered.WaitAsync(Patience);
        int extra = 6;

        // If the callback ever waited for room or for the store, this would not finish while the store is gated.
        await Task.Factory.StartNew(
            () =>
            {
                for (int i = 0; i < LiveSyncService.QueueCapacity + extra; i++)
                {
                    _source.EmitMessages(message);
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).WaitAsync(Patience);

        Assert.True(service.DroppedBatches > 0);
        Assert.Equal(extra, service.DroppedBatches);

        _store.ReleaseSave();
        await Eventually(() => _store.Saved.Count == 1 + LiveSyncService.QueueCapacity);

        // The writer keeps working after the overflow.
        _source.EmitMessages(Msg("9002"));
        await Eventually(() => _store.Saved.Count == 2 + LiveSyncService.QueueCapacity);
        Assert.Equal(extra, service.DroppedBatches);
        await service.StopAsync();
    }

    [Fact]
    public async Task A_batch_written_after_the_queue_was_completed_is_not_counted_as_dropped()
    {
        using LiveSyncService service = Create();
        service.Start();
        await _source.WhenRunCalls(1).WaitAsync(Patience);
        await service.StopAsync();

        _source.EmitMessages(Msg("9101"));
        await Task.Yield();

        Assert.Equal(0, service.DroppedBatches);
        Assert.Empty(_store.Saved);
    }

    [Fact]
    public void Dropped_batches_start_at_zero()
    {
        using LiveSyncService service = Create();

        Assert.Equal(0, service.DroppedBatches);
    }

    // ---- Shutdown ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Stop_cancels_the_source_drains_queued_batches_and_ends_stopped()
    {
        _store.BlockSave = true;
        using LiveSyncService service = Create();
        service.Start();
        await _source.WhenRunCalls(1).WaitAsync(Patience);
        _source.EmitMessages(Msg("4001"));
        _source.EmitMessages(Msg("4002"));
        await _store.SaveEntered.WaitAsync(Patience);

        Task stop = service.StopAsync();
        await Eventually(() => _source.Tokens[0].IsCancellationRequested);
        _store.ReleaseSave();
        await stop.WaitAsync(Patience);

        Assert.Equal(2, _store.Saved.Count);
        Assert.Equal(LiveSyncState.Stopped, service.State);
    }

    [Fact]
    public async Task Stop_twice_and_stop_without_start_are_safe()
    {
        using LiveSyncService service = Create();
        await service.StopAsync();
        service.Start();
        await _source.WhenRunCalls(1).WaitAsync(Patience);

        Task first = service.StopAsync();
        Task second = service.StopAsync();
        await Task.WhenAll(first, second).WaitAsync(Patience);
        await service.StopAsync();

        Assert.Equal(LiveSyncState.Stopped, service.State);
        Assert.Equal(1, _source.RunCalls);
        Assert.True(_source.Tokens[0].IsCancellationRequested);
    }

    [Fact]
    public async Task Stop_while_a_save_is_blocked_ends_when_the_drain_token_is_cancelled()
    {
        _store.BlockSave = true;
        using LiveSyncService service = Create();
        service.Start();
        await _source.WhenRunCalls(1).WaitAsync(Patience);
        _source.EmitMessages(Msg("5001"));
        await _store.SaveEntered.WaitAsync(Patience);
        using var drain = new CancellationTokenSource();

        Task stop = service.StopAsync(drain.Token);
        Assert.False(stop.IsCompleted);
        await drain.CancelAsync();
        await stop.WaitAsync(Patience);

        Assert.Equal(LiveSyncState.Stopped, service.State);
        Assert.Empty(_store.Saved);
        _store.ReleaseSave();
    }

    [Fact]
    public async Task Messages_and_states_arriving_after_stop_are_ignored()
    {
        using LiveSyncService service = Create();
        service.Start();
        await _source.WhenRunCalls(1).WaitAsync(Patience);
        await service.StopAsync();

        _source.EmitMessages(Msg("6001"));
        _source.EmitState(LiveSyncState.Connected);
        await Task.Yield();

        Assert.Empty(_store.Saved);
        Assert.Equal(LiveSyncState.Stopped, service.State);
    }

    [Fact]
    public async Task Start_after_stop_runs_again_with_one_connection()
    {
        using LiveSyncService service = Create();
        service.Start();
        await _source.WhenRunCalls(1).WaitAsync(Patience);
        await service.StopAsync();

        service.Start();
        await _source.WhenRunCalls(2).WaitAsync(Patience);
        _source.EmitMessages(Msg("7001"));
        await Eventually(() => _store.Saved.Count == 1);

        Assert.Equal(1, _source.MaxAlive);
        await service.StopAsync();
    }

    [Fact]
    public async Task Dispose_twice_is_safe_and_start_after_dispose_does_nothing()
    {
        LiveSyncService service = Create();
        service.Start();
        await _source.WhenRunCalls(1).WaitAsync(Patience);

        service.Dispose();
        service.Dispose();
        service.Start();
        service.Reconnect();
        await service.StopAsync();

        Assert.Equal(1, _source.RunCalls);
        await Eventually(() => _source.Tokens[0].IsCancellationRequested);
    }

    [Fact]
    public async Task Dispose_while_a_save_is_blocked_does_not_throw_or_hang()
    {
        _store.BlockSave = true;
        LiveSyncService service = Create();
        service.Start();
        await _source.WhenRunCalls(1).WaitAsync(Patience);
        _source.EmitMessages(Msg("8001"));
        await _store.SaveEntered.WaitAsync(Patience);

        service.Dispose();

        await Eventually(() => _source.Tokens[0].IsCancellationRequested);
        _store.ReleaseSave();
    }

    [Fact]
    public void ToString_prints_the_type_name_only()
    {
        using LiveSyncService service = Create();

        Assert.Equal(nameof(LiveSyncService), service.ToString());
    }
}
