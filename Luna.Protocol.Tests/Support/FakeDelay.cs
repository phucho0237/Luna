namespace Luna.Protocol.Tests.Support;

/// <summary>One pending wait requested through <see cref="FakeDelay"/>.</summary>
internal sealed class DelayRequest(TimeSpan duration)
{
    private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TimeSpan Duration { get; } = duration;

    public bool IsCanceled => _tcs.Task.IsCanceled;

    public Task Task => _tcs.Task;

    /// <summary>Lets the waiter continue, as if the time had passed.</summary>
    public void Complete() => _tcs.TrySetResult();

    internal void Cancel(CancellationToken ct) => _tcs.TrySetCanceled(ct);
}

/// <summary>
/// Fake clock for SocketClient: never waits for real. Each call becomes a <see cref="DelayRequest"/> that completes only
/// when the test says so and is canceled with the token. Requests of the ping interval (15 s) and the reconnect backoff
/// go to separate queues so the two loops cannot be confused (the backoff is never exactly 15 s).
/// </summary>
internal sealed class FakeDelay
{
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(15);

    public AsyncQueue<DelayRequest> Pings { get; } = new();

    public AsyncQueue<DelayRequest> Backoffs { get; } = new();

    public Task Delay(TimeSpan duration, CancellationToken ct)
    {
        var request = new DelayRequest(duration);
        if (ct.IsCancellationRequested)
        {
            request.Cancel(ct);
        }
        else
        {
            ct.Register(() => request.Cancel(ct));
        }

        (duration == PingInterval ? Pings : Backoffs).Add(request);
        return request.Task;
    }
}
