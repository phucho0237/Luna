using System;
using Luna.Core.Chat;
using Luna.Core.Presentation;
using Microsoft.UI.Xaml;

namespace Luna.App;

/// <summary>
/// One message as the chat list shows it. The text of a message with text is shown as it is; any other kind shows its
/// localized label (italic, secondary color, chosen by the template through the two visibility flags). The sender label
/// is only for other people's messages in a group. The time text is computed when the row is built. The row holds message
/// content, so <see cref="ToString"/> prints nothing about it.
/// </summary>
public sealed class MessageRow
{
    public MessageRow(MessageItem item, bool isGroup, DateTimeOffset nowLocal)
    {
        ArgumentNullException.ThrowIfNull(item);
        Id = item.Id;
        IsSelf = item.IsSelf;
        bool plain = item.Kind == MessageKind.Text;
        PlainText = plain ? item.Text ?? string.Empty : string.Empty;
        KindText = plain ? string.Empty : ChatText.Kind(item.Kind);
        PlainVisibility = plain ? Visibility.Visible : Visibility.Collapsed;
        KindVisibility = plain ? Visibility.Collapsed : Visibility.Visible;
        bool showSender = isGroup && !item.IsSelf;
        SenderLabel = showSender ? ChatText.Sender(item.SenderName, item.SenderShortId) : string.Empty;
        SenderVisibility = showSender ? Visibility.Visible : Visibility.Collapsed;
        TimeText = ChatTimeFormat.Format(item.TimestampMs, nowLocal);
        AutomationName = BuildAutomationName(item.IsSelf, showSender, plain);
    }

    /// <summary>The local row id.</summary>
    public long Id { get; }

    public bool IsSelf { get; }

    /// <summary>The text of a text message; empty for the other kinds.</summary>
    public string PlainText { get; }

    /// <summary>The localized label of a message without text; empty for a text message.</summary>
    public string KindText { get; }

    public Visibility PlainVisibility { get; }

    public Visibility KindVisibility { get; }

    /// <summary>The sender label; empty when it is not shown.</summary>
    public string SenderLabel { get; }

    public Visibility SenderVisibility { get; }

    /// <summary>Empty when the message has no usable timestamp.</summary>
    public string TimeText { get; }

    /// <summary>What a screen reader reads: the sender, the content and the time.</summary>
    public string AutomationName { get; }

    /// <summary>Never prints the text, the sender or the time.</summary>
    public override string ToString() => nameof(MessageRow);

    private string BuildAutomationName(bool isSelf, bool showSender, bool plain)
    {
        string who = isSelf ? ChatText.Self() : showSender ? SenderLabel : string.Empty;
        string what = plain ? PlainText : KindText;
        string[] parts = [who, what, TimeText];
        return string.Join(", ", Array.FindAll(parts, p => p.Length > 0));
    }
}
