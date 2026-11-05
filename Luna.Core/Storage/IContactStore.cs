namespace Luna.Core.Storage;

/// <summary>Local cache of friend names (user id to display name), kept in the same database as the messages.</summary>
public interface IContactStore
{
    /// <summary>
    /// Replaces ALL cached contacts with <paramref name="contacts"/> in ONE transaction: either the new set is stored or
    /// the old one stays. A later entry with the same user id wins.
    /// </summary>
    Task ReplaceContactsAsync(IReadOnlyList<ContactName> contacts, CancellationToken cancellationToken = default);

    /// <summary>Returns every cached contact as user id to name. Empty when nothing is cached.</summary>
    Task<IReadOnlyDictionary<string, string>> GetContactsAsync(CancellationToken cancellationToken = default);
}
