using DbExplorer.Application.Copy;
using DbExplorer.Core.Connections;

namespace DbExplorer.Application.Connections;

/// <summary>
/// SQLite files handed to the app by Explorer (Open with, a double-click, drag-drop) or the command line: telling them
/// from .sql scripts, and the saved connection that opens one.
/// </summary>
public static class DatabaseFiles
{
    /// <summary>The extensions offered in the Open SQLite file dialog and registered for Open with.</summary>
    public static readonly string[] Extensions = [".db", ".sqlite", ".sqlite3", ".db3"];

    private static readonly byte[] Header = "SQLite format 3\0"u8.ToArray();

    /// <summary>
    /// True for a SQLite database: a file that starts with SQLite's header, or an empty or not-yet-readable one with a
    /// SQLite extension. A .sql script never counts, whatever is in it.
    /// </summary>
    public static bool IsDatabaseFile(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".sql", StringComparison.OrdinalIgnoreCase)) return false;
        var known = Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = new byte[Header.Length];
            var read = stream.ReadAtLeast(start, start.Length, throwOnEndOfStream: false);
            return read == 0 ? known : read == Header.Length && start.AsSpan().SequenceEqual(Header);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return known;
        }
    }

    /// <summary>
    /// The saved SQLite connection for <paramref name="path"/>, or a new one named after the file (not saved yet:
    /// <c>IsNew</c> says so). Paths are compared in full, ignoring case on Windows.
    /// </summary>
    public static (ConnectionProfile Profile, bool IsNew) ProfileFor(IEnumerable<ConnectionProfile> profiles, string path)
    {
        var full = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var existing = profiles.FirstOrDefault(p => p.IsFile && !string.IsNullOrWhiteSpace(p.Host) &&
                                                    string.Equals(SafeFullPath(p.Host), full, comparison));
        if (existing is not null) return (existing, false);
        return (new ConnectionProfile
        {
            Name = Path.GetFileNameWithoutExtension(full) is { Length: > 0 } name ? name : Path.GetFileName(full),
            ProviderKey = SqlDialect.SqliteKey,
            Host = full
        }, true);
    }

    private static string SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path.Trim().Trim('"')); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }
}
