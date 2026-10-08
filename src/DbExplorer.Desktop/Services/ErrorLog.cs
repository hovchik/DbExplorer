using System.Text;

namespace DbExplorer.Desktop.Services;

/// <summary>
/// Last-resort record of errors nothing else handled, so a user can send the file instead of describing a crash.
/// Kept in <c>&lt;app data&gt;/DbExplorer/logs/errors.log</c>, rolled over at 1 MB. Never throws.
/// </summary>
public static class ErrorLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();

    public static string FilePath { get; } = Path.Combine(DbExplorer.Application.AppPaths.DefaultRoot, "logs", "errors.log");

    public static void Write(string source, Exception? exception) =>
        Append(source, exception?.ToString() ?? "(no exception object)");

    /// <summary>A line that is worth having next to the errors (a UI hang, a rendering warning), without an exception.</summary>
    public static void Note(string source, string message) => Append(source, message);

    private static void Append(string source, string text)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
                    File.Move(FilePath, FilePath + ".1", overwrite: true);

                var entry = new StringBuilder()
                    .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz")).Append(" [").Append(source).Append("] ")
                    .Append(typeof(ErrorLog).Assembly.GetName().Version).AppendLine()
                    .AppendLine(text)
                    .AppendLine();
                File.AppendAllText(FilePath, entry.ToString());
            }
        }
        catch
        {
            // Logging must never be the reason the app goes down.
        }
    }
}
