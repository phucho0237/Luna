using Luna.Core.Storage;
using Luna.Protocol.Messaging;

namespace Luna.Core.Tests.Support;

/// <summary>One recorded <see cref="IMessageStore.GetPageAsync"/> call (numbers and flags only).</summary>
internal sealed record PageCall(MessageCursor? Before, int Limit, bool IncludeRecalled, bool IncludeRaw);

/// <summary>
/// Wraps a real store: counts and records page reads, can fail them, and can hold the next calls (in call order) until the
/// test releases them. The gate is taken when the call starts and waited for BEFORE the inner read, so a gated read sees
/// whatever was saved while it waited. Waiting is asynchronous: no thread is blocked.
/// </summary>
internal sealed class GatedMessageStore : IMessageStore
{
    private readonly IMessageStore _inner;
    private readonly object _lock = new();
    private readonly Queue<Task> _gates = new();
    private readonly List<(int Count, TaskCompletionSource Done)> _waiters = [];
    private readonly List<PageCall> _calls = [];

    public GatedMessageStore(IMessageStore inner)
    {
        _inner = inner;
    }

    /// <summary>When set, every page read throws it (after its gate, before the inner read).</summary>
    public Exception? PageException { get; set; }

    public int PageCalls
    {
        get
        {
            lock (_lock)
            {
                return _calls.Count;
            }
        }
    }

    public IReadOnlyList<PageCall> Calls
    {
        get
        {
            lock (_lock)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>The next page read (in call order) waits until the returned source is completed.</summary>
    public TaskCompletionSource GateNextPage()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            _gates.Enqueue(gate.Task);
        }

        return gate;
    }

    /// <summary>Completes once at least <paramref name="count"/> page reads have started.</summary>
    public Task WhenPageCalls(int count)
    {
        lock (_lock)
        {
            if (_calls.Count >= count)
            {
                return Task.CompletedTask;
            }

            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((count, done));
            return done.Task;
        }
    }

    public Task<StoreResult> SaveAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default) =>
        _inner.SaveAsync(messages, cancellationToken);

    public async Task<IReadOnlyList<StoredMessage>> GetPageAsync(
        ThreadType threadType,
        string threadId,
        MessageCursor? before,
        int limit,
        bool includeRecalled = false,
        bool includeRaw = true,
        CancellationToken cancellationToken = default)
    {
        Task? gate = null;
        lock (_lock)
        {
            _calls.Add(new PageCall(before, limit, includeRecalled, includeRaw));
            if (_gates.Count > 0)
            {
                gate = _gates.Dequeue();
            }

            foreach ((int count, TaskCompletionSource done) in _waiters.Where(w => w.Count <= _calls.Count).ToList())
            {
                done.TrySetResult();
                _waiters.Remove((count, done));
            }
        }

        if (gate is not null)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (PageException is not null)
        {
            throw PageException;
        }

        return await _inner.GetPageAsync(threadType, threadId, before, limit, includeRecalled, includeRaw, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<IReadOnlyList<ConversationSummary>> ListConversationsAsync(int limit, CancellationToken cancellationToken = default) =>
        _inner.ListConversationsAsync(limit, cancellationToken);

    public void Dispose()
    {
        // The test owns the inner store.
    }
}

/// <summary>A contact store whose read always fails.</summary>
internal sealed class ThrowingContactStore : IContactStore
{
    public Task ReplaceContactsAsync(IReadOnlyList<ContactName> contacts, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyDictionary<string, string>> GetContactsAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<IReadOnlyDictionary<string, string>>(new InvalidOperationException("fake contact failure"));
}
