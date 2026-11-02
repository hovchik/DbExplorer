using CommunityToolkit.Mvvm.ComponentModel;
using DbExplorer.Application.Search;

namespace DbExplorer.Desktop.ViewModels;

public enum PaletteItemKind
{
    Command,
    Tab,
    Object
}

/// <summary>How the item was picked: Enter, Shift+Enter or Ctrl+Enter.</summary>
public enum PaletteModifier
{
    None,
    Shift,
    Control
}

public sealed record PaletteItem(string Title, string? Subtitle, PaletteItemKind Kind, Action<PaletteModifier> Invoke)
{
    /// <summary>Keys that run the same thing without the palette, shown on the right.</summary>
    public string? Shortcut { get; init; }

    /// <summary>Extra words the filter also matches ("execute" for Run, "dark" for the theme…).</summary>
    public string? Keywords { get; init; }

    /// <summary>Remembered in the recent list; the title unless the title changes with state.</summary>
    public string Id { get; init; } = Title;

    /// <summary>The group shown on the left ("Query", "Connection", "View"…); commands only.</summary>
    public string? Category { get; init; }

    public bool IsRecent { get; init; }

    public string KindLabel => IsRecent ? "recent" : Kind switch
    {
        PaletteItemKind.Command => Category?.ToLowerInvariant() ?? "command",
        PaletteItemKind.Tab => "go to",
        _ => Subtitle?.Split(' ')[0].ToLowerInvariant() ?? "object"
    };

    public bool HasShortcut => !string.IsNullOrEmpty(Shortcut);
}

/// <summary>Fuzzy "quick open" over commands, tabs and every object in the metadata cache. A leading "&gt;" lists commands
/// only; commands used recently come first.</summary>
public partial class CommandPaletteViewModel : ViewModelBase
{
    public const string CommandPrefix = ">";
    private const int MaxResults = 60;
    private readonly IReadOnlyList<PaletteItem> _all;
    private readonly IReadOnlyList<string> _recent;
    private readonly Action<PaletteItem>? _accepted;

    public CommandPaletteViewModel(IReadOnlyList<PaletteItem> items, IReadOnlyList<string>? recentIds = null,
        Action<PaletteItem>? accepted = null, bool commandsOnly = false)
    {
        _all = items;
        _recent = recentIds ?? [];
        _accepted = accepted;
        _query = commandsOnly ? CommandPrefix : "";
        Filter();
    }

    [ObservableProperty] private string _query;
    [ObservableProperty] private IReadOnlyList<PaletteItem> _results = [];
    [ObservableProperty] private PaletteItem? _selected;
    [ObservableProperty] private bool _commandsOnly;

    public event Action? CloseRequested;

    /// <summary>F1 in the palette closes it and opens the shortcut sheet.</summary>
    public Action? ShowShortcuts { get; init; }

    public string Watermark => "Type a table, view, procedure, tab or command… (> for commands only)";

    partial void OnQueryChanged(string value) => Filter();

    private void Filter()
    {
        var text = Query.TrimStart();
        CommandsOnly = text.StartsWith(CommandPrefix, StringComparison.Ordinal);
        var q = (CommandsOnly ? text[CommandPrefix.Length..] : text).Trim();
        var pool = CommandsOnly ? _all.Where(i => i.Kind == PaletteItemKind.Command) : _all;

        // Recent position: 0 = most recent; items never used rank after every recent one.
        int RecentRank(PaletteItem item)
        {
            if (item.Kind != PaletteItemKind.Command) return int.MaxValue;
            var index = IndexOf(_recent, item.Id);
            return index < 0 ? int.MaxValue : index;
        }

        if (q.Length == 0)
        {
            var recent = pool.Where(i => RecentRank(i) != int.MaxValue).OrderBy(RecentRank).Select(i => i with { IsRecent = true });
            var rest = pool.Where(i => RecentRank(i) == int.MaxValue);
            Results = recent
                .Concat(rest.Where(i => i.Kind == PaletteItemKind.Command))
                .Concat(rest.Where(i => i.Kind == PaletteItemKind.Tab))
                .Concat(rest.Where(i => i.Kind == PaletteItemKind.Object))
                .Take(MaxResults)
                .ToList();
        }
        else
        {
            Results = pool
                .Select(i => (Item: i, Score: Score(i, q)))
                .Where(x => x.Score is not null)
                .OrderByDescending(x => x.Score)
                .ThenBy(x => RecentRank(x.Item))
                .ThenBy(x => x.Item.Kind)
                .ThenBy(x => x.Item.Title.Length)
                .Take(MaxResults)
                .Select(x => x.Item)
                .ToList();
        }
        Selected = Results.FirstOrDefault();
    }

    /// <summary>The title counts most; category, keywords and shortcut match too, a little lower, so "explain" or
    /// "ctrl+l" finds the Explain command.</summary>
    public static int? Score(PaletteItem item, string query)
    {
        var best = FuzzyMatcher.Score(item.Title, query);
        if (item.Kind != PaletteItemKind.Command) return best;

        void Consider(string? text, int penalty)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (FuzzyMatcher.Score(text, query) is int s && (best is null || s - penalty > best)) best = s - penalty;
        }

        Consider(item.Category is null ? null : item.Category + " " + item.Title, 5);
        Consider(item.Keywords, 20);
        if (LooksLikeKeys(query) && item.Shortcut is { } shortcut && shortcut.Contains(query.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
            best = Math.Max(best ?? int.MinValue, 40 + query.Length);
        return best;
    }

    /// <summary>"ctrl+l", "shift+f8", "f5": a query that names keys rather than words.</summary>
    private static bool LooksLikeKeys(string query) =>
        query.Contains('+') ||
        (query.Length is 2 or 3 && query[0] is 'f' or 'F' && query[1..].All(char.IsDigit));

    private static int IndexOf(IReadOnlyList<string> list, string id)
    {
        for (var i = 0; i < list.Count; i++)
            if (string.Equals(list[i], id, StringComparison.Ordinal)) return i;
        return -1;
    }

    public void Move(int delta)
    {
        if (Results.Count == 0) return;
        var index = Selected is null ? 0 : Results.ToList().IndexOf(Selected);
        Selected = Results[Math.Clamp(index + delta, 0, Results.Count - 1)];
    }

    public void Accept(PaletteModifier modifier)
    {
        if (Selected is not { } item) return;
        CloseRequested?.Invoke();
        if (item.Kind == PaletteItemKind.Command) _accepted?.Invoke(item);
        item.Invoke(modifier);
    }
}
