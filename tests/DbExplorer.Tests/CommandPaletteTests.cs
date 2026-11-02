using System.Text.RegularExpressions;
using DbExplorer.Application;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Tests;

public class CommandPaletteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbx-palette-" + Guid.NewGuid().ToString("N"));

    public CommandPaletteTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static PaletteItem Command(string title, string? shortcut = null, string? keywords = null, string? category = null) =>
        new(title, null, PaletteItemKind.Command, _ => { }) { Shortcut = shortcut, Keywords = keywords, Category = category };

    private static readonly IReadOnlyList<PaletteItem> Items =
    [
        Command("Run script or selection", "F5", "execute go", "Query"),
        Command("Format SQL", "Ctrl+Shift+F", "beautify pretty", "Query"),
        Command("Explain (execution plan)", "Ctrl+L", "plan", "Query"),
        Command("Refresh metadata", category: "Connection"),
        new("Objects", "tab", PaletteItemKind.Tab, _ => { }),
        new("dbo.Formats", "Table", PaletteItemKind.Object, _ => { }),
        new("dbo.Orders", "Table", PaletteItemKind.Object, _ => { })
    ];

    [Fact]
    public void Leading_angle_bracket_lists_commands_only()
    {
        var vm = new CommandPaletteViewModel(Items) { Query = ">" };
        Assert.True(vm.CommandsOnly);
        Assert.All(vm.Results, i => Assert.Equal(PaletteItemKind.Command, i.Kind));
        Assert.Equal(4, vm.Results.Count);

        vm.Query = "> form";
        Assert.Equal(["Format SQL"], vm.Results.Select(i => i.Title));

        vm.Query = "form";
        Assert.Contains(vm.Results, i => i.Title == "dbo.Formats");
        Assert.False(vm.CommandsOnly);
    }

    [Fact]
    public void Commands_only_mode_can_start_open()
    {
        var vm = new CommandPaletteViewModel(Items, commandsOnly: true);
        Assert.Equal(">", vm.Query);
        Assert.All(vm.Results, i => Assert.Equal(PaletteItemKind.Command, i.Kind));
    }

    [Fact]
    public void Recent_commands_come_first_in_recent_order_and_are_labelled()
    {
        var vm = new CommandPaletteViewModel(Items, ["Refresh metadata", "Format SQL", "gone command"]);

        Assert.Equal(["Refresh metadata", "Format SQL"], vm.Results.Take(2).Select(i => i.Title));
        Assert.All(vm.Results.Take(2), i => Assert.Equal("recent", i.KindLabel));
        Assert.Equal("query", vm.Results[2].KindLabel);
        // Each command shows once, then tabs, then objects.
        Assert.Equal(Items.Count, vm.Results.Count);
        Assert.Equal(PaletteItemKind.Object, vm.Results[^1].Kind);
    }

    [Fact]
    public void Keywords_and_shortcuts_find_commands()
    {
        var vm = new CommandPaletteViewModel(Items) { Query = "execute" };
        Assert.Equal("Run script or selection", vm.Selected?.Title);

        vm.Query = "ctrl+l";
        Assert.Equal("Explain (execution plan)", vm.Selected?.Title);

        vm.Query = "f5";
        Assert.Equal("Run script or selection", vm.Selected?.Title);

        // A single letter is a word query, not a key: it must not pull every Ctrl+ command up.
        Assert.Null(CommandPaletteViewModel.Score(Command("Zzz", "Ctrl+E"), "e"));
    }

    [Fact]
    public void Accepting_a_command_reports_it_for_the_recent_list_but_objects_do_not()
    {
        var accepted = new List<string>();
        var ran = new List<string>();
        var items = new List<PaletteItem>
        {
            new("Toggle", null, PaletteItemKind.Command, _ => ran.Add("Toggle")) { Id = "toggle-x" },
            new("dbo.T", "Table", PaletteItemKind.Object, _ => ran.Add("dbo.T"))
        };
        var vm = new CommandPaletteViewModel(items, accepted: i => accepted.Add(i.Id));

        vm.Selected = vm.Results[0];
        vm.Accept(PaletteModifier.None);
        vm.Selected = vm.Results[1];
        vm.Accept(PaletteModifier.None);

        Assert.Equal(["toggle-x"], accepted);
        Assert.Equal(["Toggle", "dbo.T"], ran);
    }

    [Fact]
    public void Recent_commands_are_remembered_most_recent_first_and_capped()
    {
        var settings = new AppSettingsService(new AppPaths(_root));
        Assert.Empty(settings.RecentCommands);

        for (var i = 0; i < AppSettingsService.MaxRecentCommands + 3; i++) settings.AddRecentCommand("c" + i);
        settings.AddRecentCommand("c5");

        var reloaded = new AppSettingsService(new AppPaths(_root));
        Assert.Equal(AppSettingsService.MaxRecentCommands, reloaded.RecentCommands.Count);
        Assert.Equal("c5", reloaded.RecentCommands[0]);
        Assert.Equal("c10", reloaded.RecentCommands[1]);
        Assert.Single(reloaded.RecentCommands, id => id == "c5");
    }

    // ----- The F1 sheet lists every shortcut the app has -----

    private static string Desktop => Path.Combine(MainTabOrderTests.RepoRoot(), "src", "DbExplorer.Desktop");

    /// <summary>Every single key combination named in the catalogue ("F8 / Shift+F8" is two).</summary>
    private static HashSet<string> CatalogKeys() =>
        ShortcutCatalog.All.SelectMany(s => s.Keys.Split(" / ", StringSplitOptions.TrimEntries)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Key_bindings_come_from_the_keymap_not_the_xaml()
    {
        // Fixed gestures in XAML could not be changed in the F1 sheet; views bind them with Keymap.Bind instead.
        var hardCoded = Directory.EnumerateFiles(Desktop, "*.axaml", SearchOption.AllDirectories)
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), "<KeyBinding Gesture=\"([^\"]+)\"").Select(m => $"{Path.GetFileName(f)}: {m.Groups[1].Value}"))
            .ToList();
        Assert.Empty(hardCoded);
    }

    [Fact]
    public void Menu_items_name_commands_that_exist()
    {
        var ids = Directory.EnumerateFiles(Desktop, "*.axaml", SearchOption.AllDirectories)
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), "Keymap.Command=\"([^\"]+)\"").Select(m => m.Groups[1].Value))
            .ToList();
        Assert.True(ids.Count > 20, $"only {ids.Count} menu items use the keymap");
        Assert.All(ids, id => Assert.NotNull(DbExplorer.Application.Shortcuts.ShortcutCommands.Get(id)));
    }

    [Fact]
    public void Menu_gestures_in_the_xaml_are_only_fixed_keys()
    {
        // A changeable command's key in a menu comes from Keymap.Command, so it follows a change in the F1 sheet.
        var changeable = DbExplorer.Application.Shortcuts.ShortcutCommands.All.SelectMany(c => c.Defaults).Select(c => c.ToString()).ToHashSet();
        // Ctrl+F is Find in the editor (fixed) and the quick filter in the grid (changeable).
        changeable.ExceptWith(DbExplorer.Application.Shortcuts.ShortcutCommands.Fixed.SelectMany(f => f.Keys.Split(" / ")));
        var stale = Directory.EnumerateFiles(Desktop, "*.axaml", SearchOption.AllDirectories)
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), "InputGesture=\"([^\"]+)\"").Select(m => (File: Path.GetFileName(f), Keys: m.Groups[1].Value)))
            .Where(x => changeable.Contains(x.Keys.Replace("PageUp", "PgUp").Replace("PageDown", "PgDn")))
            .Select(x => $"{x.File}: {x.Keys}")
            .ToList();
        Assert.Empty(stale);
    }

    [Fact]
    public void Every_shortcut_named_in_a_tooltip_is_in_the_shortcut_sheet()
    {
        var keys = CatalogKeys();
        var missing = new List<string>();
        var found = 0;
        foreach (var file in Directory.EnumerateFiles(Desktop, "*.axaml", SearchOption.AllDirectories))
        foreach (Match tip in Regex.Matches(File.ReadAllText(file), "ToolTip.Tip=\"([^\"]*)\""))
        foreach (Match key in Regex.Matches(tip.Groups[1].Value, @"\b(?:(?:Ctrl|Shift|Alt)\+)+(?:[A-Z0-9↑↓]|F\d+|Enter|Space)\b|(?<![\w+])F\d+\b|(?:(?:Ctrl|Shift|Alt)\+)+[↑↓]"))
        {
            found++;
            if (!keys.Contains(key.Value)) missing.Add($"{Path.GetFileName(file)}: {key.Value}");
        }
        Assert.Empty(missing);
        Assert.True(found > 20, $"only {found} shortcuts found in tooltips; is the pattern still right?");
    }

    [Fact]
    public void Shortcuts_shown_in_the_palette_are_in_the_shortcut_sheet()
    {
        var keys = CatalogKeys();
        var source = File.ReadAllText(Path.Combine(Desktop, "ViewModels", "MainWindowViewModel.Palette.cs"));
        var shown = Regex.Matches(source, "\"((?:Ctrl|Shift|Alt|F\\d)[^\"]*)\"").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.NotEmpty(shown);
        Assert.All(shown, s => Assert.Contains(s, keys));
    }

    [Fact]
    public void Shortcut_sheet_filter_matches_keys_and_text()
    {
        var byKeys = ShortcutCatalog.Grouped("ctrl+shift+f");
        Assert.Contains(byKeys.SelectMany(g => g.Items), s => s.Action.StartsWith("Format", StringComparison.Ordinal));

        var byText = ShortcutCatalog.Grouped("step");
        Assert.Equal([ShortcutCatalog.Debugger], byText.Select(g => g.Area));

        Assert.Empty(ShortcutCatalog.Grouped("no such shortcut"));
        Assert.Equal(ShortcutCatalog.All.Count, ShortcutCatalog.Grouped("").Sum(g => g.Items.Count));
    }

    [Fact]
    public void No_key_is_listed_twice_in_one_area()
    {
        var duplicates = ShortcutCatalog.All
            .SelectMany(s => s.Keys.Split(" / ", StringSplitOptions.TrimEntries).Select(k => (s.Area, Key: k)))
            .GroupBy(x => x).Where(g => g.Count() > 1).Select(g => $"{g.Key.Area}: {g.Key.Key}").ToList();
        Assert.Empty(duplicates);
    }
}
