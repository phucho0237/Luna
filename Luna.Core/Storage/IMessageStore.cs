using Luna.Protocol.Messaging;

namespace Luna.Core.Storage;

/// <summary>Local message storage. Storage only: no network and no history sync.</summary>
public interface IMessageStore : IDisposable
{
    /// <summary>
    /// Stores a batch in ONE transaction: either the whole batch is applied or nothing is. Recall notices are never stored;
    /// they mark the matching stored message instead. A notice that arrives before the original message is lost
    /// (counted as skipped).
    /// </summary>
    Task<StoreResult> SaveAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns up to <paramref name="limit"/> (1..500) messages of one thread, newest first, strictly older than
    /// <paramref name="before"/> when given. Recalled messages are hidden unless <paramref name="includeRecalled"/> is true.
    /// </summary>
    Task<IReadOnlyList<StoredMessage>> GetPageAsync(
        ThreadType threadType,
        string threadId,
        MessageCursor? before,
        int limit,
        bool includeRecalled = false,
        CancellationToken cancellationToken = default);

    /// <summary>Lists up to <paramref name="limit"/> (1..500) conversations, most recently active first.</summary>
    Task<IReadOnlyList<ConversationSummary>> ListConversationsAsync(int limit, CancellationToken cancellationToken = default);
}
