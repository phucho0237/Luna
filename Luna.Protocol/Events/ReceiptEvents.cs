namespace Luna.Protocol.Events;

/// <summary>
/// A message was delivered to (and may have been seen by) the recipient (cmd 502: private, 522: group).
/// <see cref="GroupId"/> exists only on cmd 522.
/// </summary>
public sealed record DeliveryReceipt(
    bool IsGroup,
    string MsgId,
    string? RealMsgId,
    string? GroupId,
    int Seen,
    IReadOnlyList<string> DeliveredUids,
    IReadOnlyList<string> SeenUids) : ChatEvent
{
    /// <summary>Prints only the type and the message id; not the recipient uid or <see cref="GroupId"/>.</summary>
    public override string ToString() => $"DeliveryReceipt {{ IsGroup = {IsGroup}, MsgId = {MsgId} }}";
}

/// <summary>
/// A message was seen (cmd 502 array <c>seens</c>: private; cmd 522 array <c>groupSeens</c>: group).
/// <see cref="ThreadId"/> is <c>idTo</c> (private) or <c>groupId</c> (group); <see cref="SeenUids"/> exists only for groups.
/// </summary>
public sealed record SeenReceipt(
    bool IsGroup,
    string MsgId,
    string? RealMsgId,
    string? ThreadId,
    IReadOnlyList<string> SeenUids) : ChatEvent
{
    /// <summary>Prints only the type and the message id; not the uid or conversation id.</summary>
    public override string ToString() => $"SeenReceipt {{ IsGroup = {IsGroup}, MsgId = {MsgId} }}";
}

/// <summary>
/// A conversation was marked as read (cmd 504, 524, 613). The meaning is inferred from key names, not verified on the live server.
/// </summary>
public sealed record UnreadCleared(
    string IdTo,
    string LastMsgId,
    string? LastCliMsgId,
    bool IsGroup,
    string Timestamp) : ChatEvent
{
    /// <summary>Prints only the conversation type and the last message id; not <see cref="IdTo"/>.</summary>
    public override string ToString() => $"UnreadCleared {{ IsGroup = {IsGroup}, LastMsgId = {LastMsgId} }}";
}
