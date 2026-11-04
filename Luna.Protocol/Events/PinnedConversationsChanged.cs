namespace Luna.Protocol.Events;

/// <summary>
/// The pinned conversation list changed (cmd 601, <c>pinned_convers/update</c>). Both pinning and unpinning raise this event.
/// The meaning of the values of <see cref="Tab"/> and <see cref="Action"/> is not verified on the live server.
/// </summary>
public sealed record PinnedConversationsChanged(int Tab, int Action, IReadOnlyList<string> PinIds) : ChatEvent
{
    /// <summary>Prints only the count of <see cref="PinIds"/>; not conversation ids.</summary>
    public override string ToString() => $"PinnedConversationsChanged {{ PinCount = {PinIds.Count} }}";
}
