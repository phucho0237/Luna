namespace Luna.Core.Sessions;

public interface ISessionStore
{
    /// <returns>The saved session, or null if there is none or it cannot be read.</returns>
    Task<SessionData?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(SessionData data, CancellationToken cancellationToken = default);

    Task DeleteAsync(CancellationToken cancellationToken = default);
}