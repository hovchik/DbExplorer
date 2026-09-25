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

/// <summary>Persists saved scripts and run history to disk, similar to <c>ConnectionStore</c>.</summary>
public sealed class ScriptStore(AppPaths paths)
{
    private const int MaxHistory = 200;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private string ScriptsFile => Path.Combine(paths.Root, "scripts.json");
    private string HistoryFile => Path.Combine(paths.Root, "script-history.json");

    public async Task<List<SavedScript>> LoadScriptsAsync(CancellationToken ct = default)
    {
        if (!File.Exists(ScriptsFile)) return [];
        await using var fs = File.OpenRead(ScriptsFile);
        return await JsonSerializer.DeserializeAsync<List<SavedScript>>(fs, Json, ct) ?? [];
    }

    public async Task SaveScriptsAsync(IEnumerable<SavedScript> scripts, CancellationToken ct = default)
    {
        var tmp = ScriptsFile + ".tmp";
        await using (var fs = File.Create(tmp))
            await JsonSerializer.SerializeAsync(fs, scripts.ToList(), Json, ct);
        File.Move(tmp, ScriptsFile, overwrite: true);
    }

    public async Task<List<ScriptHistoryEntry>> LoadHistoryAsync(CancellationToken ct = default)
    {
        if (!File.Exists(HistoryFile)) return [];
        await using var fs = File.OpenRead(HistoryFile);
        return await JsonSerializer.DeserializeAsync<List<ScriptHistoryEntry>>(fs, Json, ct) ?? [];
    }

    public async Task AppendHistoryAsync(ScriptHistoryEntry entry, CancellationToken ct = default)
    {
        var history = await LoadHistoryAsync(ct);
        history.Insert(0, entry);
        if (history.Count > MaxHistory) history.RemoveRange(MaxHistory, history.Count - MaxHistory);

        var tmp = HistoryFile + ".tmp";
        await using (var fs = File.Create(tmp))
            await JsonSerializer.SerializeAsync(fs, history, Json, ct);
        File.Move(tmp, HistoryFile, overwrite: true);
    }
}
