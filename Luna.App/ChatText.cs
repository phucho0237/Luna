using System;
using System.Globalization;
using Luna.Core.Chat;

namespace Luna.App;

/// <summary>
/// Maps the chat view model values to the Vietnamese lines from Resources.resw: the label of a message without text, the
/// state line and the sender label. Like <see cref="HomeText"/> it never throws and never returns a raw resource key: a
/// missing key or a malformed format string falls back to another text, and if even that is missing the result is an empty
/// string (a sender label then falls back to the short id alone).
/// </summary>
public static class ChatText
{
    private const string KindPrefix = "Chat_Kind_";
    private const string StatePrefix = "Chat_State_";
    private const string SenderUnknownKey = "Chat_Sender_Unknown";
    private const string SenderSelfKey = "Chat_Sender_Self";

    /// <summary>The resource key of the label of a message kind.</summary>
    public static string KindKey(MessageKind kind) => KindPrefix + kind;

    /// <summary>The resource key of a chat state line.</summary>
    public static string StateKey(ChatState state) => StatePrefix + state;

    /// <summary>The label of a message without text (a text message has none: an empty string).</summary>
    public static string Kind(MessageKind kind)
        => kind == MessageKind.Text
            ? string.Empty
            : HomeText.Resolve(Lookup, KindKey(kind), KindKey(MessageKind.Other), null);

    /// <summary>The state line of the pane.</summary>
    public static string State(ChatState state)
        => HomeText.Resolve(Lookup, StateKey(state), StateKey(ChatState.Idle), null);

    /// <summary>The label of the user's own messages.</summary>
    public static string Self() => Lookup(SenderSelfKey) ?? string.Empty;

    /// <summary>The sender name when there is one, otherwise the localized unknown-member label with the short id.</summary>
    public static string Sender(string? name, string shortId)
        => Sender(Lookup, name, shortId);

    /// <summary>Same as <see cref="Sender(string?, string)"/> with an injectable lookup.</summary>
    internal static string Sender(Func<string, string?> lookup, string? name, string shortId)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name.Trim();
        }

        string? template = null;
        try
        {
            template = lookup(SenderUnknownKey);
        }
        catch (Exception)
        {
            // A failing lookup degrades to the fallback below.
        }

        if (!string.IsNullOrWhiteSpace(template))
        {
            try
            {
                return string.Format(CultureInfo.InvariantCulture, template, shortId);
            }
            catch (FormatException)
            {
                // Malformed template: fall through to the fallback.
            }
        }

        return "..." + shortId;
    }

    // HomeText.Get never throws and returns an empty string for a missing key.
    private static string? Lookup(string key)
    {
        string value = HomeText.Get(key);
        return value.Length == 0 ? null : value;
    }
}
