namespace Luna.Core.Storage;

/// <summary>Default location of the local message database.</summary>
public static class MessageStoreDefaults
{
    /// <summary>
    /// %LOCALAPPDATA%\Luna\history.db. Outside the repository. The folder is created by
    /// <see cref="SqliteMessageStore"/> when it opens the file, never by this property.
    /// </summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Luna",
        "history.db");
}
