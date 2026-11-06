using System.Globalization;
using System.Text.Json;
using Luna.Core.Storage;
using Luna.Protocol.Messaging;
using Luna.Protocol.Realtime;

namespace Luna.Core.Dev;

/// <summary>Why a path was accepted or refused as the target of a synthetic history database.</summary>
public enum SeedTargetCheck
{
    /// <summary>The path may be used.</summary>
    Ok,

    /// <summary>Empty, a directory, or contains characters a file name cannot have.</summary>
    Invalid,

    /// <summary>The path is the real history database.</summary>
    IsDefaultPath,

    /// <summary>The path is anywhere inside the folder that holds the real history database.</summary>
    InsideDataFolder,

    /// <summary>The file already exists and the caller did not ask for --force.</summary>
    ExistsWithoutForce,
}

/// <summary>
/// FAKE chat history for testing the chat pane offline: real <see cref="MessageParser"/> output built from synthetic socket
/// events with fake ids and ASCII text. Nothing here touches the network or a real account.
/// </summary>
public static class SyntheticHistory
{
    /// <summary>Every fake thread id starts with this prefix.</summary>
    public const string ThreadPrefix = "7000";

    /// <summary>Number of conversations <see cref="Build"/> spreads the messages over.</summary>
    public const int ConversationCount = 3;

    /// <summary>The first private conversation.</summary>
    public const string PrivateThreadA = "7000001";

    /// <summary>The group conversation.</summary>
    public const string GroupThread = "7000002";

    /// <summary>The second private conversation.</summary>
    public const string PrivateThreadB = "7000003";

    // One recall notice follows every 200th regular message.
    private const int RecallEvery = 200;

    // Ids: global ids start at 7000000000000, client ids and notice ids live in their own fake ranges.
    private const long MsgIdBase = 7_000_000_000_000;
    private const long CliMsgIdBase = 7_000_500_000_000;
    private const long NoticeIdBase = 7_000_900_000_000;

    private const string FakeSelfUid = "7000999";
    private const string SelfDisplayName = "Fake Self";
    private const int VeryLongTextLength = 3000;

    private static readonly string[] GroupSenderUids = ["7000101", "7000102", "7000103"];
    private static readonly string[] GroupSenderNames = ["Fake Member One", "Fake Member Two", "Fake Member Three"];

    private static readonly ContactName[] Contacts =
    [
        new ContactName(PrivateThreadA, "Fake Contact One"),
        new ContactName(PrivateThreadB, "Fake Contact Two"),
    ];

    /// <summary>Fake names for the two private conversations.</summary>
    public static IReadOnlyList<ContactName> ContactNames => Contacts;

    /// <summary>
    /// Builds the fake history in saving order: <paramref name="messageCount"/> regular messages (message i goes to
    /// conversation i % 3, one minute apart, the last one at <paramref name="now"/>), and after every 200th one a real
    /// <c>chat.undo</c> notice that recalls an earlier message of the same conversation. Deterministic.
    /// </summary>
    public static IReadOnlyList<ChatMessage> Build(int messageCount, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(messageCount);

        long nowMs = now.ToUnixTimeMilliseconds();
        int veryLongIndex = messageCount / 2;
        var result = new List<ChatMessage>(messageCount + (messageCount / RecallEvery));
        var regular = new List<ChatMessage>(messageCount);

        for (int i = 0; i < messageCount; i++)
        {
            long timestamp = nowMs - ((long)(messageCount - 1 - i) * 60_000L);
            ChatMessage message = BuildRegular(i, timestamp, i == veryLongIndex);
            regular.Add(message);
            result.Add(message);

            if ((i + 1) % RecallEvery == 0)
            {
                // Three steps back stays in the same conversation; the step grows with the notice number, so no
                // message is recalled twice.
                int targetIndex = i - (3 * (1 + ((i / RecallEvery) % 4)));
                result.Add(BuildNotice(((i + 1) / RecallEvery) - 1, regular[targetIndex], timestamp));
            }
        }

        return result;
    }

    /// <summary>
    /// Decides whether <paramref name="path"/> may receive a synthetic database. Both paths are normalized with
    /// <see cref="Path.GetFullPath(string)"/> and compared ignoring case. Anything inside the folder of
    /// <paramref name="defaultPath"/> is refused whatever <paramref name="force"/> says. <paramref name="fileExists"/> is
    /// called with the normalized path, and only for a path that is otherwise acceptable.
    /// </summary>
    public static SeedTargetCheck CheckTarget(string? path, string defaultPath, bool force, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(defaultPath);
        ArgumentNullException.ThrowIfNull(fileExists);

        if (string.IsNullOrWhiteSpace(path)
            || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0
            || path[^1] is '\\' or '/')
        {
            return SeedTargetCheck.Invalid;
        }

        string full;
        string defaultFull;
        try
        {
            // Windows drops trailing spaces and dots in GetFullPath; trimming again keeps the check independent of that.
            full = Path.GetFullPath(path).TrimEnd(' ', '.');
            defaultFull = Path.GetFullPath(defaultPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return SeedTargetCheck.Invalid;
        }

        string fileName = Path.GetFileName(full);
        if (fileName.Length == 0 || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Directory.Exists(full))
        {
            return SeedTargetCheck.Invalid;
        }

        if (string.Equals(full, defaultFull, StringComparison.OrdinalIgnoreCase))
        {
            return SeedTargetCheck.IsDefaultPath;
        }

        string dataFolder = Path.GetDirectoryName(defaultFull)?.TrimEnd('\\', '/') ?? "";
        if (dataFolder.Length > 0
            && (full.StartsWith(dataFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(dataFolder + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
        {
            return SeedTargetCheck.InsideDataFolder;
        }

        return !force && fileExists(full) ? SeedTargetCheck.ExistsWithoutForce : SeedTargetCheck.Ok;
    }

    /// <summary>True when every thread id is synthetic (an empty list counts as synthetic).</summary>
    public static bool AreAllThreadsSynthetic(IEnumerable<string> threadIds)
    {
        ArgumentNullException.ThrowIfNull(threadIds);
        return threadIds.All(id => id.StartsWith(ThreadPrefix, StringComparison.Ordinal));
    }

    private static ChatMessage BuildRegular(int index, long timestamp, bool veryLong)
    {
        int slot = index % ConversationCount;
        bool isGroup = slot == 1;
        string threadId = slot switch
        {
            0 => PrivateThreadA,
            1 => GroupThread,
            _ => PrivateThreadB,
        };
        bool isSelf = index % 4 == 3;
        int senderIndex = (index / ConversationCount) % GroupSenderUids.Length;

        string displayName = isSelf ? SelfDisplayName
            : isGroup ? GroupSenderNames[senderIndex]
            : Contacts[slot == 0 ? 0 : 1].Name;

        var item = new Dictionary<string, object?>
        {
            ["msgId"] = (MsgIdBase + index).ToString(CultureInfo.InvariantCulture),
            ["cliMsgId"] = (CliMsgIdBase + index).ToString(CultureInfo.InvariantCulture),
            ["msgType"] = "webchat",
            ["dName"] = displayName,
            ["ts"] = timestamp.ToString(CultureInfo.InvariantCulture),
            ["content"] = veryLong ? VeryLongText(index) : TextFor(index),
        };
        AddAddresses(item, isGroup, threadId, isSelf, isGroup ? GroupSenderUids[senderIndex] : threadId);
        return Parse(isGroup, item);
    }

    private static ChatMessage BuildNotice(int noticeIndex, ChatMessage target, long timestamp)
    {
        bool isGroup = target.ThreadType == ThreadType.Group;
        string noticeId = (NoticeIdBase + noticeIndex).ToString(CultureInfo.InvariantCulture);
        var item = new Dictionary<string, object?>
        {
            ["msgId"] = noticeId,
            ["cliMsgId"] = noticeId + "1",
            ["msgType"] = "chat.undo",
            ["dName"] = target.DisplayName,
            ["ts"] = timestamp.ToString(CultureInfo.InvariantCulture),
            ["content"] = new Dictionary<string, object?>
            {
                ["deleteMsg"] = 1,
                ["globalMsgId"] = target.MsgId,
                ["cliMsgId"] = target.CliMsgId,
            },
        };
        AddAddresses(item, isGroup, target.ThreadId, target.IsSelf, isGroup ? target.UidFrom : target.ThreadId);
        return Parse(isGroup, item);
    }

    // Private incoming: from the other party to the fake self uid. Self (uidFrom "0") goes to the thread id.
    private static void AddAddresses(Dictionary<string, object?> item, bool isGroup, string threadId, bool isSelf, string senderUid)
    {
        if (isGroup)
        {
            item["uidFrom"] = isSelf ? "0" : senderUid;
            item["idTo"] = threadId;
        }
        else if (isSelf)
        {
            item["uidFrom"] = "0";
            item["idTo"] = threadId;
        }
        else
        {
            item["uidFrom"] = threadId;
            item["idTo"] = FakeSelfUid;
        }
    }

    private static ChatMessage Parse(bool isGroup, Dictionary<string, object?> item)
    {
        string arrayName = isGroup ? "groupMsgs" : "msgs";
        var payload = new Dictionary<string, object?>
        {
            ["data"] = new Dictionary<string, object?> { [arrayName] = new[] { item } },
        };
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        ushort cmd = isGroup ? MessageParser.CmdGroupMessage : MessageParser.CmdUserMessage;
        var socketEvent = new SocketEvent(cmd, 0, document.RootElement.Clone());
        ParseResult parsed = MessageParser.ParseEvent(socketEvent);
        if (parsed.Messages.Count != 1)
        {
            // Cannot happen with the fixed shape above; failing loudly beats saving a short history.
            throw new InvalidOperationException("The synthetic socket event was not parsed into exactly one message.");
        }

        return parsed.Messages[0];
    }

    // Short, medium, multi-line and long-ish texts in a fixed rotation. ASCII only.
    private static string TextFor(int index)
    {
        string number = index.ToString(CultureInfo.InvariantCulture);
        return (index % 10) switch
        {
            5 => Repeat("This is a fake message of medium length. ", 3) + number,
            6 => "Fake line one of message " + number + "\nFake line two\nFake line three",
            7 => Repeat("A longer fake paragraph used to test wrapping in the chat pane. ", 7) + number,
            8 => "ok " + number,
            9 => "Fake message with some more words in it, number " + number,
            _ => "fake message " + number,
        };
    }

    private static string VeryLongText(int index)
    {
        string unit = "Very long fake text for scroll and wrapping tests number " + index.ToString(CultureInfo.InvariantCulture) + ". ";
        return Repeat(unit, (VeryLongTextLength / unit.Length) + 1)[..VeryLongTextLength];
    }

    private static string Repeat(string text, int times) => string.Concat(Enumerable.Repeat(text, times));
}
