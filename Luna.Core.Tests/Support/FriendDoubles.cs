using Luna.Core.Friends;
using Luna.Core.Storage;
using Luna.Protocol.Api;

namespace Luna.Core.Tests.Support;

/// <summary>
/// Scriptable <see cref="IFriendListSource"/>: counts calls, can run a probe on entry, throw a scripted exception or
/// block until released. Never touches the network.
/// </summary>
internal sealed class FakeFriendListSource : IFriendListSource
{
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _calls;

    public FriendListResult Result { get; set; } = new([], 0, 1);

    public Exception? Exception { get; set; }

    /// <summary>When true the call waits for <see cref="Release"/> or cancellation.</summary>
    public bool Block { get; set; }

    /// <summary>Runs first on every call, before anything else (used to look at the throttle file at that moment).</summary>
    public Action? OnEnter { get; set; }

    public int Calls => Volatile.Read(ref _calls);

    public Task Entered => _entered.Task;

    public void Release() => _gate.TrySetResult();

    public async Task<FriendListResult> GetAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        OnEnter?.Invoke();
        _entered.TrySetResult();

        if (Block)
        {
            await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (Exception is not null)
        {
            throw Exception;
        }

        return Result;
    }
}

/// <summary>In-memory <see cref="IContactStore"/> that records every replace and can fail or block on request.</summary>
internal sealed class FakeContactStore : IContactStore
{
    private readonly object _lock = new();
    private readonly List<IReadOnlyList<ContactName>> _replaced = [];
    private readonly TaskCompletionSource _getGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Dictionary<string, string> _contacts = new(StringComparer.Ordinal);

    public Exception? ReplaceException { get; set; }

    public Exception? GetException { get; set; }

    /// <summary>When true <see cref="GetContactsAsync"/> waits for <see cref="ReleaseGet"/> or cancellation.</summary>
    public bool BlockGet { get; set; }

    public int GetCalls { get; private set; }

    public IReadOnlyList<IReadOnlyList<ContactName>> Replaced
    {
        get
        {
            lock (_lock)
            {
                return [.. _replaced];
            }
        }
    }

    public void ReleaseGet() => _getGate.TrySetResult();

    public void Seed(params (string UserId, string Name)[] contacts)
    {
        lock (_lock)
        {
            _contacts = contacts.ToDictionary(c => c.UserId, c => c.Name, StringComparer.Ordinal);
        }
    }

    public Task ReplaceContactsAsync(IReadOnlyList<ContactName> contacts, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ReplaceException is not null)
        {
            return Task.FromException(ReplaceException);
        }

        lock (_lock)
        {
            _replaced.Add([.. contacts]);
            _contacts = contacts.ToDictionary(c => c.UserId, c => c.Name, StringComparer.Ordinal);
        }

        return Task.CompletedTask;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetContactsAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            GetCalls++;
        }

        if (BlockGet)
        {
            await _getGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (GetException is not null)
        {
            throw GetException;
        }

        lock (_lock)
        {
            return new Dictionary<string, string>(_contacts, StringComparer.Ordinal);
        }
    }
}

internal static class FakeFriends
{
    public static Friend Create(string userId, string? username = null, string? displayName = null, string? profileName = null) =>
        new(userId, username, displayName, profileName, null, null, null, null, true, false, true, null, null, null);

    public static FriendListResult List(params Friend[] friends) => new(friends, 0, 1);
}
