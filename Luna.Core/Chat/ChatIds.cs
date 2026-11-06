namespace Luna.Core.Chat;

/// <summary>Short forms of ids for labels. Ids are sensitive: only a few trailing characters are ever shown.</summary>
public static class ChatIds
{
    /// <summary>How many characters of an id a short id has.</summary>
    public const int ShortIdLength = 4;

    /// <summary>
    /// The last <see cref="ShortIdLength"/> characters of <paramref name="id"/>; a surrogate pair is never cut in half.
    /// An empty string for a null or empty id.
    /// </summary>
    public static string ShortId(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return string.Empty;
        }

        int start = Math.Max(0, id.Length - ShortIdLength);
        if (start > 0 && char.IsLowSurrogate(id[start]))
        {
            start++;
        }

        return id[start..];
    }
}
