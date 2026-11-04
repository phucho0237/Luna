namespace Luna.Core.ViewModels;

/// <summary>Runs an action on the UI thread. View models route every state change through it.</summary>
public interface IUiDispatcher
{
    /// <summary>Queues <paramref name="action"/> to run on the UI thread, in call order.</summary>
    void Post(Action action);
}
