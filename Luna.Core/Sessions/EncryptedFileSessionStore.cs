using System.Security.Cryptography;
using System.Text.Json;

namespace Luna.Core.Sessions;

/// <summary>Stores the session in an encrypted file. Writes through a temporary file and then renames, so a half-written file is never left behind.</summary>
public sealed class EncryptedFileSessionStore(string path, ISecretProtector protector) : ISessionStore
{
    /// <summary>%LOCALAPPDATA%\Luna\session.bin, outside the source folder so it cannot be committed by mistake.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Luna", "session.bin");

    public async Task<SessionData?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        byte[] protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        byte[]? json = null;
        try
        {
            json = protector.Unprotect(protectedBytes);
            SessionData? data = JsonSerializer.Deserialize<SessionData>(json);
            return data is { Version: SessionData.CurrentVersion } ? data : null;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            // Corrupt file, a different Windows user, or a different version: treated as no session.
            return null;
        }
        finally
        {
            if (json is not null)
            {
                CryptographicOperations.ZeroMemory(json);
            }
        }
    }

    public async Task SaveAsync(SessionData data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(data);
        try
        {
            byte[] protectedBytes = protector.Protect(json);
            string temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, protectedBytes, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
    }

    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        File.Delete(path);
        File.Delete(path + ".tmp");
        return Task.CompletedTask;
    }
}