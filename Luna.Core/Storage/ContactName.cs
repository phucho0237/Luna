namespace Luna.Core.Storage;

/// <summary>One cached friend name. The user id is a 64-bit number kept as a string.</summary>
public sealed record ContactName(string UserId, string Name)
{
    /// <summary>Never prints the user id or the name.</summary>
    public override string ToString() => nameof(ContactName);
}
