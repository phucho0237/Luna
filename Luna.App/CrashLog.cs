using System;
using System.IO;
using Luna.Core.Sessions;

namespace Luna.App;

/// <summary>
/// Appends one line per unhandled exception to crash.log next to the session file. The line holds the UTC time, the
/// exception type, the HResult and the stack trace; the exception message is never written because it can carry data.
/// The file is bounded and writing never throws.
/// </summary>
public static class CrashLog
{
    /// <summary>When the file is larger than this, a new file is started before the next line is appended.</summary>
    internal const long MaxBytes = 256 * 1024;

    private static readonly object Gate = new();

    /// <summary>Same folder as the session file (LocalApplicationData\Luna), without repeating the path.</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Path.GetDirectoryName(EncryptedFileSessionStore.DefaultPath) ?? string.Empty, "crash.log");

    public static void Write(Exception exception, string? path = null)
    {
        try
        {
            string target = path ?? DefaultPath;
            string line = Format(DateTime.UtcNow, exception);
            lock (Gate)
            {
                string? folder = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                var info = new FileInfo(target);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Delete(target);
                }

                File.AppendAllText(target, line + Environment.NewLine);
            }
        }
        catch (Exception)
        {
            // Diagnostics must never take the app down.
        }
    }

    internal static string Format(DateTime utc, Exception exception)
    {
        string type = exception.GetType().FullName ?? exception.GetType().Name;
        string stack = (exception.StackTrace ?? string.Empty)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", " | ", StringComparison.Ordinal);
        return $"{utc:yyyy-MM-ddTHH:mm:ssZ} {type} HResult=0x{exception.HResult:X8} {stack}";
    }
}
