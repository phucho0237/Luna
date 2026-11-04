using System.Text.Json;

namespace Luna.Protocol.Messaging;

/// <summary>Conversation type: private (User) or group (Group).</summary>
public enum ThreadType
{
    User = 0,
    Group = 1,
}

/// <summary>Message content. Keeps the original <see cref="Raw"/> JsonElement so no information is lost.</summary>
public abstract record MessageContent(JsonElement Raw)
{
    /// <summary>Tells whether this message is a recall notice.</summary>
    public virtual bool IsUndo => false;

    /// <summary>Does not print the message content to logs.</summary>
    public override string ToString() => GetType().Name;
}

/// <summary>Plain text content.</summary>
public sealed record TextContent(string Text, JsonElement Raw) : MessageContent(Raw)
{
    /// <summary>Does not print the message text to logs.</summary>
    public override string ToString() => $"TextContent {{ Length = {Text.Length} }}";
}

/// <summary>Attachment content (photo, file, link, contact card...).</summary>
public sealed record AttachmentContent(
    JsonElement Raw,
    string? Title = null,
    string? Description = null,
    string? Href = null,
    string? Thumb = null,
    int ChildNumber = 0,
    string? Action = null,
    string? Params = null,
    string? AttachmentType = null) : MessageContent(Raw)
{
    /// <summary>Does not print link details or titles to logs.</summary>
    public override string ToString() => $"AttachmentContent {{ Type = {AttachmentType} }}";
}

/// <summary>
/// Sticker content (<c>msgType == "chat.sticker"</c>). Id, CatId and Type are numbers on the wire, kept as strings.
/// </summary>
public sealed record StickerContent(
    JsonElement Raw,
    string? Id = null,
    string? CatId = null,
    string? Type = null) : MessageContent(Raw)
{
    /// <summary>Does not print sticker data to logs.</summary>
    public override string ToString() => nameof(StickerContent);
}

/// <summary>
/// Content that is an object but whose shape is not recognized yet. Keeps <see cref="MessageContent.Raw"/> so no information is lost.
/// </summary>
public sealed record UnknownContent(JsonElement Raw) : MessageContent(Raw)
{
    /// <summary>Does not print the data to logs.</summary>
    public override string ToString() => nameof(UnknownContent);
}

/// <summary>Content of a message recall notice (<c>content.deleteMsg</c>).</summary>
public sealed record UndoContent(
    JsonElement Raw,
    string? DeleteMsg = null,
    string? GlobalMsgId = null,
    string? CliMsgId = null,
    string? SrcId = null,
    string? DestId = null) : MessageContent(Raw)
{
    public override bool IsUndo => true;

    /// <summary>Prints only the id of the recalled message, not its content.</summary>
    public override string ToString() => $"UndoContent {{ CliMsgId = {CliMsgId}, GlobalMsgId = {GlobalMsgId} }}";
}

/// <summary>Quote of another message (when replying to a message).</summary>
public sealed record MessageQuote(
    string OwnerId,
    string CliMsgId,
    string GlobalMsgId,
    int CliMsgType,
    string Timestamp,
    string Msg,
    string? Attach,
    string? FromD,
    int Ttl)
{
    /// <summary>Does not print the quoted content or the sender uid to logs.</summary>
    public override string ToString() =>
        $"MessageQuote {{ CliMsgId = {CliMsgId}, GlobalMsgId = {GlobalMsgId} }}";
}

/// <summary>Position of a member mention (@mention) in a group message.</summary>
public sealed record MessageMention(
    string Uid,
    int Pos,
    int Len,
    int Type)
{
    /// <summary>Does not print the mentioned member's uid to logs.</summary>
    public override string ToString() =>
        $"MessageMention {{ Pos = {Pos}, Len = {Len}, Type = {Type} }}";
}

/// <summary>Extended properties for message formatting (color, size...).</summary>
public sealed record MessagePropertyExt(
    int Color,
    int Size,
    int Type,
    int SubType,
    string? Ext)
{
    /// <summary>Does not print <see cref="Ext"/> to logs because it may contain formatting data of the message content.</summary>
    public override string ToString() =>
        $"MessagePropertyExt {{ Color = {Color}, Size = {Size}, Type = {Type}, SubType = {SubType} }}";
}

/// <summary>Extended parameters (unread count, content type...).</summary>
public sealed record MessageParamsExt(
    int CountUnread,
    int ContainType,
    int PlatformType);

/// <summary>
/// Represents a chat message (private or group message).
/// Every ID is stored as a string to avoid losing precision with 64-bit or larger numbers.
/// </summary>
public sealed record ChatMessage(
    ThreadType ThreadType,
    string ThreadId,
    bool IsSelf,
    string ActionId,
    string MsgId,
    string CliMsgId,
    string MsgType,
    string UidFrom,
    string IdTo,
    string DisplayName,
    string Timestamp,
    int Status,
    MessageContent Content,
    string? Notify,
    int Ttl,
    string? UserId,
    string? Uin,
    string? TopOut,
    string? TopOutTimeOut,
    string? TopOutImprTimeOut,
    MessagePropertyExt? PropertyExt,
    MessageParamsExt? ParamsExt,
    int Cmd,
    int St,
    int At,
    string? RealMsgId,
    MessageQuote? Quote,
    IReadOnlyList<MessageMention> Mentions,
    string? PreviewThumb,
    JsonElement Raw)
{
    /// <summary>The message text if it is TextContent, otherwise null.</summary>
    public string? Text => Content is TextContent text ? text.Text : null;

    /// <summary>Tells whether this message is a message recall notice.</summary>
    public bool IsUndo => Content.IsUndo;

    /// <summary>
    /// Never prints the message content, the quote, or the sensitive uid/ThreadId to logs.
    /// </summary>
    public override string ToString() =>
        $"ChatMessage {{ MsgId = {MsgId}, CliMsgId = {CliMsgId}, ThreadType = {ThreadType}, IsSelf = {IsSelf}, MsgType = {MsgType}, IsUndo = {IsUndo} }}";
}
