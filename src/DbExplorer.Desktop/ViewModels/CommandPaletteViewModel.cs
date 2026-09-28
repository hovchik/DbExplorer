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
    public string KindLabel => Kind switch
    {
        PaletteItemKind.Command => "command",
        PaletteItemKind.Tab => "go to",
        _ => Subtitle?.Split(' ')[0].ToLowerInvariant() ?? "object"
    };
}

/// <summary>Fuzzy "quick open" over commands, tabs and every object in the metadata cache.</summary>
public partial class CommandPaletteViewModel : ViewModelBase
{
    private const int MaxResults = 60;
    private readonly IReadOnlyList<PaletteItem> _all;

    public CommandPaletteViewModel(IReadOnlyList<PaletteItem> items)
    {
        _all = items;
        Filter();
    }

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private IReadOnlyList<PaletteItem> _results = [];
    [ObservableProperty] private PaletteItem? _selected;

    public event Action? CloseRequested;

    partial void OnQueryChanged(string value) => Filter();

    private void Filter()
    {
        var q = Query.Trim();
        Results = q.Length == 0
            ? _all.Where(i => i.Kind != PaletteItemKind.Object).Concat(_all.Where(i => i.Kind == PaletteItemKind.Object)).Take(MaxResults).ToList()
            : _all
                .Select(i => (Item: i, Score: FuzzyMatcher.Score(i.Title, q)))
                .Where(x => x.Score is not null)
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Item.Kind)
                .ThenBy(x => x.Item.Title.Length)
                .Take(MaxResults)
                .Select(x => x.Item)
                .ToList();
        Selected = Results.FirstOrDefault();
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
        item.Invoke(modifier);
    }
}
