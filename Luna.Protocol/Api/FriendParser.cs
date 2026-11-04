using System.Globalization;
using System.Text.Json;

namespace Luna.Protocol.Api;

/// <summary>
/// Result of reading the friend list. <see cref="Skipped"/> is the number of unusable elements (not an object,
/// missing userId, or a corrupt string). The other fields are filled in by <see cref="ApiClient.GetAllFriendsAsync"/>.
/// <see cref="ToString"/> prints counts only.
/// </summary>
/// <param name="Pages">Number of pages requested.</param>
/// <param name="Truncated">Stopped at maxPages without a signal that the list ended (friends may remain unfetched).</param>
/// <param name="PageParameterIgnored">The server seems to ignore the page parameter (a later page repeats the previous page entirely).</param>
/// <param name="EndedByNoData">
/// Stopped because the server returned code 112 on page 2 or later. This is an INFERENCE ("The server returns 112 on a later page"), not
/// proof that the list is complete: treat the list as complete only when the real friend count is known too (not verified on the live server
/// for a page in the middle of the list).
/// </param>
public sealed record FriendListResult(
    IReadOnlyList<Friend> Friends,
    int Skipped,
    int Pages = 0,
    bool Truncated = false,
    bool PageParameterIgnored = false,
    bool EndedByNoData = false)
{
    public override string ToString() =>
        $"FriendListResult {{ Friends = {Friends.Count}, Skipped = {Skipped}, Pages = {Pages}, Truncated = {Truncated}, PageParameterIgnored = {PageParameterIgnored}, EndedByNoData = {EndedByNoData} }}";
}

/// <summary>Converts the <c>data</c> of <c>getfriends</c> into <see cref="Friend"/>; corrupt elements are skipped and counted.</summary>
public static class FriendParser
{
    /// <exception cref="InvalidDataException"><paramref name="data"/> is not an array.</exception>
    public static FriendListResult Parse(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The friend list is not a JSON array.");
        }

        var friends = new List<Friend>();
        int skipped = 0;
        foreach (JsonElement element in data.EnumerateArray())
        {
            if (TryParseFriend(element, out Friend? friend))
            {
                friends.Add(friend);
            }
            else
            {
                skipped++;
            }
        }

        return new FriendListResult(friends, skipped);
    }

    /// <summary>Reads one element; false if it is unusable (not an object, missing userId, corrupt string).</summary>
    internal static bool TryParseFriend(JsonElement element, out Friend friend)
    {
        friend = null!;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        try
        {
            string? userId = ReadId(element);
            if (string.IsNullOrEmpty(userId))
            {
                return false;
            }

            friend = new Friend(
                userId,
                Text(element, "username"),
                Text(element, "displayName"),
                Text(element, "zaloName"),
                Text(element, "avatar"),
                Text(element, "phoneNumber"),
                Text(element, "status"),
                Int(element, "gender"),
                Flag(element, "isFr"),
                Flag(element, "isBlocked"),
                Flag(element, "isActive"),
                Long(element, "lastActionTime"),
                Long(element, "lastUpdateTime"),
                Long(element, "createdTs"));
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or JsonException)
        {
            // A string with a lone surrogate makes GetString throw InvalidOperationException.
            return false;
        }
    }

    /// <summary>userId accepts only a string or a number (original text kept verbatim); boolean/object/null is not an id.</summary>
    internal static string? ReadId(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("userId", out JsonElement id)
            || id.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
        {
            return null;
        }

        return JsonFields.ReadScalar(id);
    }

    // The field readers degrade errors to the field level: a string with a lone surrogate makes GetString throw
    // InvalidOperationException; the field is then null (the flag is false) and the friend is still kept.
    private static string? Text(JsonElement element, string name)
    {
        try
        {
            return JsonFields.ReadScalar(element, name);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static int? Int(JsonElement element, string name) =>
        int.TryParse(Text(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : null;

    private static long? Long(JsonElement element, string name) =>
        long.TryParse(Text(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)
            ? value
            : null;

    private static bool Flag(JsonElement element, string name) => Int(element, name) == 1;
}
