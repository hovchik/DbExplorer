using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DbExplorer.Core.Backup;

/// <summary>One copy of a command-line tool that was found, with its version.</summary>
public sealed record ClientTool(string Path, Version Version, string VersionText)
{
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public override string ToString() => $"{Name} {VersionText} in {Folder}";
}

/// <summary>How a tool run ended: its exit code and the last lines it printed.</summary>
public sealed record ClientToolResult(int ExitCode, IReadOnlyList<string> LastLines)
{
    public string Output => string.Join(Environment.NewLine, LastLines);
}

/// <summary>
/// Finds and runs the engines' command-line tools (pg_dump, mysqldump…). Arguments go through
/// <see cref="ProcessStartInfo.ArgumentList"/>, never a joined command line, and passwords only through the
/// environment or a temporary option file, so neither shows in the process list.
/// </summary>
public static partial class ClientTools
{
    private const int KeptLines = 60;

    /// <summary>The folders to look in, best first: the user's pick, the usual install folders (newest version first
    /// when the folder names carry one), then PATH.</summary>
    public static IReadOnlyList<string> CandidateFolders(string? userFolder, IEnumerable<string> installPatterns)
    {
        var folders = new List<string>();
        if (!string.IsNullOrWhiteSpace(userFolder)) folders.Add(userFolder.Trim());
        foreach (var pattern in installPatterns) folders.AddRange(Expand(pattern));
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        folders.AddRange(path.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return folders.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToList();
    }

    /// <summary>Expands one "*" in a folder pattern ("C:\Program Files\PostgreSQL\*\bin"), newest-looking first.</summary>
    public static IEnumerable<string> Expand(string pattern)
    {
        pattern = Environment.ExpandEnvironmentVariables(pattern);
        var star = pattern.IndexOf('*');
        if (star < 0) return Directory.Exists(pattern) ? [pattern] : [];

        var cut = pattern.LastIndexOfAny(['/', '\\'], star);
        if (cut < 0) return [];
        var parent = pattern[..cut];
        var next = pattern.IndexOfAny(['/', '\\'], star);
        var segment = next < 0 ? pattern[(cut + 1)..] : pattern[(cut + 1)..next];
        var rest = next < 0 ? "" : pattern[next..];
        try
        {
            if (!Directory.Exists(parent)) return [];
            return Directory.GetDirectories(parent, segment)
                .OrderByDescending(d => VersionIn(System.IO.Path.GetFileName(d)) ?? new Version(0, 0))
                .ThenByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .Select(d => d + rest)
                .Where(Directory.Exists)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The executable of <paramref name="name"/> in a folder ("pg_dump.exe" on Windows), or null.</summary>
    public static string? ExecutableIn(string folder, string name)
    {
        try
        {
            var file = System.IO.Path.Combine(folder, OperatingSystem.IsWindows() ? name + ".exe" : name);
            return File.Exists(file) ? file : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Every copy of the tools named in <paramref name="names"/> in these folders that answers --version.</summary>
    public static async Task<IReadOnlyList<ClientTool>> FindAsync(
        IReadOnlyList<string> folders, IReadOnlyList<string> names, CancellationToken ct)
    {
        var found = new List<ClientTool>();
        foreach (var folder in folders)
        foreach (var name in names)
        {
            if (ExecutableIn(folder, name) is not { } exe) continue;
            if (await VersionOfAsync(exe, ct) is { } tool) found.Add(tool);
        }
        return found;
    }

    public static async Task<ClientTool?> VersionOfAsync(string exe, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var lines = new List<string>();
            var result = await RunAsync(exe, ["--version"], null, lines.Add, null, null, timeout.Token);
            var text = string.Join(" ", lines.Concat(result.LastLines));
            return result.ExitCode == 0 && ParseVersion(text) is { } v ? new ClientTool(exe, v.Version, v.Text) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>The first version number in a --version line: "pg_dump (PostgreSQL) 16.4" → 16.4,
    /// "mysqldump  Ver 8.0.36 for Linux" → 8.0.36, "mariadb-dump from 11.4.2-MariaDB, client 10.19" → 11.4.2.</summary>
    public static (Version Version, string Text)? ParseVersion(string text)
    {
        var m = VersionPattern().Match(text);
        if (!m.Success) return null;
        var parts = m.Value.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        var version = parts.Length switch
        {
            1 => new Version(parts[0], 0),
            2 => new Version(parts[0], parts[1]),
            _ => new Version(parts[0], parts[1], parts[2])
        };
        return (version, m.Value);
    }

    /// <summary>The version in an install folder's name: "16", "MySQL Server 8.0", "MariaDB 11.4".</summary>
    private static Version? VersionIn(string folderName)
    {
        var m = FolderVersionPattern().Match(folderName);
        if (!m.Success) return null;
        var parts = m.Value.Split('.').Take(3).Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        return parts.Length == 1 ? new Version(parts[0], 0) : parts.Length == 2 ? new Version(parts[0], parts[1]) : new Version(parts[0], parts[1], parts[2]);
    }

    [GeneratedRegex(@"\d+(?:\.\d+)*", RegexOptions.CultureInvariant)]
    private static partial Regex FolderVersionPattern();

    [GeneratedRegex(@"(?<![\w.])\d+\.\d+(?:\.\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    /// <summary>
    /// Runs a tool to the end, handing each line it writes to standard error (and to standard output when
    /// <paramref name="stdout"/> is null) to <paramref name="line"/>. Cancelling kills it and its children.
    /// </summary>
    /// <param name="stdin">Fed to the tool's standard input, then closed (e.g. a script for the mysql client).</param>
    /// <param name="stdinProgress">Bytes of <paramref name="stdin"/> fed so far.</param>
    public static async Task<ClientToolResult> RunAsync(
        string exe, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string?>? environment,
        Action<string> line, Stream? stdin, IProgress<long>? stdinProgress, CancellationToken ct, Stream? stdout = null)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = stdin is not null
        };
        foreach (var a in arguments) info.ArgumentList.Add(a);
        if (environment is not null)
            foreach (var (key, value) in environment)
            {
                if (value is null) info.Environment.Remove(key);
                else info.Environment[key] = value;
            }

        var last = new Queue<string>();
        void Keep(string text)
        {
            lock (last)
            {
                last.Enqueue(text);
                if (last.Count > KeptLines) last.Dequeue();
            }
            line(text);
        }

        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new BackupException($"Could not start {System.IO.Path.GetFileName(exe)}: {ex.Message}", inner: ex);
        }

        using var kill = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception) { /* already gone */ }
        });

        var errors = PumpLines(process.StandardError, Keep);
        var output = stdout is null
            ? PumpLines(process.StandardOutput, Keep)
            : process.StandardOutput.BaseStream.CopyToAsync(stdout, CancellationToken.None);
        if (stdin is not null)
        {
            try
            {
                var buffer = new byte[81920];
                long fed = 0;
                int read;
                var input = process.StandardInput.BaseStream;
                while ((read = await stdin.ReadAsync(buffer, ct)) > 0)
                {
                    await input.WriteAsync(buffer.AsMemory(0, read), ct);
                    fed += read;
                    stdinProgress?.Report(fed);
                }
                await input.FlushAsync(ct);
            }
            catch (IOException)
            {
                // The tool stopped reading (it failed); its exit code and output say why.
            }
            finally
            {
                try { process.StandardInput.Close(); } catch (IOException) { }
            }
        }

        await process.WaitForExitAsync(CancellationToken.None);
        await Task.WhenAll(errors, output);
        ct.ThrowIfCancellationRequested();
        lock (last) return new ClientToolResult(process.ExitCode, last.ToList());
    }

    private static async Task PumpLines(StreamReader reader, Action<string> line)
    {
        while (await reader.ReadLineAsync() is { } text)
            if (text.Length > 0) line(text);
    }

    /// <summary>A file only the current user can read, for passing a password to a tool; deleted on dispose.</summary>
    public static PrivateFile WritePrivateFile(string content) => new(content);

    public sealed class PrivateFile : IDisposable
    {
        public string Path { get; }

        internal PrivateFile(string content)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dbx-" + Guid.NewGuid().ToString("N") + ".cnf");
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(Path, content);
            }
            else
            {
                // Created 0600 before anything is written, so the password is never readable by others.
                using var stream = new FileStream(Path, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew, Access = FileAccess.Write,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                });
                using var writer = new StreamWriter(stream);
                writer.Write(content);
            }
        }

        public void Dispose()
        {
            try { File.Delete(Path); } catch (Exception) { /* temp folder; best effort */ }
        }
    }
}
