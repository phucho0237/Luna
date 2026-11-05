using System.Text.Json;
using Luna.Protocol.Messaging;
using Luna.Protocol.Realtime;

namespace Luna.Core.Tests.Support;

/// <summary>Builds <see cref="ChatMessage"/> objects by running the real <see cref="MessageParser"/> on small FAKE payloads.</summary>
internal static class FakeMessages
{
    public const string FakeText = "fake-message-text-marker";
    public const string FakeSender = "fake-sender-uid-777";
    public const string FakeDisplayName = "Fake Display Name";

    /// <summary>A text message. For a private thread the sender is the thread id; for a group the sender is a separate uid.</summary>
    public static ChatMessage Text(
        ThreadType threadType,
        string threadId,
        string msgId,
        string cliMsgId,
        string timestamp,
        string text = FakeText,
        string sender = FakeSender,
        bool self = false,
        string displayName = FakeDisplayName)
    {
        var item = new Dictionary<string, object?>
        {
            ["msgId"] = msgId,
            ["cliMsgId"] = cliMsgId,
            ["msgType"] = "webchat",
            ["dName"] = displayName,
            ["ts"] = timestamp,
            ["content"] = text,
        };
        AddAddresses(item, threadType, threadId, sender, self);
        return Parse(threadType, item);
    }

    /// <summary>A recall notice that targets the message with the given global id and client id.</summary>
    public static ChatMessage Undo(
        ThreadType threadType,
        string threadId,
        string targetMsgId,
        string targetCliMsgId,
        string noticeMsgId = "9000001",
        string timestamp = "1700000009999")
    {
        var item = new Dictionary<string, object?>
        {
            ["msgId"] = noticeMsgId,
            ["cliMsgId"] = noticeMsgId + "1",
            ["msgType"] = "chat.undo",
            ["dName"] = FakeDisplayName,
            ["ts"] = timestamp,
            ["content"] = new Dictionary<string, object?>
            {
                ["deleteMsg"] = 1,
                ["globalMsgId"] = targetMsgId,
                ["cliMsgId"] = targetCliMsgId,
            },
        };
        AddAddresses(item, threadType, threadId, FakeSender, self: false);
        return Parse(threadType, item);
    }

    private static void AddAddresses(Dictionary<string, object?> item, ThreadType threadType, string threadId, string sender, bool self)
    {
        if (threadType == ThreadType.Group)
        {
            item["uidFrom"] = self ? "0" : sender;
            item["idTo"] = threadId;
        }
        else if (self)
        {
            item["uidFrom"] = "0";
            item["idTo"] = threadId;
        }
        else
        {
            item["uidFrom"] = threadId;
            item["idTo"] = "fake-self-uid";
        }
    }

    private static ChatMessage Parse(ThreadType threadType, Dictionary<string, object?> item)
    {
        string arrayName = threadType == ThreadType.Group ? "groupMsgs" : "msgs";
        var payload = new Dictionary<string, object?>
        {
            ["data"] = new Dictionary<string, object?> { [arrayName] = new[] { item } },
        };
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        ushort cmd = threadType == ThreadType.Group ? (ushort)521 : (ushort)501;
        var socketEvent = new SocketEvent(cmd, 0, document.RootElement.Clone());
        return Assert.Single(MessageParser.ParseEvent(socketEvent).Messages);
    }
}
