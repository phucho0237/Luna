namespace Luna.Protocol;

/// <summary>Snapshot of one cookie, to save and reload a session. It must be encrypted when written to disk, because it is the key to the account.</summary>
public sealed record CookieSnapshot(
    string Name,
    string Value,
    string Domain,
    string Path,
    DateTime? ExpiresUtc,
    bool Secure,
    bool HttpOnly);