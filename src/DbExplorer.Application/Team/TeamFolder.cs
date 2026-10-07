using System.Security.Cryptography;
using System.Text;

namespace DbExplorer.Application.Team;

public enum TeamItemKind
{
    Connection,
    Query,
    Snippet
}

/// <summary>A file in the team folder as it is on disk now.</summary>
/// <param name="RelativePath">Forward slashes, from the team folder, e.g. <c>queries/Reports/Monthly sales.sql</c>.</param>
/// <param name="Name">What the list shows: the file name without extension, with sub-folders for queries ("Reports/Monthly sales").</param>
public sealed record TeamFile(TeamItemKind Kind, string RelativePath, string Name, string Hash, DateTime ModifiedUtc);

/// <summary>Where a shared item was written. <paramref name="Conflict"/>: a teammate had changed the file since this
/// app last saw it, so it was kept and ours was written next to it under another name.</summary>
public sealed record TeamWriteResult(string RelativePath, string Hash, bool Conflict, bool Unchanged);

/// <summary>
/// A folder the team already shares (OneDrive, Dropbox, Google Drive, a network share, a folder in a git repo):
/// <c>connections/*.dbxconnections</c> (one connection per file, in the export format), <c>queries/**/*.sql</c> and
/// <c>snippets/*.sql</c>. Plain files, so the sync tool, a diff or a code review can read them.
/// </summary>
public sealed class TeamFolder(string root)
{
    public const string ConnectionsDirectory = "connections";
    public const string QueriesDirectory = "queries";
    public const string SnippetsDirectory = "snippets";
    public const string ConnectionExtension = ".dbxconnections";
    public const string SqlExtension = ".sql";

    /// <summary>Temporary files start with this and are skipped, so a half-written file is never offered.</summary>
    private const string TempPrefix = ".~dbx-";

    public string Root { get; } = root;

    public bool Exists => Directory.Exists(Root);

    /// <summary>Every shared item. Hidden files, temporary files and anything outside the three folders are ignored.</summary>
    public IReadOnlyList<TeamFile> List()
    {
        var result = new List<TeamFile>();
        Add(TeamItemKind.Connection, ConnectionsDirectory, ConnectionExtension, SearchOption.TopDirectoryOnly);
        Add(TeamItemKind.Query, QueriesDirectory, SqlExtension, SearchOption.AllDirectories);
        Add(TeamItemKind.Snippet, SnippetsDirectory, SqlExtension, SearchOption.TopDirectoryOnly);
        return result;

        void Add(TeamItemKind kind, string directory, string extension, SearchOption depth)
        {
            var dir = Path.Combine(Root, directory);
            if (!Directory.Exists(dir)) return;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*" + extension, depth).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }

            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(Root, file).Replace('\\', '/');
                if (relative.Split('/').Any(part => part.StartsWith('.') || part.StartsWith('~'))) continue;
                // "*.sql" also matches "x.sqlite" on Windows (8.3 names); keep the exact extension only.
                if (!file.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var name = relative[(directory.Length + 1)..^extension.Length];
                    result.Add(new TeamFile(kind, relative, name, HashOf(File.ReadAllBytes(file)), File.GetLastWriteTimeUtc(file)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Locked while the sync tool writes it: picked up on the next scan.
                }
            }
        }
    }

    public string ReadText(string relativePath) => File.ReadAllText(FullPath(relativePath));

    /// <summary>
    /// Writes <paramref name="content"/> as <paramref name="name"/>. An existing file is replaced only when it is still
    /// the version this app last saw (<paramref name="expectedHash"/>); otherwise both are kept and ours is written as
    /// "name (<paramref name="member"/>)". Same content again writes nothing.
    /// </summary>
    /// <param name="canReplace">Extra check on the existing file (e.g. that it is the same connection).</param>
    /// <param name="copyContent">The content for the kept-both copy, given its suffix (" (alice)"); by default the same.</param>
    public TeamWriteResult Write(TeamItemKind kind, string name, string content, string? expectedHash, string member,
        Func<string, bool>? canReplace = null, Func<string, string>? copyContent = null)
    {
        var (directory, extension) = Location(kind);
        var stem = SafeRelativeName(name, allowFolders: kind == TeamItemKind.Query);
        var relative = $"{directory}/{stem}{extension}";
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = HashOf(bytes);
        var conflict = false;

        if (File.Exists(FullPath(relative)))
        {
            var existing = File.ReadAllBytes(FullPath(relative));
            var existingHash = HashOf(existing);
            if (existingHash == hash) return new TeamWriteResult(relative, hash, false, true);
            var replaceable = existingHash == expectedHash && (canReplace?.Invoke(Encoding.UTF8.GetString(existing)) ?? true);
            if (!replaceable)
            {
                conflict = true;
                (relative, var suffix) = FreeName(directory, stem, extension, SafeFileName(member), path => File.Exists(path) &&
                    copyContent is null && HashOf(File.ReadAllBytes(path)) == hash);
                if (copyContent is not null) bytes = Encoding.UTF8.GetBytes(copyContent(suffix));
                hash = HashOf(bytes);
                if (File.Exists(FullPath(relative))) return new TeamWriteResult(relative, hash, true, true);
            }
        }

        WriteAtomically(relative, bytes);
        return new TeamWriteResult(relative, hash, conflict, false);
    }

    /// <summary>Deletes a shared file; false when a teammate changed it since <paramref name="expectedHash"/> (it is kept).</summary>
    public bool Delete(string relativePath, string expectedHash)
    {
        var path = FullPath(relativePath);
        if (!File.Exists(path)) return true;
        if (HashOf(File.ReadAllBytes(path)) != expectedHash) return false;
        File.Delete(path);
        return true;
    }

    /// <summary>"name (alice).sql", or "name (alice 2).sql" when that is taken (by different content).</summary>
    private (string Relative, string Suffix) FreeName(string directory, string stem, string extension, string member, Func<string, bool> sameContent)
    {
        for (var n = 1; ; n++)
        {
            var suffix = $" ({member}{(n == 1 ? "" : " " + n)})";
            var candidate = $"{directory}/{stem}{suffix}{extension}";
            var path = FullPath(candidate);
            if (!File.Exists(path) || sameContent(path)) return (candidate, suffix);
        }
    }

    private void WriteAtomically(string relative, byte[] bytes)
    {
        var path = FullPath(relative);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var tmp = Path.Combine(directory, TempPrefix + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>The file under the team folder; refuses paths that would leave it.</summary>
    public string FullPath(string relativePath)
    {
        var root = Path.GetFullPath(Root);
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{relativePath} is outside the team folder.");
        return full;
    }

    public static (string Directory, string Extension) Location(TeamItemKind kind) => kind switch
    {
        TeamItemKind.Connection => (ConnectionsDirectory, ConnectionExtension),
        TeamItemKind.Query => (QueriesDirectory, SqlExtension),
        _ => (SnippetsDirectory, SqlExtension)
    };

    public static string HashOf(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>"Reports / Monthly: sales" → "Reports/Monthly_ sales" (folders only for queries).</summary>
    public static string SafeRelativeName(string name, bool allowFolders)
    {
        var parts = allowFolders
            ? name.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [name.Replace('/', '_').Replace('\\', '_')];
        var safe = parts.Select(SafeFileName).Where(p => p.Length > 0).ToList();
        return safe.Count == 0 ? "Untitled" : string.Join("/", safe);
    }

    /// <summary>A file name valid on Windows, macOS and Linux, so the folder syncs everywhere.</summary>
    public static string SafeFileName(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.Trim())
            sb.Append(c < 32 || "<>:\"/\\|?*".Contains(c) ? '_' : c);
        var result = sb.ToString().TrimEnd('.', ' ').TrimStart('.', '~', ' ');
        if (result.Length > 120) result = result[..120].TrimEnd('.', ' ');
        var stem = result.Split('.')[0].ToUpperInvariant();
        string[] reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];
        return reserved.Contains(stem) ? "_" + result : result;
    }
}
