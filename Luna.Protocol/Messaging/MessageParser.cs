using System.Text.Json;
using Luna.Protocol.Realtime;

namespace Luna.Protocol.Messaging;

/// <summary>
/// Result of parsing a message event from the socket.
/// </summary>
public sealed record ParseResult(IReadOnlyList<ChatMessage> Messages, int Skipped)
{
    public static readonly ParseResult Empty = new([], 0);

    /// <summary>Prints counts only, never the messages.</summary>
    public override string ToString() => $"ParseResult {{ Count = {Messages.Count}, Skipped = {Skipped} }}";
}

/// <summary>
/// Parses socket events (cmd 501: private message, cmd 521: group message) into a list of <see cref="ChatMessage"/>.
/// </summary>
public sealed class MessageParser
{
    public const ushort CmdUserMessage = 501;
    public const ushort CmdGroupMessage = 521;

    private static readonly JsonElement NullElement = JsonDocument.Parse("null").RootElement.Clone();

    private readonly string? _selfUid;
    private readonly Action<string>? _trace;

    /// <summary>
    /// Creates the message parser with an optional own-account identifier (<paramref name="selfUid"/>) and an error trace function (<paramref name="trace"/>).
    /// </summary>
    public MessageParser(string? selfUid = null, Action<string>? trace = null)
    {
        _selfUid = selfUid;
        _trace = trace;
    }

    /// <summary>
    /// Parses one socket event into a list of messages and the number of skipped messages.
    /// If the event is not cmd 501 or 521, returns <see cref="ParseResult.Empty"/>.
    /// A message that fails to parse is skipped and does not spoil the whole batch.
    /// </summary>
    public ParseResult Parse(SocketEvent socketEvent)
    {
        ArgumentNullException.ThrowIfNull(socketEvent);

        if (socketEvent.Cmd != CmdUserMessage && socketEvent.Cmd != CmdGroupMessage)
        {
            return ParseResult.Empty;
        }

        ThreadType threadType = socketEvent.Cmd == CmdGroupMessage ? ThreadType.Group : ThreadType.User;
        string arrayName = socketEvent.Cmd == CmdGroupMessage ? "groupMsgs" : "msgs";

        JsonElement root = socketEvent.Payload;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return ParseResult.Empty;
        }

        JsonElement container = root;
        if (root.TryGetProperty("data", out JsonElement dataElement)
            && dataElement.ValueKind == JsonValueKind.Object)
        {
            container = dataElement;
        }

        if (!container.TryGetProperty(arrayName, out JsonElement arrayElement)
            || arrayElement.ValueKind != JsonValueKind.Array)
        {
            return ParseResult.Empty;
        }

        int skipped = 0;
        var messages = new List<ChatMessage>();
        foreach (JsonElement item in arrayElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                skipped++;
                continue;
            }

            try
            {
                ChatMessage message = ParseSingle(item, threadType, _selfUid);
                messages.Add(message);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or JsonException)
            {
                skipped++;
                _trace?.Invoke($"Skipped a message that could not be parsed: {ex.GetType().Name}");
            }
        }

        return new ParseResult(messages, skipped);
    }

    /// <summary>Static helper to parse an event quickly.</summary>
    public static ParseResult ParseEvent(SocketEvent socketEvent, string? selfUid = null) =>
        new MessageParser(selfUid).Parse(socketEvent);

    private static ChatMessage ParseSingle(JsonElement item, ThreadType threadType, string? selfUid)
    {
        string uidFrom = JsonFields.ReadScalar(item, "uidFrom") ?? "";
        string idTo = JsonFields.ReadScalar(item, "idTo") ?? "";
        bool isSelf = string.Equals(uidFrom, "0", StringComparison.Ordinal)
            || (!string.IsNullOrEmpty(selfUid)
                && !string.IsNullOrEmpty(uidFrom)
                && string.Equals(uidFrom, selfUid, StringComparison.Ordinal));

        string threadId = threadType == ThreadType.Group
            ? idTo
            : (isSelf ? idTo : uidFrom);

        string actionId = JsonFields.ReadScalar(item, "actionId") ?? "";
        string msgId = JsonFields.ReadScalar(item, "msgId") ?? "";
        string cliMsgId = JsonFields.ReadScalar(item, "cliMsgId") ?? "";
        string msgType = JsonFields.ReadScalar(item, "msgType") ?? "";
        string displayName = JsonFields.ReadScalar(item, "dName") ?? "";
        string timestamp = JsonFields.ReadScalar(item, "ts") ?? "";
        int status = JsonFields.ReadInt32(item, "status");
        string? notify = JsonFields.ReadScalar(item, "notify");
        int ttl = JsonFields.ReadInt32(item, "ttl");
        string? userId = JsonFields.ReadScalar(item, "userId");
        string? uin = JsonFields.ReadScalar(item, "uin");
        string? topOut = JsonFields.ReadScalar(item, "topOut");
        string? topOutTimeOut = JsonFields.ReadScalar(item, "topOutTimeOut");
        string? topOutImprTimeOut = JsonFields.ReadScalar(item, "topOutImprTimeOut");
        int cmd = JsonFields.ReadInt32(item, "cmd");
        int st = JsonFields.ReadInt32(item, "st");
        int at = JsonFields.ReadInt32(item, "at");
        string? realMsgId = JsonFields.ReadScalar(item, "realMsgId");
        string? previewThumb = JsonFields.ReadScalar(item, "previewThumb");

        MessageContent content = ParseContent(item, msgType);
        MessagePropertyExt? propertyExt = ParsePropertyExt(item);
        MessageParamsExt? paramsExt = ParseParamsExt(item);
        MessageQuote? quote = ParseQuote(item);
        IReadOnlyList<MessageMention> mentions = ParseMentions(item);

        return new ChatMessage(
            threadType,
            threadId,
            isSelf,
            actionId,
            msgId,
            cliMsgId,
            msgType,
            uidFrom,
            idTo,
            displayName,
            timestamp,
            status,
            content,
            notify,
            ttl,
            userId,
            uin,
            topOut,
            topOutTimeOut,
            topOutImprTimeOut,
            propertyExt,
            paramsExt,
            cmd,
            st,
            at,
            realMsgId,
            quote,
            mentions,
            previewThumb,
            item.Clone());
    }

    private static readonly string[] AttachmentKeys = ["title", "description", "href", "thumb", "params"];

    private static MessageContent ParseContent(JsonElement item, string msgType)
    {
        if (!item.TryGetProperty("content", out JsonElement contentElement)
            || contentElement.ValueKind == JsonValueKind.Null)
        {
            return new TextContent(string.Empty, NullElement);
        }

        if (contentElement.ValueKind == JsonValueKind.String)
        {
            return new TextContent(contentElement.GetString() ?? string.Empty, contentElement.Clone());
        }

        if (contentElement.ValueKind == JsonValueKind.Object)
        {
            if (contentElement.TryGetProperty("deleteMsg", out JsonElement deleteMsgElement))
            {
                string? deleteMsg = JsonFields.ReadScalar(deleteMsgElement);
                string? globalMsgId = JsonFields.ReadScalar(contentElement, "globalMsgId");
                string? cliMsgId = JsonFields.ReadScalar(contentElement, "cliMsgId");
                string? srcId = JsonFields.ReadScalar(contentElement, "srcId");
                string? destId = JsonFields.ReadScalar(contentElement, "destId");

                return new UndoContent(contentElement.Clone(), deleteMsg, globalMsgId, cliMsgId, srcId, destId);
            }

            if (string.Equals(msgType, MessageTypes.Sticker, StringComparison.Ordinal))
            {
                return new StickerContent(
                    contentElement.Clone(),
                    JsonFields.ReadScalar(contentElement, "id"),
                    JsonFields.ReadScalar(contentElement, "catId"),
                    JsonFields.ReadScalar(contentElement, "type"));
            }

            // Treated as an attachment only when it has at least one characteristic key; any other object keeps Raw as is.
            if (!AttachmentKeys.Any(key => contentElement.TryGetProperty(key, out _)))
            {
                return new UnknownContent(contentElement.Clone());
            }

            string? title = JsonFields.ReadScalar(contentElement, "title");
            string? description = JsonFields.ReadScalar(contentElement, "description");
            string? href = JsonFields.ReadScalar(contentElement, "href");
            string? thumb = JsonFields.ReadScalar(contentElement, "thumb");
            int childNumber = JsonFields.ReadInt32(contentElement, "childnumber");
            string? action = JsonFields.ReadScalar(contentElement, "action");
            string? @params = JsonFields.ReadScalarOrJson(contentElement, "params");
            string? type = JsonFields.ReadScalar(contentElement, "type");

            return new AttachmentContent(
                contentElement.Clone(),
                title,
                description,
                href,
                thumb,
                childNumber,
                action,
                @params,
                type);
        }

        return new TextContent(contentElement.GetRawText(), contentElement.Clone());
    }

    private static MessagePropertyExt? ParsePropertyExt(JsonElement item)
    {
        if (!item.TryGetProperty("propertyExt", out JsonElement propElement)
            || propElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        int color = JsonFields.ReadInt32(propElement, "color");
        int size = JsonFields.ReadInt32(propElement, "size");
        int type = JsonFields.ReadInt32(propElement, "type");
        int subType = JsonFields.ReadInt32(propElement, "subType");
        string? ext = JsonFields.ReadScalar(propElement, "ext");

        return new MessagePropertyExt(color, size, type, subType, ext);
    }

    private static MessageParamsExt? ParseParamsExt(JsonElement item)
    {
        if (!item.TryGetProperty("paramsExt", out JsonElement paramsElement)
            || paramsElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        int countUnread = JsonFields.ReadInt32(paramsElement, "countUnread");
        int containType = JsonFields.ReadInt32(paramsElement, "containType");
        int platformType = JsonFields.ReadInt32(paramsElement, "platformType");

        return new MessageParamsExt(countUnread, containType, platformType);
    }

    private static MessageQuote? ParseQuote(JsonElement item)
    {
        if (!item.TryGetProperty("quote", out JsonElement quoteElement)
            || quoteElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string ownerId = JsonFields.ReadScalar(quoteElement, "ownerId") ?? string.Empty;
        string cliMsgId = JsonFields.ReadScalar(quoteElement, "cliMsgId") ?? string.Empty;
        string globalMsgId = JsonFields.ReadScalar(quoteElement, "globalMsgId") ?? string.Empty;
        int cliMsgType = JsonFields.ReadInt32(quoteElement, "cliMsgType");
        string ts = JsonFields.ReadScalar(quoteElement, "ts") ?? string.Empty;
        string msg = JsonFields.ReadScalar(quoteElement, "msg") ?? string.Empty;
        string? attach = JsonFields.ReadScalarOrJson(quoteElement, "attach");
        string? fromD = JsonFields.ReadScalar(quoteElement, "fromD");
        int ttl = JsonFields.ReadInt32(quoteElement, "ttl");

        return new MessageQuote(ownerId, cliMsgId, globalMsgId, cliMsgType, ts, msg, attach, fromD, ttl);
    }

    private static IReadOnlyList<MessageMention> ParseMentions(JsonElement item)
    {
        if (!item.TryGetProperty("mentions", out JsonElement mentionsElement)
            || mentionsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<MessageMention>();
        foreach (JsonElement m in mentionsElement.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string uid = JsonFields.ReadScalar(m, "uid") ?? string.Empty;
            int pos = JsonFields.ReadInt32(m, "pos");
            int len = JsonFields.ReadInt32(m, "len");
            int type = JsonFields.ReadInt32(m, "type");

            list.Add(new MessageMention(uid, pos, len, type));
        }

        return list;
    }
}
