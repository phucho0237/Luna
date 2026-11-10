using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Luna.Protocol.Messaging;
using Luna.Protocol.Realtime;

namespace Luna.Protocol.Diagnostics;

/// <summary>
/// What one old-message response page tells us, as numbers and safe tokens only (T5b probe). The payload is wrapped in a
/// synthetic message event (cmd 501 private, 521 group) and read by the real <see cref="MessageParser"/>, so the numbers also
/// show whether the parser fits history responses; the self rule is the parser's, not guessed here.
/// Never exposes message text, names, ids, uids or exact times: thread ids, message ids and timestamps are used in memory
/// only. The id for the next request is reachable ONLY through <see cref="TryGetNextLastId"/> and is not part of any printed form.
/// </summary>
public sealed class HistoryProbeSummary
{
    private const string OtherKinds = "other";

    // The only kinds ever printed: the msgType constants of MessageTypes, dots turned into underscores. Built from the
    // constants themselves so that a new constant is picked up without touching this file.
    private static readonly HashSet<string> AllowedKinds = typeof(MessageTypes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.IsLiteral && field.FieldType == typeof(string))
        .Select(field => ((string)field.GetRawConstantValue()!).Replace('.', '_'))
        .ToHashSet(StringComparer.Ordinal);

    private const long MillisecondsThreshold = 100_000_000_000L;
    private const long DayMilliseconds = 86_400_000L;

    private string? _nextLastId;
    private readonly HashSet<string> _messageIds = new(StringComparer.Ordinal);
    private long? _oldestMilliseconds;
    private long? _newestMilliseconds;

    private HistoryProbeSummary()
    {
    }

    /// <summary>Messages the parser accepted.</summary>
    public int Parsed { get; private set; }

    /// <summary>Elements of the array the parser rejected.</summary>
    public int Skipped { get; private set; }

    /// <summary>Accepted messages the parser marked as sent by the own account.</summary>
    public int SelfCount { get; private set; }

    /// <summary>Accepted messages from someone else.</summary>
    public int OthersCount { get; private set; }

    /// <summary>Number of distinct threads (private: the other party; group: the group). The ids themselves are never kept.</summary>
    public int ThreadCount { get; private set; }

    /// <summary>Whole days before now of the oldest message with a usable timestamp, or null.</summary>
    public int? OldestDaysAgo { get; private set; }

    /// <summary>Whole days before now of the newest message with a usable timestamp (future counts as 0), or null.</summary>
    public int? NewestDaysAgo { get; private set; }

    /// <summary>
    /// Messages per <c>msgType</c>. A kind is named only when it equals one of the <see cref="MessageTypes"/> constants
    /// (dots turned into underscores); every other value, including an empty one, is counted under <c>other</c>, so no
    /// free text from the server reaches the output.
    /// </summary>
    public IReadOnlyDictionary<string, int> KindCounts { get; private set; } = new Dictionary<string, int>();

    /// <summary>
    /// Names (through <see cref="JsonShape.SafeNameStrict"/>) of numeric or boolean top-level fields, sorted. An object
    /// with more than 30 keys contributes no names.
    /// </summary>
    public IReadOnlyList<string> ExtraFieldNames { get; private set; } = [];

    /// <summary>Names and types of the payload to depth 2 (strict <see cref="JsonShape.Describe(JsonElement, int, bool)"/>). Multi-line; not part of <see cref="Format"/>.</summary>
    public string Shape { get; private set; } = string.Empty;

    /// <summary>The numeric <c>error_code</c> of the payload (or of its data), null if there is none. A present but unreadable one is <see cref="int.MaxValue"/>.</summary>
    public int? ErrorCode { get; private set; }

    /// <summary>True when there is an error code other than 0.</summary>
    public bool HasError => ErrorCode is { } code && code != 0;

    /// <summary>For pages after the first: how many of this page's message ids were also in the previous page. Null for the first page.</summary>
    public int? OverlapWithPrevious { get; private set; }

    /// <summary>
    /// For pages after the first: true when the newest timestamp of this page is not newer than the oldest of the previous page.
    /// Null for the first page or when either page has no usable timestamp.
    /// </summary>
    public bool? ContinuesOlder { get; private set; }

    /// <param name="payload">The decoded response payload (the <c>{error_code, data:{msgs|groupMsgs}}</c> object).</param>
    /// <param name="selfUid">The own account id, handed to the parser.</param>
    /// <param name="previous">The summary of the previous page, to measure overlap and direction.</param>
    public static HistoryProbeSummary From(
        JsonElement payload, HistoryScope scope, string? selfUid, DateTimeOffset now, HistoryProbeSummary? previous = null)
    {
        if (!Enum.IsDefined(scope))
        {
            throw new ArgumentOutOfRangeException(nameof(scope), "Unknown history scope.");
        }

        ushort cmd = scope == HistoryScope.Private ? MessageParser.CmdUserMessage : MessageParser.CmdGroupMessage;
        ParseResult parsed = MessageParser.ParseEvent(new SocketEvent(cmd, 1, payload), selfUid);

        var summary = new HistoryProbeSummary
        {
            Parsed = parsed.Messages.Count,
            Skipped = parsed.Skipped,
            Shape = JsonShape.Describe(payload, 2, strict: true),
            ErrorCode = ReadErrorCode(payload),
            ExtraFieldNames = ReadExtraFieldNames(payload),
        };

        long nowMilliseconds = now.ToUnixTimeMilliseconds();
        var threads = new HashSet<string>(StringComparer.Ordinal);
        var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
        string? oldestId = null;

        foreach (ChatMessage message in parsed.Messages)
        {
            if (message.IsSelf)
            {
                summary.SelfCount++;
            }
            else
            {
                summary.OthersCount++;
            }

            if (message.ThreadId.Length > 0)
            {
                threads.Add(message.ThreadId);
            }

            if (message.MsgId.Length > 0)
            {
                summary._messageIds.Add(message.MsgId);
            }

            string candidate = message.MsgType.Replace('.', '_');
            string kind = AllowedKinds.Contains(candidate) ? candidate : OtherKinds;
            kinds[kind] = kinds.GetValueOrDefault(kind) + 1;

            if (!TryNormalizeMilliseconds(message.Timestamp, out long milliseconds))
            {
                continue;
            }

            summary._newestMilliseconds = summary._newestMilliseconds is { } newest ? Math.Max(newest, milliseconds) : milliseconds;
            if (summary._oldestMilliseconds is not { } oldest
                || milliseconds < oldest
                || (milliseconds == oldest && IsSmallerNumber(message.MsgId, oldestId)))
            {
                summary._oldestMilliseconds = milliseconds;
                oldestId = message.MsgId;
            }
        }

        summary.ThreadCount = threads.Count;
        summary._nextLastId = oldestId;
        summary.KindCounts = kinds;
        summary.OldestDaysAgo = summary._oldestMilliseconds is { } o ? DaysBefore(o, nowMilliseconds) : null;
        summary.NewestDaysAgo = summary._newestMilliseconds is { } n ? DaysBefore(n, nowMilliseconds) : null;

        if (previous is not null)
        {
            summary.OverlapWithPrevious = summary._messageIds.Count(previous._messageIds.Contains);
            summary.ContinuesOlder = summary._newestMilliseconds is { } thisNewest
                && previous._oldestMilliseconds is { } previousOldest
                    ? thisNewest <= previousOldest
                    : null;
        }

        return summary;
    }

    /// <summary>
    /// The id for the next request: the message id of the oldest message (smallest timestamp) of this page, if it is 1 to 20
    /// ASCII digits. False (and an empty id) when the page has no usable timestamp or the id is not usable.
    /// </summary>
    public bool TryGetNextLastId(out string id)
    {
        if (_nextLastId is { Length: > 0 and <= 20 } candidate && candidate.All(c => c is >= '0' and <= '9'))
        {
            id = candidate;
            return true;
        }

        id = string.Empty;
        return false;
    }

    /// <summary>One line of key=value pairs: counts, day ranges, kinds and field names. No ids, no text, no exact times.</summary>
    public string Format()
    {
        string kinds = KindCounts.Count == 0
            ? "none"
            : string.Join(
                ',',
                KindCounts.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Key}:{p.Value}")));
        string extra = ExtraFieldNames.Count == 0 ? "none" : string.Join(',', ExtraFieldNames);
        string error = ErrorCode is { } code && code != 0 ? code.ToString(CultureInfo.InvariantCulture) : "none";
        string direction = ContinuesOlder is { } older ? (older ? "yes" : "no") : "na";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"parsed={Parsed} skipped={Skipped} self={SelfCount} others={OthersCount} threads={ThreadCount} "
            + $"oldest_days={Days(OldestDaysAgo)} newest_days={Days(NewestDaysAgo)} kinds={kinds} extra={extra} "
            + $"error={error} overlap={Days(OverlapWithPrevious)} continues_older={direction}");
    }

    /// <summary>Same as <see cref="Format"/>.</summary>
    public override string ToString() => Format();

    private static string Days(int? value) => value is { } v ? v.ToString(CultureInfo.InvariantCulture) : "na";

    /// <summary>A timestamp under 1e11 is in seconds, otherwise in milliseconds. Zero, negative or unreadable ones are unusable.</summary>
    private static bool TryNormalizeMilliseconds(string text, out long milliseconds)
    {
        milliseconds = 0;
        if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value) || value <= 0)
        {
            return false;
        }

        milliseconds = value < MillisecondsThreshold ? value * 1000 : value;
        return true;
    }

    private static int DaysBefore(long milliseconds, long nowMilliseconds)
    {
        long difference = nowMilliseconds - milliseconds;
        return difference <= 0 ? 0 : (int)Math.Min(int.MaxValue, difference / DayMilliseconds);
    }

    private static bool IsSmallerNumber(string candidate, string? current)
    {
        if (candidate.Length == 0 || !candidate.All(char.IsAsciiDigit))
        {
            return false;
        }

        if (current is null || current.Length == 0 || !current.All(char.IsAsciiDigit))
        {
            return true;
        }

        int byLength = candidate.Length.CompareTo(current.Length);
        return byLength != 0 ? byLength < 0 : string.CompareOrdinal(candidate, current) < 0;
    }

    private static int? ReadErrorCode(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        int? top = ReadCode(payload);
        int? nested = payload.TryGetProperty("data", out JsonElement data) ? ReadCode(data) : null;
        if (top is { } t && t != 0)
        {
            return t;
        }

        return nested is { } d && d != 0 ? d : top ?? nested;
    }

    private static int? ReadCode(JsonElement container)
    {
        if (container.ValueKind != JsonValueKind.Object || !container.TryGetProperty("error_code", out JsonElement code))
        {
            return null;
        }

        switch (code.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            case JsonValueKind.Number:
                return code.TryGetInt32(out int number) ? number : int.MaxValue;
            case JsonValueKind.String:
                return int.TryParse(code.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                    ? parsed
                    : int.MaxValue;
            default:
                return int.MaxValue;
        }
    }

    private static string[] ReadExtraFieldNames(JsonElement payload)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (payload.ValueKind == JsonValueKind.Object)
        {
            AddScalarNames(payload, names);
            if (payload.TryGetProperty("data", out JsonElement data))
            {
                AddScalarNames(data, names);
            }
        }

        return [.. names.Order(StringComparer.Ordinal)];
    }

    private static void AddScalarNames(JsonElement container, HashSet<string> names)
    {
        // An object with that many keys is most likely a map keyed by data: none of its names are reported.
        if (container.ValueKind != JsonValueKind.Object || JsonShape.HasTooManyKeysForStrict(container))
        {
            return;
        }

        foreach (JsonProperty property in container.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            {
                names.Add(JsonShape.SafeNameStrict(property.Name));
            }
        }
    }
}
