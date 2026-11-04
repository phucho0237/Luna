using Luna.Protocol.Messaging;

namespace Luna.Core.Storage;

/// <summary>One conversation as seen by the local store: the last activity time and the number of non-recalled messages.</summary>
public sealed record ConversationSummary(ThreadType ThreadType, string ThreadId, long LastTimestampMs, int MessageCount)
{
    /// <summary>Never prints the thread id.</summary>
    public override string ToString() => $"ConversationSummary {{ ThreadType = {ThreadType}, MessageCount = {MessageCount} }}";
}
