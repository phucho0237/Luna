using System;
using Luna.Core.ViewModels;
using Microsoft.UI.Dispatching;

namespace Luna.App;

/// <summary>
/// Runs view model updates on the UI thread. It always enqueues, even when already on the UI thread, so the order
/// of earlier queued posts is kept. If the queue is gone (the window was closed) the action is dropped silently.
/// </summary>
public sealed class WinUiDispatcher : IUiDispatcher
{
    private readonly DispatcherQueue _queue;

    public WinUiDispatcher(DispatcherQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        _queue = queue;
    }

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _ = _queue.TryEnqueue(() => action());
    }
}
