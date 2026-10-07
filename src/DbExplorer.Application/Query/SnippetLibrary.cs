using System.Text.Json;

namespace DbExplorer.Application.Query;

/// <summary>A piece of SQL the user saved under a name, offered by every query tab.</summary>
public sealed class UserSnippet
{
    public string Name { get; set; } = "";
    public string Sql { get; set; } = "";
}

/// <summary>
/// The user's own snippets ("My snippets"), shared by all query tabs and kept in <c>snippets.json</c>. They show in the
/// editor's suggestions at the start of a statement and in the Snippets menu of the Query toolbar.
/// </summary>
public sealed class SnippetLibrary(AppPaths paths)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private List<UserSnippet>? _items;

    private string FilePath => Path.Combine(paths.Root, "snippets.json");

    /// <summary>Raised after a snippet was saved or deleted.</summary>
    public event Action? Changed;

    /// <summary>The snippets, sorted by name.</summary>
    public IReadOnlyList<UserSnippet> Items => _items ??= Load();

    /// <summary>Saves <paramref name="sql"/> as <paramref name="name"/>, replacing a snippet of the same name (case-insensitive).</summary>
    public void Save(string name, string sql)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("A snippet needs a name.", nameof(name));
        if (string.IsNullOrWhiteSpace(sql)) throw new ArgumentException("A snippet needs some SQL.", nameof(sql));

        var items = Items.Where(s => !string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        items.Add(new UserSnippet { Name = name, Sql = sql });
        Store(items);
    }

    /// <summary>Deletes the snippet called <paramref name="name"/>; false when there is none.</summary>
    public bool Remove(string name)
    {
        var items = Items.Where(s => !string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (items.Count == Items.Count) return false;
        Store(items);
        return true;
    }

    public bool Contains(string name) => Items.Any(s => string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    private void Store(List<UserSnippet> items)
    {
        _items = items.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Directory.CreateDirectory(paths.Root);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_items, Json));
        File.Move(tmp, FilePath, overwrite: true);
        Changed?.Invoke();
    }

    private List<UserSnippet> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            var stored = JsonSerializer.Deserialize<List<UserSnippet>>(File.ReadAllText(FilePath), Json) ?? [];
            return stored.Where(s => !string.IsNullOrWhiteSpace(s.Name) && !string.IsNullOrWhiteSpace(s.Sql))
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch
        {
            // An unreadable file starts an empty library rather than breaking the Query tab.
            return [];
        }
    }
}
