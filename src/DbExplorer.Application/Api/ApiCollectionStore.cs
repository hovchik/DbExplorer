using System.Text.Json;
using System.Text.Json.Serialization;

namespace DbExplorer.Application.Api;

/// <summary>Imported collections, environments and the send history, as JSON files next to the saved connections.</summary>
public sealed class ApiCollectionStore(AppPaths paths)
{
    private const int MaxHistory = 200;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public string CollectionsFile => Path.Combine(paths.Root, "api-collections.json");
    public string EnvironmentsFile => Path.Combine(paths.Root, "api-environments.json");
    public string HistoryFile => Path.Combine(paths.Root, "api-history.json");

    public Task<List<ApiCollection>> LoadCollectionsAsync(CancellationToken ct = default) => ReadAsync<ApiCollection>(CollectionsFile, ct);

    public Task SaveCollectionsAsync(IEnumerable<ApiCollection> collections, CancellationToken ct = default)
        => WriteAsync(CollectionsFile, collections.ToList(), ct);

    public Task<List<ApiEnvironment>> LoadEnvironmentsAsync(CancellationToken ct = default) => ReadAsync<ApiEnvironment>(EnvironmentsFile, ct);

    public Task SaveEnvironmentsAsync(IEnumerable<ApiEnvironment> environments, CancellationToken ct = default)
        => WriteAsync(EnvironmentsFile, environments.ToList(), ct);

    public Task<List<ApiHistoryEntry>> LoadHistoryAsync(CancellationToken ct = default) => ReadAsync<ApiHistoryEntry>(HistoryFile, ct);

    public async Task AppendHistoryAsync(ApiHistoryEntry entry, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var history = await ReadUnlockedAsync<ApiHistoryEntry>(HistoryFile, ct);
            history.Insert(0, entry);
            if (history.Count > MaxHistory) history.RemoveRange(MaxHistory, history.Count - MaxHistory);
            await WriteUnlockedAsync(HistoryFile, history, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Adds what an import produced to the saved lists. A collection or environment with the same name as a saved
    /// one replaces it (re-importing an updated export), so the lists do not fill with copies.
    /// </summary>
    public async Task SaveImportAsync(ApiImportResult import, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (import.Collections.Count > 0)
            {
                var collections = await ReadUnlockedAsync<ApiCollection>(CollectionsFile, ct);
                foreach (var c in import.Collections) Upsert(collections, c, x => x.Name, (old, @new) => @new.Id = old.Id);
                await WriteUnlockedAsync(CollectionsFile, collections, ct);
            }
            if (import.Environments.Count > 0)
            {
                var environments = await ReadUnlockedAsync<ApiEnvironment>(EnvironmentsFile, ct);
                foreach (var e in import.Environments) Upsert(environments, e, x => x.Name, (old, @new) => @new.Id = old.Id);
                await WriteUnlockedAsync(EnvironmentsFile, environments, ct);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void Upsert<T>(List<T> items, T item, Func<T, string> name, Action<T, T> keepIdentity)
    {
        var i = items.FindIndex(x => string.Equals(name(x), name(item), StringComparison.OrdinalIgnoreCase));
        if (i < 0)
        {
            items.Add(item);
            return;
        }
        keepIdentity(items[i], item);
        items[i] = item;
    }

    private async Task<List<T>> ReadAsync<T>(string file, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await ReadUnlockedAsync<T>(file, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task WriteAsync<T>(string file, List<T> items, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await WriteUnlockedAsync(file, items, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<List<T>> ReadUnlockedAsync<T>(string file, CancellationToken ct)
    {
        if (!File.Exists(file)) return [];
        try
        {
            await using var fs = File.OpenRead(file);
            return await JsonSerializer.DeserializeAsync<List<T>>(fs, Json, ct) ?? [];
        }
        catch (JsonException ex)
        {
            // Keep the damaged file: the next save would otherwise replace everything in it with an empty list.
            var backup = $"{file}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(file, backup, overwrite: true);
            throw new InvalidDataException($"{Path.GetFileName(file)} was damaged and has been moved to {backup}.", ex);
        }
    }

    private async Task WriteUnlockedAsync<T>(string file, List<T> items, CancellationToken ct)
    {
        Directory.CreateDirectory(paths.Root);
        var tmp = file + ".tmp";
        await using (var fs = File.Create(tmp))
            await JsonSerializer.SerializeAsync(fs, items, Json, ct);
        File.Move(tmp, file, overwrite: true);
    }
}
