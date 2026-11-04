using System.Threading.Channels;

namespace Luna.Protocol.Tests.Support;

/// <summary>Time limit applied to every awaited task in the reconnect tests, so a bug fails instead of hanging.</summary>
internal static class TestTime
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    public static Task Soon(this Task task) => task.WaitAsync(Limit);

    public static Task<T> Soon<T>(this Task<T> task) => task.WaitAsync(Limit);
}

/// <summary>Thread-safe recorder: producers <see cref="Add"/>, the test reads items in order with a time limit.</summary>
internal sealed class AsyncQueue<T>
{
    private readonly Channel<T> _channel = Channel.CreateUnbounded<T>();
    private readonly List<T> _all = [];

    /// <summary>Snapshot of every item added so far (read or not).</summary>
    public IReadOnlyList<T> All
    {
        get
        {
            lock (_all)
            {
                return [.. _all];
            }
        }
    }

    public void Add(T item)
    {
        lock (_all)
        {
            _all.Add(item);
        }

        _channel.Writer.TryWrite(item);
    }

    public Task<T> NextAsync() => _channel.Reader.ReadAsync().AsTask().Soon();
}
