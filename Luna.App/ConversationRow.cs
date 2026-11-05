using System;
using Luna.Core.Presentation;
using Luna.Core.ViewModels;
using Luna.Protocol.Messaging;

namespace Luna.App;

/// <summary>
/// One row of the conversation list as the view shows it: the title and the time text, plus the thread key that T7c
/// uses to open the conversation. The time text is computed when the row is built (at every list refresh), so it is not
/// live-updated when the day changes while the window stays open; the next refresh corrects it. The row holds a thread
/// id, so <see cref="ToString"/> prints nothing about it.
/// </summary>
public sealed class ConversationRow
{
    public ConversationRow(ConversationItem item, DateTimeOffset nowLocal)
    {
        ArgumentNullException.ThrowIfNull(item);
        ThreadType = item.ThreadType;
        ThreadId = item.ThreadId;
        Key = KeyOf(item.ThreadType, item.ThreadId);
        Title = HomeText.ConversationTitle(item.ThreadType, item.ThreadId, item.Name);
        TimeText = ChatTimeFormat.Format(item.LastTimestampMs, nowLocal);
    }

    public ThreadType ThreadType { get; }

    /// <summary>The thread id; kept for T7c, never shown.</summary>
    public string ThreadId { get; }

    /// <summary>The identity of the conversation: thread type plus thread id.</summary>
    public string Key { get; }

    public string Title { get; }

    /// <summary>Empty when the conversation has no usable timestamp.</summary>
    public string TimeText { get; }

    /// <summary>The name a screen reader reads: the title and, when there is one, the time.</summary>
    public string AutomationName => TimeText.Length == 0 ? Title : Title + ", " + TimeText;

    /// <summary>The key of a conversation; the type is part of it so a user and a group with the same id stay distinct.</summary>
    public static string KeyOf(ThreadType threadType, string threadId) => (int)threadType + ":" + threadId;

    /// <summary>Never prints the title or the thread id.</summary>
    public override string ToString() => nameof(ConversationRow);
}
