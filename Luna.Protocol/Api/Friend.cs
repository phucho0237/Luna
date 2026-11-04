namespace Luna.Protocol.Api;

/// <summary>
/// One friend in the friend list (<c>getfriends</c>). Every field other than <see cref="UserId"/> may be missing.
/// Not taken: dob, sdob, globalId, userKey, cover, bgavatar.
/// <see cref="ToString"/> deliberately prints only the <see cref="IsFriend"/> and <see cref="IsBlocked"/> flags:
/// identifier, name, phone number, avatar and status are personal data and must not leak into logs.
/// </summary>
/// <param name="UserId">User identifier, kept as a string (64-bit number).</param>
public sealed record Friend(
    string UserId,
    string? Username,
    string? DisplayName,
    string? ProfileName,
    string? Avatar,
    string? PhoneNumber,
    string? Status,
    int? Gender,
    bool IsFriend,
    bool IsBlocked,
    bool IsActive,
    long? LastActionTime,
    long? LastUpdateTime,
    long? CreatedTs)
{
    public override string ToString() => $"Friend {{ IsFriend = {IsFriend}, IsBlocked = {IsBlocked} }}";
}
