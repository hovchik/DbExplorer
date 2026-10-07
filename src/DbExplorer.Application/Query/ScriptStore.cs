using System.Text.Json;

namespace DbExplorer.Application.Query;

/// <summary>A saved ad-hoc script, keyed by name.</summary>
public sealed class SavedScript
{
    public string Name { get; set; } = "";
    public string Sql { get; set; } = "";
}

/// <summary>One past execution, kept for quick recall.</summary>
public sealed class ScriptHistoryEntry
{
    public DateTimeOffset RanAt { get; set; }
    public string Sql { get; set; } = "";
    public bool Succeeded { get; set; }
    public string? Error { get; set; }
}

/// <summary>An open query tab, restored when the application starts again.</summary>
public sealed class QueryTabState
{
    public string Title { get; set; } = "";
    public string Sql { get; set; } = "";
    public string? FilePath { get; set; }
    public bool IsDirty { get; set; }

    /// <summary>The database the tab ran in (picked in its toolbar); null for the connection's default.</summary>
    public string? Database { get; set; }

    /// <summary>The database picked on each saved connection (by connection id); null in files written before this existed.</summary>
    public Dictionary<Guid, string>? Databases { get; set; }

    /// <summary>Whether the tab takes its name from the queries it runs; null in files written before this existed.</summary>
    public bool? AutoTitle { get; set; }
}

/// <summary>
/// Persists saved scripts and run history to disk, similar to <c>ConnectionStore</c>. Reads and writes go one at a
/// time (two tabs can finish a run together), and a damaged file reads as empty rather than failing every time.
/// </summary>
public sealed class ScriptStore(AppPaths paths)
{
    private const int MaxHistory = 200;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly SemaphoreSlim _gate = new(1, 1);

    private string ScriptsFile => Path.Combine(paths.Root, "scripts.json");
    private string HistoryFile => Path.Combine(paths.Root, "script-history.json");
    private string TabsFile => Path.Combine(paths.Root, "query-tabs.json");

    public Task<List<QueryTabState>> LoadTabsAsync(CancellationToken ct = default) =>
        Locked(() => ReadAsync<QueryTabState>(TabsFile, ct), ct);

    public Task SaveTabsAsync(IEnumerable<QueryTabState> tabs, CancellationToken ct = default) =>
        Locked(async () =>
        {
            Directory.CreateDirectory(paths.Root);
            await AtomicFile.WriteJsonAsync(TabsFile, tabs.ToList(), Json, ct);
            return true;
        }, ct);

    public Task<List<SavedScript>> LoadScriptsAsync(CancellationToken ct = default) =>
        Locked(() => ReadAsync<SavedScript>(ScriptsFile, ct), ct);

    public Task SaveScriptsAsync(IEnumerable<SavedScript> scripts, CancellationToken ct = default) =>
        Locked(async () =>
        {
            await AtomicFile.WriteJsonAsync(ScriptsFile, scripts.ToList(), Json, ct);
            return true;
        }, ct);

    public Task<List<ScriptHistoryEntry>> LoadHistoryAsync(CancellationToken ct = default) =>
        Locked(() => ReadAsync<ScriptHistoryEntry>(HistoryFile, ct), ct);

    public Task AppendHistoryAsync(ScriptHistoryEntry entry, CancellationToken ct = default) =>
        Locked(async () =>
        {
            var history = await ReadAsync<ScriptHistoryEntry>(HistoryFile, ct);
            history.Insert(0, entry);
            if (history.Count > MaxHistory) history.RemoveRange(MaxHistory, history.Count - MaxHistory);
            await AtomicFile.WriteJsonAsync(HistoryFile, history, Json, ct);
            return true;
        }, ct);

    private async Task<T> Locked<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await action(); }
        finally { _gate.Release(); }
    }

    private static async Task<List<T>> ReadAsync<T>(string file, CancellationToken ct)
    {
        if (!File.Exists(file)) return [];
        try
        {
            await using var fs = File.OpenRead(file);
            return await JsonSerializer.DeserializeAsync<List<T>>(fs, Json, ct) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
