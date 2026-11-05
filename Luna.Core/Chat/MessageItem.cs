namespace Luna.Core.Chat;

/// <summary>
/// One message as the chat pane shows it. Immutable. <see cref="SenderName"/> is null when no name is known (the view then
/// shows a localized fallback with <see cref="SenderShortId"/>). <see cref="Text"/> is sensitive: <see cref="ToString"/>
/// never prints it.
/// </summary>
public sealed class MessageItem
{
    public MessageItem(
        long id,
        bool isSelf,
        string? senderName,
        string senderShortId,
        long timestampMs,
        MessageKind kind,
        string? text)
    {
        Id = id;
        IsSelf = isSelf;
        SenderName = senderName;
        SenderShortId = senderShortId ?? string.Empty;
        TimestampMs = timestampMs;
        Kind = kind;
        Text = text;
    }

    /// <summary>The local row id (not a server id).</summary>
    public long Id { get; }

    public bool IsSelf { get; }

    public string? SenderName { get; }

    /// <summary>The last characters of the sender uid, for the fallback label. Never the whole uid.</summary>
    public string SenderShortId { get; }

    public long TimestampMs { get; }

    public MessageKind Kind { get; }

    public string? Text { get; }

    /// <summary>Never prints the text, the sender name or the short id.</summary>
    public override string ToString() => $"MessageItem {{ Id = {Id}, Kind = {Kind}, IsSelf = {IsSelf} }}";
}
