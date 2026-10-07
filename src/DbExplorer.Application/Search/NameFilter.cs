namespace DbExplorer.Application.Search;

/// <summary>The rule the database pickers filter by: names that contain the typed text, ignoring case.</summary>
public static class NameFilter
{
    /// <summary><paramref name="names"/> that contain <paramref name="text"/> (trimmed), in their order; all of them when the text is blank.</summary>
    public static IReadOnlyList<string> Apply(IEnumerable<string> names, string? text)
    {
        var f = text?.Trim() ?? "";
        return f.Length == 0
            ? names.ToList()
            : names.Where(n => n.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
    }
}
