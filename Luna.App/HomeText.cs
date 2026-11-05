using System;
using System.Globalization;
using Luna.Core.Friends;
using Luna.Core.Realtime;
using Luna.Protocol.Messaging;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Luna.App;

/// <summary>
/// Maps the home view model states to the Vietnamese lines from Resources.resw: the connection text, the friend status
/// text and the title of a conversation row. Like <see cref="LoginText"/> it never throws and never returns a raw
/// resource key: a missing key or a malformed format string falls back to a generic text, and if even that is missing
/// the result is an empty string (a conversation title then falls back to the id suffix alone).
/// </summary>
public static class HomeText
{
    private const string ConnectionPrefix = "Home_Connection_";
    private const string FriendsPrefix = "Home_Friends_";
    private const string FriendsFailurePrefix = "Home_FriendsFailure_";
    private const string UnknownUserKey = "Home_Conversation_UnknownUser";
    private const string GroupKey = "Home_Conversation_Group";

    private const string FallbackConnectionKey = ConnectionPrefix + nameof(LiveSyncState.Stopped);
    private const string FallbackFriendsKey = FriendsPrefix + nameof(FriendsState.NotLoaded);
    private const string FallbackFailureKey = FriendsPrefix + nameof(FriendsState.Failed);

    /// <summary>How many characters of the thread id a fallback title shows.</summary>
    internal const int IdSuffixLength = 4;

    private static readonly Lazy<ResourceLoader?> Loader = new(CreateLoader);

    /// <summary>The connection line for a live sync state.</summary>
    public static string Connection(LiveSyncState state)
        => Resolve(Lookup, ConnectionKey(state), FallbackConnectionKey, null);

    /// <summary>
    /// The friend list status line. A failed load is described by its failure; the waiting states take the minutes and a
    /// server failure takes the numeric code (never server text).
    /// </summary>
    public static string Friends(FriendsState state, FriendsFailure failure, int? retryMinutes, int? serverErrorCode)
        => Resolve(Lookup, FriendsKey(state, failure), state == FriendsState.Failed ? FallbackFailureKey : FallbackFriendsKey,
            ArgumentFor(state, failure, retryMinutes, serverErrorCode));

    /// <summary>
    /// The title of a conversation row: the name when there is one, otherwise the localized unknown-user or group label
    /// with the last characters of the thread id.
    /// </summary>
    public static string ConversationTitle(ThreadType threadType, string threadId, string? name)
        => Title(Lookup, threadType, threadId, name);

    /// <summary>Looks one string up by key; an empty string when it is missing.</summary>
    public static string Get(string key) => Safe(Lookup, key) ?? string.Empty;

    /// <summary>The resource key of a live sync state.</summary>
    public static string ConnectionKey(LiveSyncState state) => ConnectionPrefix + state;

    /// <summary>The resource key of the friend status: a failed load is described by its failure.</summary>
    public static string FriendsKey(FriendsState state, FriendsFailure failure)
        => state == FriendsState.Failed
            ? FriendsFailurePrefix + failure
            : FriendsPrefix + state;

    /// <summary>The resource key of the fallback title of a conversation without a name.</summary>
    public static string TitleKey(ThreadType threadType) => threadType == ThreadType.Group ? GroupKey : UnknownUserKey;

    /// <summary>The last <see cref="IdSuffixLength"/> characters of an id; a surrogate pair is never cut in half.</summary>
    internal static string IdSuffix(string? threadId)
    {
        if (string.IsNullOrEmpty(threadId))
        {
            return string.Empty;
        }

        int start = Math.Max(0, threadId.Length - IdSuffixLength);
        if (start > 0 && char.IsLowSurrogate(threadId[start]))
        {
            start++;
        }

        return threadId[start..];
    }

    /// <summary>The slot value for a status key, or null when the key takes none.</summary>
    internal static string? ArgumentFor(FriendsState state, FriendsFailure failure, int? retryMinutes, int? serverErrorCode)
    {
        int? value = state switch
        {
            FriendsState.Throttled or FriendsState.CoolingDown => retryMinutes,
            FriendsState.Failed when failure == FriendsFailure.Server => serverErrorCode,
            _ => null,
        };

        return value?.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Same as <see cref="ConversationTitle"/> with an injectable lookup, so the fallback rules need no WinUI.</summary>
    internal static string Title(Func<string, string?> lookup, ThreadType threadType, string threadId, string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name.Trim();
        }

        string suffix = IdSuffix(threadId);
        string? template = Safe(lookup, TitleKey(threadType));
        string? text = template is null ? null : Format(template, suffix);
        return text ?? "..." + suffix;
    }

    /// <summary>
    /// Looks <paramref name="key"/> up and fills its slot with <paramref name="argument"/>. A template whose slot cannot
    /// be filled (or that is malformed) is replaced by the fallback key, which must take no slot.
    /// </summary>
    internal static string Resolve(Func<string, string?> lookup, string key, string fallbackKey, string? argument)
    {
        string? template = Safe(lookup, key);
        if (template is not null)
        {
            string? text = Format(template, argument);
            if (text is not null)
            {
                return text;
            }
        }

        string? fallback = Safe(lookup, fallbackKey);
        if (fallback is not null && fallback.Contains("{0}", StringComparison.Ordinal))
        {
            fallback = null;
        }

        return fallback ?? string.Empty;
    }

    private static string? Format(string template, string? argument)
    {
        if (argument is null)
        {
            return template.Contains("{0}", StringComparison.Ordinal) ? null : template;
        }

        try
        {
            return string.Format(CultureInfo.InvariantCulture, template, argument);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string? Safe(Func<string, string?> lookup, string key)
    {
        try
        {
            string? value = lookup(key);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? Lookup(string key)
    {
        ResourceLoader? loader = Loader.Value;
        return loader?.GetString(key);
    }

    private static ResourceLoader? CreateLoader()
    {
        try
        {
            return new ResourceLoader();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
