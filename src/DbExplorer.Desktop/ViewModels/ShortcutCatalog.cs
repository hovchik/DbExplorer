using DbExplorer.Application.Shortcuts;
using DbExplorer.Desktop.Services;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>One keyboard shortcut: where it works, the keys, and what it does. <see cref="Id"/> is set when the keys can
/// be changed in the F1 sheet.</summary>
public sealed record ShortcutInfo(string Area, string Keys, string Action)
{
    public string? Id { get; init; }

    /// <summary>The keys differ from the defaults.</summary>
    public bool IsCustomized { get; init; }

    public bool IsEditable => Id is not null;

    /// <summary>The default keys, shown next to a changed shortcut.</summary>
    public string? DefaultKeys { get; init; }

    public bool HasNoKeys => Keys.Length == 0;
}

/// <summary>Every keyboard shortcut in the app, in the order the F1 sheet shows them: the changeable commands of
/// <see cref="ShortcutCommands"/> with their current keys, then the fixed keys of each area. A test checks that every
/// KeyBinding and every "(Ctrl+…)" tooltip in the XAML is listed here.</summary>
public static class ShortcutCatalog
{
    public const string Anywhere = ShortcutAreas.Anywhere;
    public const string QueryEditor = ShortcutAreas.QueryEditor;
    public const string QueryTabs = ShortcutAreas.QueryTabs;
    public const string ResultGrid = ShortcutAreas.ResultGrid;
    public const string Navigator = ShortcutAreas.Navigator;
    public const string Comparer = ShortcutAreas.Comparer;
    public const string ErModel = ShortcutAreas.ErModel;
    public const string TableDesigner = ShortcutAreas.TableDesigner;
    public const string QueryBuilder = ShortcutAreas.QueryBuilder;
    public const string Debugger = ShortcutAreas.Debugger;
    public const string Palette = ShortcutAreas.Palette;
    public const string Vim = ShortcutAreas.Vim;

    private static readonly string[] AreaOrder = [Anywhere, Palette, QueryEditor, QueryTabs, ResultGrid, Navigator, Comparer, QueryBuilder, TableDesigner, ErModel, Debugger, Vim];

    /// <summary>Every shortcut with the keys in use now (the Vim card only while Vim mode is on).</summary>
    public static IReadOnlyList<ShortcutInfo> All => Build(Keymap.Current);

    public static IReadOnlyList<ShortcutInfo> Build(KeymapService keymap)
    {
        var list = new List<ShortcutInfo>();
        foreach (var area in AreaOrder)
        {
            foreach (var command in ShortcutCommands.All.Where(c => c.Area == area))
            {
                list.Add(new ShortcutInfo(area, keymap.Display(command.Id) ?? "", command.Action)
                {
                    Id = command.Id,
                    IsCustomized = keymap.IsCustomized(command.Id),
                    DefaultKeys = string.Join(" / ", command.Defaults)
                });
            }
            var fixedKeys = area == Vim ? keymap.VimMode ? ShortcutCommands.Vim : [] : ShortcutCommands.Fixed.Where(f => f.Area == area);
            list.AddRange(fixedKeys.Select(f => new ShortcutInfo(f.Area, f.Keys, f.Action)));
        }
        return list;
    }

    /// <summary>The keys of a changeable command as the palette shows them next to it.</summary>
    public static string? KeysFor(string id) => Keymap.Display(id);

    /// <summary>Areas in sheet order, each with the shortcuts that match <paramref name="filter"/> (keys or text).</summary>
    public static IReadOnlyList<ShortcutGroup> Grouped(string? filter) => Grouped(All, filter);

    public static IReadOnlyList<ShortcutGroup> Grouped(IReadOnlyList<ShortcutInfo> all, string? filter)
    {
        var terms = (filter ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return all
            .Where(s => terms.All(t => s.Keys.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                                       s.Action.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                                       s.Area.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(s => s.Area)
            .Select(g => new ShortcutGroup(g.Key, g.ToList()))
            .ToList();
    }
}

public sealed record ShortcutGroup(string Area, IReadOnlyList<ShortcutInfo> Items);
