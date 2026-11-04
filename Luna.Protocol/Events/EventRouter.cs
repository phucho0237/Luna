using System.Text.Json;
using Luna.Protocol.Messaging;
using Luna.Protocol.Realtime;

namespace Luna.Protocol.Events;

/// <summary>Result of routing one socket event: the typed events and the number of elements skipped because of errors.</summary>
public sealed record RouteResult(IReadOnlyList<ChatEvent> Events, int Skipped)
{
    public static readonly RouteResult Empty = new([], 0);

    /// <summary>Prints counts only, never the events.</summary>
    public override string ToString() => $"RouteResult {{ Count = {Events?.Count ?? 0}, Skipped = {Skipped} }}";
}

/// <summary>
/// Converts a raw <see cref="SocketEvent"/> into typed <see cref="ChatEvent"/> values.
/// Never throws because of unusual data: anything not understood goes into <see cref="UnknownEvent"/>.
/// </summary>
public sealed class EventRouter
{
    // Common properties of data, not counted as "unhandled" (a real cmd 501 always has a non-empty queueStatus).
    private static readonly HashSet<string> IgnoredKeys =
        new(["lastActionId", "more", "queueStatus", "eesession"], StringComparer.Ordinal);

    private const ushort CmdUserDelivered = 502;
    private const ushort CmdGroupDelivered = 522;
    private const ushort CmdUserUnreadCleared = 504;
    private const ushort CmdGroupUnreadCleared = 524; // Not seen on the live server.
    private const ushort CmdUnreadClearedV2 = 613; // Unclear how it differs from 504.
    private const ushort CmdReaction = 612;
    private const ushort CmdActions = 602;
    private const ushort CmdControls = 601;

    private readonly MessageParser _messageParser;
    private readonly Action<string>? _trace;

    /// <summary>
    /// Creates the router with the account's own uid (<paramref name="selfUid"/>, to determine <c>IsSelf</c>)
    /// and a trace function (<paramref name="trace"/>, accepts only messages that contain no sensitive data).
    /// </summary>
    public EventRouter(string? selfUid = null, Action<string>? trace = null)
    {
        _messageParser = new MessageParser(selfUid, trace);
        _trace = trace;
    }

    /// <summary>
    /// Routes one socket event. Skips cmd 1 and 2. One event yields the recognized events,
    /// plus at most one <see cref="UnknownEvent"/> if <c>data</c> still has a non-empty array/object that was not handled.
    /// An unsupported cmd, or a payload without an object-shaped <c>data</c>, yields exactly one <see cref="UnknownEvent"/>.
    /// </summary>
    public RouteResult Route(SocketEvent socketEvent)
    {
        ArgumentNullException.ThrowIfNull(socketEvent);

        if (socketEvent.Cmd is 1 or 2)
        {
            return RouteResult.Empty;
        }

        var events = new List<ChatEvent>();
        int skipped = 0;

        JsonElement payload = socketEvent.Payload;
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("data", out JsonElement data)
            || data.ValueKind != JsonValueKind.Object)
        {
            events.Add(Unknown(socketEvent));
            return new RouteResult(events, 0);
        }

        string[]? handled = Dispatch(socketEvent, data, events, ref skipped);
        if (handled is null || HasUnhandledContent(data, handled))
        {
            events.Add(Unknown(socketEvent));
        }

        return new RouteResult(events, skipped);
    }

    private static UnknownEvent Unknown(SocketEvent e) => new(e.Cmd, e.SubCmd, e.Payload);

    /// <summary>Handles a supported cmd and returns the names of the arrays handled; returns <c>null</c> if the cmd is unsupported.</summary>
    private string[]? Dispatch(SocketEvent socketEvent, JsonElement data, List<ChatEvent> events, ref int skipped)
    {
        return socketEvent.Cmd switch
        {
            MessageParser.CmdUserMessage => RouteMessages(socketEvent, events, ref skipped, "msgs"),
            MessageParser.CmdGroupMessage => RouteMessages(socketEvent, events, ref skipped, "groupMsgs"),
            CmdUserDelivered or CmdGroupDelivered => RouteReceipts(socketEvent, data, events, ref skipped),
            CmdUserUnreadCleared or CmdGroupUnreadCleared or CmdUnreadClearedV2 => RouteItems(
                socketEvent, data, "clearUnreads", events, ref skipped, item => ParseUnreadCleared(item, socketEvent.Cmd)),
            CmdReaction => RouteReactions(socketEvent, data, events, ref skipped),
            CmdControls => RouteItems(socketEvent, data, "controls", events, ref skipped, ParseControl),
            CmdActions => RouteActions(socketEvent, data, events, ref skipped),
            _ => null,
        };
    }

    /// <summary>
    /// Cmd 602: accepts only the typing/gtyping actions. With any other action (or <c>actions</c> not being an array)
    /// it returns <c>null</c> so the whole event goes into <see cref="UnknownEvent"/>.
    /// </summary>
    private string[]? RouteActions(SocketEvent socketEvent, JsonElement data, List<ChatEvent> events, ref int skipped)
    {
        if (!data.TryGetProperty("actions", out JsonElement array) || array.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        bool hasOther = false;
        foreach (JsonElement item in array.EnumerateArray())
        {
            string? actType = item.ValueKind == JsonValueKind.Object ? JsonFields.ReadScalar(item, "act_type") : null;
            string? act = item.ValueKind == JsonValueKind.Object ? JsonFields.ReadScalar(item, "act") : null;
            if (actType != "typing" || act is not ("typing" or "gtyping"))
            {
                hasOther = true;
                continue;
            }

            try
            {
                events.Add(ParseTyping(item, act == "gtyping"));
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or JsonException)
            {
                skipped++;
                _trace?.Invoke($"Skipped a typing action that could not be parsed: {ex.GetType().Name}");
            }
        }

        return hasOther ? null : ["actions"];
    }

    /// <summary><c>data</c> is a JSON string missing its outer pair of braces (zca-js: <c>JSON.parse("{" + data + "}")</c>).</summary>
    private static TypingNotice ParseTyping(JsonElement item, bool isGroup)
    {
        string raw = JsonFields.GetString(item, "data") ?? throw new FormatException("Missing field data.");
        string text = raw.TrimStart().StartsWith('{') ? raw : "{" + raw + "}";
        using JsonDocument doc = JsonDocument.Parse(text);
        JsonElement root = doc.RootElement;
        RequireObject(root);
        return new TypingNotice(
            isGroup,
            Require(root, "uid"),
            isGroup ? JsonFields.ReadScalar(root, "gid") : null,
            JsonFields.ReadScalar(root, "ts"));
    }

    private string[]? RouteReactions(SocketEvent socketEvent, JsonElement data, List<ChatEvent> events, ref int skipped)
    {
        string[]? users = RouteItems(socketEvent, data, "reacts", events, ref skipped, item => ParseReaction(item, ThreadType.User));
        string[]? groups = RouteItems(socketEvent, data, "reactGroups", events, ref skipped, item => ParseReaction(item, ThreadType.Group));
        return users is null || groups is null ? null : ["reacts", "reactGroups"];
    }

    /// <summary>
    /// Emits a typed event when <c>data</c> can be parsed; if the data is unknown or missing fields, emits
    /// <see cref="ControlEvent"/> as before (not counted in Skipped).
    /// </summary>
    private static ChatEvent ParseControl(JsonElement item)
    {
        RequireObject(item);
        if (!item.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("Missing field content.");
        }

        string actType = Require(content, "act_type");
        string act = JsonFields.ReadScalar(content, "act") ?? "";
        var control = new ControlEvent(actType, act, content, ControlEvent.KindOf(actType));
        return TryParseTyped(content, actType, act) ?? control;
    }

    /// <summary>Tries to parse a typed event; any data error yields <c>null</c> (falls back to <see cref="ControlEvent"/>).</summary>
    private static ChatEvent? TryParseTyped(JsonElement content, string actType, string act)
    {
        try
        {
            return actType switch
            {
                "fr" => ParseFriend(content, act),
                "mute" => ParseMute(content, act),
                "pinned_convers" => ParsePinned(content, act),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads <c>content.data</c> as an object, or as a string containing JSON (parsed a second time, copied out of the
    /// <see cref="JsonDocument"/>). A plain string that is not an object or array, or any other type, yields <c>null</c>.
    /// Never lets <see cref="JsonException"/>/<see cref="ArgumentException"/> escape when the string is corrupt
    /// (lone surrogate, nested too deeply).
    /// </summary>
    private static JsonElement? ReadControlData(JsonElement content)
    {
        if (!content.TryGetProperty("data", out JsonElement data))
        {
            return null;
        }

        if (data.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            return data;
        }

        if (data.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        try
        {
            string? text = data.GetString()?.Trim();
            if (string.IsNullOrEmpty(text) || text[0] is not ('{' or '['))
            {
                return null;
            }

            using JsonDocument doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary><c>pinned_convers/update</c>: data object <c>{ tab, action, pinIds[] }</c>; missing fields fall back to <see cref="ControlEvent"/>.</summary>
    private static PinnedConversationsChanged? ParsePinned(JsonElement content, string act)
    {
        if (act != "update"
            || ReadControlData(content) is not { ValueKind: JsonValueKind.Object } data
            || JsonFields.ReadScalar(data, "tab") is null
            || JsonFields.ReadScalar(data, "action") is null
            || !data.TryGetProperty("pinIds", out JsonElement ids)
            || ids.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var pinIds = new List<string>();
        foreach (JsonElement id in ids.EnumerateArray())
        {
            if (JsonFields.ReadScalar(id) is not { } value)
            {
                return null;
            }

            pinIds.Add(value);
        }

        return new PinnedConversationsChanged(
            JsonFields.ReadInt32(data, "tab"), JsonFields.ReadInt32(data, "action"), pinIds);
    }

    /// <summary><c>mute/set</c> and <c>mute/unset</c>: data is a JSON string with <c>toId</c> (<c>advanced</c> is null on unset).</summary>
    private static MuteChanged? ParseMute(JsonElement content, string act)
    {
        if (act is not ("set" or "unset")
            || ReadControlData(content) is not { ValueKind: JsonValueKind.Object } data
            || JsonFields.ReadScalar(data, "toId") is not { Length: > 0 } threadId)
        {
            return null;
        }

        return new MuteChanged(
            threadId,
            act == "set",
            JsonFields.ReadScalar(data, "duration"),
            JsonFields.ReadScalar(data, "startTime"));
    }

    /// <summary><c>fr</c>: <c>req_v2</c>, <c>undo_req</c> (data object), <c>req</c> (JSON string), <c>add</c>/<c>remove</c> (plain string).</summary>
    private static FriendEvent? ParseFriend(JsonElement content, string act)
    {
        if (act is "add" or "remove")
        {
            // Data is a plain string (content unclear), not parsed; it only needs to be a non-empty string.
            bool hasText = content.TryGetProperty("data", out JsonElement raw)
                && raw.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(raw.GetString());
            return hasText
                ? new FriendEvent(act == "add" ? FriendEventKind.Added : FriendEventKind.Removed, null, null, null, null)
                : null;
        }

        if (ReadControlData(content) is not { ValueKind: JsonValueKind.Object } data)
        {
            return null;
        }

        switch (act)
        {
            case "req_v2" or "undo_req":
                string? toUid = JsonFields.ReadScalar(data, "toUid");
                string? fromUid = JsonFields.ReadScalar(data, "fromUid");
                if (toUid is null || fromUid is null)
                {
                    return null;
                }

                return new FriendEvent(
                    act == "req_v2" ? FriendEventKind.RequestSent : FriendEventKind.RequestUndone,
                    null,
                    toUid,
                    fromUid,
                    JsonFields.ReadScalar(data, "message"));
            case "req":
                // uid is a JSON number: ReadScalar uses GetRawText so no precision is lost.
                string? uid = JsonFields.ReadScalar(data, "uid");
                return uid is null
                    ? null
                    : new FriendEvent(FriendEventKind.RequestReceived, uid, null, null, JsonFields.ReadScalar(data, "message"));
            default:
                return null;
        }
    }

    private static ReactionReceived ParseReaction(JsonElement item, ThreadType threadType)
    {
        RequireObject(item);
        return new ReactionReceived(
            threadType,
            Require(item, "msgId"),
            Require(item, "cliMsgId"),
            Require(item, "uidFrom"),
            Require(item, "idTo"),
            JsonFields.ReadScalarOrJson(item, "content") ?? throw new FormatException("Missing field content."),
            Require(item, "ts"));
    }

    /// <summary>
    /// Iterates the array <paramref name="arrayName"/>; a bad element (not an object, missing field, wrong type) is skipped and counted in Skipped.
    /// </summary>
    private string[]? RouteItems(
        SocketEvent socketEvent,
        JsonElement data,
        string arrayName,
        List<ChatEvent> events,
        ref int skipped,
        Func<JsonElement, ChatEvent> factory)
    {
        if (data.TryGetProperty(arrayName, out JsonElement array)
            && array.ValueKind is not (JsonValueKind.Array or JsonValueKind.Null))
        {
            // The key exists but is not an array (object, string...): not understood, left to UnknownEvent.
            return null;
        }

        if (array.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in array.EnumerateArray())
            {
                try
                {
                    events.Add(factory(item));
                }
                catch (Exception ex) when (ex is InvalidOperationException or FormatException or JsonException)
                {
                    skipped++;
                    _trace?.Invoke($"Skipped an element of cmd {socketEvent.Cmd} that could not be parsed: {ex.GetType().Name}");
                }
            }
        }

        return [arrayName];
    }

    /// <summary>Cmd 502/522: <c>delivereds</c> together with the seen array (<c>seens</c> or <c>groupSeens</c> depending on the cmd).</summary>
    private string[]? RouteReceipts(SocketEvent socketEvent, JsonElement data, List<ChatEvent> events, ref int skipped)
    {
        bool isGroup = socketEvent.Cmd == CmdGroupDelivered;
        string seenName = isGroup ? "groupSeens" : "seens";
        string[]? delivered = RouteItems(
            socketEvent, data, "delivereds", events, ref skipped, item => ParseDelivery(item, socketEvent.Cmd));
        string[]? seen = RouteItems(
            socketEvent, data, seenName, events, ref skipped, item => ParseSeen(item, isGroup));
        return delivered is null || seen is null ? null : ["delivereds", seenName];
    }

    private static SeenReceipt ParseSeen(JsonElement item, bool isGroup)
    {
        RequireObject(item);
        return new SeenReceipt(
            isGroup,
            Require(item, "msgId"),
            JsonFields.ReadScalar(item, "realMsgId"),
            JsonFields.ReadScalar(item, isGroup ? "groupId" : "idTo"),
            ReadStringList(item, "seenUids"));
    }

    private static DeliveryReceipt ParseDelivery(JsonElement item, ushort cmd)
    {
        RequireObject(item);
        return new DeliveryReceipt(
            cmd == CmdGroupDelivered,
            Require(item, "msgId"),
            JsonFields.ReadScalar(item, "realMsgId"),
            cmd == CmdGroupDelivered ? JsonFields.ReadScalar(item, "groupId") : null,
            JsonFields.ReadInt32(item, "seen"),
            ReadStringList(item, "deliveredUids"),
            ReadStringList(item, "seenUids"));
    }

    private static UnreadCleared ParseUnreadCleared(JsonElement item, ushort cmd)
    {
        RequireObject(item);
        string? isGroup = JsonFields.ReadScalar(item, "isGroup");
        return new UnreadCleared(
            Require(item, "idTo"),
            Require(item, "lastMsgId"),
            JsonFields.ReadScalar(item, "lastCliMsgId"),
            isGroup is null ? cmd == CmdGroupUnreadCleared : isGroup is not ("0" or "false"),
            Require(item, "ts"));
    }

    private static void RequireObject(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("The element is not an object.");
        }
    }

    private static string Require(JsonElement item, string name) =>
        JsonFields.ReadScalar(item, name) ?? throw new FormatException($"Missing field {name}.");

    private static List<string> ReadStringList(JsonElement item, string name)
    {
        var list = new List<string>();
        if (item.TryGetProperty(name, out JsonElement array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement element in array.EnumerateArray())
            {
                if (JsonFields.ReadScalar(element) is { } value)
                {
                    list.Add(value);
                }
            }
        }

        return list;
    }

    /// <summary>A message with <c>IsUndo</c> raises <see cref="MessageUndone"/> instead of <see cref="MessageReceived"/>.</summary>
    private string[] RouteMessages(SocketEvent socketEvent, List<ChatEvent> events, ref int skipped, string arrayName)
    {
        ParseResult parsed = _messageParser.Parse(socketEvent);
        skipped += parsed.Skipped;
        foreach (ChatMessage message in parsed.Messages)
        {
            if (message.Content is UndoContent undo)
            {
                events.Add(new MessageUndone(
                    message.ThreadType, message.ThreadId, undo.GlobalMsgId ?? "", undo.CliMsgId ?? ""));
            }
            else
            {
                events.Add(new MessageReceived(message));
            }
        }

        return [arrayName];
    }

    private static bool HasUnhandledContent(JsonElement data, string[] handled)
    {
        foreach (JsonProperty property in data.EnumerateObject())
        {
            if (IgnoredKeys.Contains(property.Name) || Array.IndexOf(handled, property.Name) >= 0)
            {
                continue;
            }

            bool nonEmpty = property.Value.ValueKind switch
            {
                JsonValueKind.Array => property.Value.GetArrayLength() > 0,
                JsonValueKind.Object => property.Value.EnumerateObject().Any(),
                _ => false,
            };
            if (nonEmpty)
            {
                return true;
            }
        }

        return false;
    }
}
