using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Luna.Core.Sessions;

/// <summary>
/// DPAPI at the scope of the current Windows user: another user or another machine cannot decrypt.
/// Note: it does not protect against malware running under this very account.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Luna.Session.v1");

    public byte[] Protect(byte[] plaintext) =>
        ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] protectedData) =>
        ProtectedData.Unprotect(protectedData, Entropy, DataProtectionScope.CurrentUser);
}