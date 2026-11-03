using System.Text.Json;

namespace DbExplorer.Application.Query;

/// <summary>
/// The .sql files opened or saved most recently, newest first, for Open ▸ Recent files and the command palette.
/// Kept in <c>recent-files.json</c>; a damaged file reads as an empty list.
/// </summary>
public sealed class RecentFiles(AppPaths paths)
{
    public const int MaxFiles = 15;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string FilePath => Path.Combine(paths.Root, "recent-files.json");

    /// <summary><paramref name="path"/> moved to the front of <paramref name="list"/>, once, at most <see cref="MaxFiles"/> long.</summary>
    public static List<string> Push(IEnumerable<string> list, string path)
    {
        var full = Path.GetFullPath(path);
        var result = new List<string> { full };
        foreach (var p in list)
        {
            if (result.Count >= MaxFiles) break;
            if (!string.IsNullOrWhiteSpace(p) && !ClosedTabs.SamePath(p, full) && !result.Any(r => ClosedTabs.SamePath(r, p))) result.Add(p);
        }
        return result;
    }

    public async Task<IReadOnlyList<string>> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { return await ReadAsync(ct); }
        finally { _gate.Release(); }
    }

    /// <summary>Puts <paramref name="path"/> first and returns the new list.</summary>
    public Task<IReadOnlyList<string>> AddAsync(string path, CancellationToken ct = default) =>
        ChangeAsync(list => Push(list, path), ct);

    public Task<IReadOnlyList<string>> RemoveAsync(string path, CancellationToken ct = default) =>
        ChangeAsync(list => list.Where(p => !ClosedTabs.SamePath(p, path)).ToList(), ct);

    public Task<IReadOnlyList<string>> ClearAsync(CancellationToken ct = default) => ChangeAsync(_ => [], ct);

    private async Task<IReadOnlyList<string>> ChangeAsync(Func<List<string>, List<string>> change, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var list = change(await ReadAsync(ct));
            Directory.CreateDirectory(paths.Root);
            await AtomicFile.WriteJsonAsync(FilePath, list, Json, ct);
            return list;
        }
        finally { _gate.Release(); }
    }

    private async Task<List<string>> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(FilePath)) return [];
        try
        {
            await using var fs = File.OpenRead(FilePath);
            return (await JsonSerializer.DeserializeAsync<List<string>>(fs, Json, ct) ?? [])
                .Where(p => !string.IsNullOrWhiteSpace(p)).Take(MaxFiles).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>The files named on the command line (<c>DbExplorer.exe a.sql b.sql</c>, or Explorer's Open with).</summary>
public static class FileArguments
{
    /// <summary>Full paths of the arguments that are not options, once each, in order.</summary>
    public static IReadOnlyList<string> Parse(IEnumerable<string> args, string currentDirectory)
    {
        var result = new List<string>();
        foreach (var raw in args)
        {
            var arg = raw.Trim().Trim('"');
            // Options ("--software-rendering"), and the "-psn_…" macOS adds, are not files.
            if (arg.Length == 0 || arg.StartsWith('-')) continue;
            string full;
            try
            {
                full = Path.GetFullPath(arg, currentDirectory);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }
            if (!result.Any(r => ClosedTabs.SamePath(r, full))) result.Add(full);
        }
        return result;
    }
}
