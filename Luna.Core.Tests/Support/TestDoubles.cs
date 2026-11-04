using System.Net;
using Luna.Core.Sessions;

namespace Luna.Core.Tests.Support;

/// <summary>Fake "encryption": XOR of each byte. Enough to prove the file on disk is no longer plain text.</summary>
internal sealed class XorProtector : ISecretProtector
{
    public byte[] Protect(byte[] plaintext) => Xor(plaintext);

    public byte[] Unprotect(byte[] protectedData) => Xor(protectedData);

    private static byte[] Xor(byte[] data) => data.Select(b => (byte)(b ^ 0x5A)).ToArray();
}

internal sealed class ThrowingProtector : ISecretProtector
{
    public byte[] Protect(byte[] plaintext) => plaintext;

    public byte[] Unprotect(byte[] protectedData) =>
        throw new System.Security.Cryptography.CryptographicException("Could not decrypt.");
}

internal sealed class InMemoryStore(SessionData? initial) : ISessionStore
{
    public SessionData? Current { get; private set; } = initial;

    public int SaveCount { get; private set; }

    public int DeleteCount { get; private set; }

    public Task<SessionData?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);

    public Task SaveAsync(SessionData data, CancellationToken cancellationToken = default)
    {
        Current = data;
        SaveCount++;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        Current = null;
        DeleteCount++;
        return Task.CompletedTask;
    }
}

internal sealed class FakeHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respond(request)) });
}