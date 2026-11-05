using Luna.Core.Realtime;
using Luna.Core.Storage;
using Luna.Protocol.Messaging;

namespace Luna.Core.Tests.Support;

/// <summary>
/// Scriptable <see cref="IRealtimeSource"/>: counts runs and the highest number of runs alive at once, lets the test push
/// messages and states through the callbacks, end a run by itself (like a duplicate connection) or fail it. Never
/// touches the network. A cancelled run returns normally, like the real socket client.
/// </summary>
internal sealed class FakeRealtimeSource : IRealtimeSource
{
    private readonly object _lock = new();
    private readonly List<(int Count, TaskCompletionSource Done)> _waiters = [];
    private readonly List<CancellationToken> _tokens = [];
    private TaskCompletionSource _end = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Action<IReadOnlyList<ChatMessage>>? _onMessages;
    private Action<LiveSyncState>? _onState;
    private int _runCalls;
    private int _alive;
    private int _maxAlive;

    /// <summary>When set, every run throws it right after starting (instead of waiting).</summary>
    public Exception? RunException { get; set; }

    public int RunCalls
    {
        get
        {
            lock (_lock)
            {
                return _runCalls;
            }
        }
    }

    public int MaxAlive
    {
        get
        {
            lock (_lock)
            {
                return _maxAlive;
            }
        }
    }

    public IReadOnlyList<CancellationToken> Tokens
    {
        get
        {
            lock (_lock)
            {
                return [.. _tokens];
            }
        }
    }

    /// <summary>Completes once at least <paramref name="count"/> runs have started.</summary>
    public Task WhenRunCalls(int count)
    {
        lock (_lock)
        {
            if (_runCalls >= count)
            {
                return Task.CompletedTask;
            }

            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((count, done));
            return done.Task;
        }
    }

    /// <summary>Calls the message callback of the latest run, on the calling thread.</summary>
    public void EmitMessages(params ChatMessage[] messages) => Callback(_onMessages)(messages);

    /// <summary>Calls the state callback of the latest run, on the calling thread.</summary>
    public void EmitState(LiveSyncState state) => Callback(_onState)(state);

    /// <summary>Ends the current run by itself, like a duplicate connection does.</summary>
    public void EndRun() => _end.TrySetResult();

    public async Task RunAsync(
        Action<IReadOnlyList<ChatMessage>> onMessages,
        Action<LiveSyncState> onState,
        CancellationToken cancellationToken = default)
    {
        TaskCompletionSource end;
        lock (_lock)
        {
            _runCalls++;
            _alive++;
            _maxAlive = Math.Max(_maxAlive, _alive);
            _tokens.Add(cancellationToken);
            _onMessages = onMessages;
            _onState = onState;
            _end = end = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            foreach ((int count, TaskCompletionSource done) in _waiters.Where(w => w.Count <= _runCalls).ToList())
            {
                done.TrySetResult();
                _waiters.Remove((count, done));
            }
        }

        try
        {
            if (RunException is not null)
            {
                throw RunException;
            }

            await end.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Like the real socket client: a deliberate stop is not an error.
        }
        finally
        {
            lock (_lock)
            {
                _alive--;
            }
        }
    }

    private static Action<T> Callback<T>(Action<T>? callback) =>
        callback ?? throw new InvalidOperationException("The source was not started.");
}

/// <summary>
/// In-memory <see cref="IMessageStore"/>: records every saved batch, can fail or block (until released or cancelled) on
/// save, and serves a scripted conversation list whose calls can each be gated separately.
/// </summary>
internal sealed class FakeMessageStore : IMessageStore
{
    private readonly object _lock = new();
    private readonly List<IReadOnlyList<ChatMessage>> _saved = [];
    private readonly Queue<(Task Gate, IReadOnlyList<ConversationSummary>? Result)> _listGates = new();
    private readonly TaskCompletionSource _saveGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _saveEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _saveCalls;
    private int _listCalls;

    public Exception? SaveException { get; set; }

    /// <summary>When true <see cref="SaveAsync"/> waits for <see cref="ReleaseSave"/> or cancellation.</summary>
    public bool BlockSave { get; set; }

    public IReadOnlyList<ConversationSummary> Conversations { get; set; } = [];

    public Exception? ListException { get; set; }

    /// <summary>Completes when the first save has been entered.</summary>
    public Task SaveEntered => _saveEntered.Task;

    public int SaveCalls => Volatile.Read(ref _saveCalls);

    public int ListCalls => Volatile.Read(ref _listCalls);

    public IReadOnlyList<IReadOnlyList<ChatMessage>> Saved
    {
        get
        {
            lock (_lock)
            {
                return [.. _saved];
            }
        }
    }

    public void ReleaseSave() => _saveGate.TrySetResult();

    /// <summary>The limit the last list call asked for.</summary>
    public int LastListLimit { get; private set; }

    /// <summary>
    /// The next list call (in call order) waits for the returned source and then returns <paramref name="result"/>
    /// (decided when the call starts), or <see cref="Conversations"/> when none is given.
    /// </summary>
    public TaskCompletionSource GateNextList(IReadOnlyList<ConversationSummary>? result = null)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            _listGates.Enqueue((gate.Task, result));
        }

        return gate;
    }

    public async Task<StoreResult> SaveAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _saveCalls);
        _saveEntered.TrySetResult();

        if (BlockSave)
        {
            await _saveGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (SaveException is not null)
        {
            throw SaveException;
        }

        lock (_lock)
        {
            _saved.Add([.. messages]);
        }

        return new StoreResult(messages.Count, 0, 0, 0);
    }

    public Task<IReadOnlyList<StoredMessage>> GetPageAsync(
        ThreadType threadType,
        string threadId,
        MessageCursor? before,
        int limit,
        bool includeRecalled = false,
        bool includeRaw = true,
        CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<StoredMessage>>([]);

    public async Task<IReadOnlyList<ConversationSummary>> ListConversationsAsync(
        int limit, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _listCalls);
        Task? gate = null;
        IReadOnlyList<ConversationSummary>? gated = null;
        lock (_lock)
        {
            LastListLimit = limit;
            if (_listGates.Count > 0)
            {
                (gate, gated) = _listGates.Dequeue();
            }
        }

        if (gate is not null)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (ListException is not null)
        {
            throw ListException;
        }

        return gated ?? Conversations;
    }

    public void Dispose()
    {
    }
}
