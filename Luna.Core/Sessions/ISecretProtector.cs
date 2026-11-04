namespace Luna.Core.Sessions;

/// <summary>Encrypts and decrypts secret data when storing. The real implementation uses DPAPI, tests use a fake.</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plaintext);

    byte[] Unprotect(byte[] protectedData);
}