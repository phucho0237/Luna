using System;
using System.Collections.Generic;

namespace Luna.App;

/// <summary>
/// Pure helper for keeping the user's selection across list refreshes: the selection is remembered by the conversation
/// key (thread type plus thread id), never by position or by row object, because every refresh builds new row objects.
/// </summary>
internal static class ConversationSelection
{
    /// <summary>
    /// The index of the row with <paramref name="key"/>, or -1 when the key is null or empty or no row has it. The
    /// comparison is ordinal (ids are opaque).
    /// </summary>
    public static int IndexOf(IReadOnlyList<ConversationRow> rows, string? key)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (string.IsNullOrEmpty(key))
        {
            return -1;
        }

        for (int i = 0; i < rows.Count; i++)
        {
            if (string.Equals(rows[i].Key, key, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}
