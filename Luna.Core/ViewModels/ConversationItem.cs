using Luna.Protocol.Messaging;

namespace Luna.Core.ViewModels;

/// <summary>
/// One row of the conversation list. <see cref="Name"/> is the cached friend name for a private conversation, and null
/// when it is unknown (a group, or a friend that is not cached): the view then shows a localized fallback.
/// </summary>
public sealed record ConversationItem(ThreadType ThreadType, string ThreadId, string? Name, long LastTimestampMs, int MessageCount)
{
    /// <summary>Never prints the thread id or the name.</summary>
    public override string ToString() => $"ConversationItem {{ ThreadType = {ThreadType}, MessageCount = {MessageCount} }}";
}
