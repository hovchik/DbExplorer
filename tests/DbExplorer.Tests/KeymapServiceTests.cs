using DbExplorer.Application;
using DbExplorer.Application.Shortcuts;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Tests;

public class KeymapServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbx-keymap-" + Guid.NewGuid().ToString("N"));

    public KeymapServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private KeymapService Open() => new(new AppPaths(_root));

    private static KeyChord K(string text) => KeyChord.Parse(text);

    [Theory]
    [InlineData("Ctrl+Shift+F", "Ctrl+Shift+F")]
    [InlineData("shift+ctrl+f", "Ctrl+Shift+F")]
    [InlineData("Ctrl+Alt+Shift+J", "Ctrl+Alt+Shift+J")]
    [InlineData("alt+up", "Alt+↑")]
    [InlineData("Ctrl+PageDown", "Ctrl+PgDn")]
    [InlineData("Ctrl+OemQuestion", "Ctrl+/")]
    [InlineData("Ctrl++", "Ctrl+=")]
    [InlineData("Ctrl+-", "Ctrl+-")]
    [InlineData("Cmd+K", "Ctrl+K")]
    [InlineData("f12", "F12")]
    [InlineData("Escape", "Esc")]
    [InlineData("Ctrl+D0", "Ctrl+0")]
    public void Chords_parse_and_print_the_way_the_sheet_shows_them(string text, string expected) =>
        Assert.Equal(expected, K(text).ToString());

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Hyper+K")]
    [InlineData("Ctrl+Banana")]
    [InlineData("Middle-click")]
    public void Not_a_key_does_not_parse(string text) => Assert.False(KeyChord.TryParse(text, out _));

    [Fact]
    public void Every_default_key_parses_and_no_area_uses_a_key_twice()
    {
        var keymap = new KeymapService();
        foreach (var command in ShortcutCommands.All)
        {
            Assert.NotEmpty(command.Defaults);
            foreach (var chord in command.Defaults)
                Assert.True(keymap.Conflicts(command.Id, chord).Count == 0, $"{command.Id}: {chord} is also {string.Join(", ", keymap.Conflicts(command.Id, chord).Select(c => c.Id))}");
        }
        Assert.Equal(ShortcutCommands.All.Count, ShortcutCommands.All.Select(c => c.Id).Distinct().Count());
    }

    [Fact]
    public void Defaults_do_not_take_a_fixed_key()
    {
        var keymap = new KeymapService();
        foreach (var command in ShortcutCommands.All)
        foreach (var chord in command.Defaults)
        {
            var problem = keymap.Check(command.Id, chord);
            // Esc to cancel a comparison is a default that a user could not pick themselves (Esc alone is reserved).
            if (command.Id == ShortcutCommands.CancelComparison) continue;
            Assert.True(problem is null, $"{command.Id}: {problem}");
        }
    }

    [Fact]
    public void A_new_key_is_found_saved_and_read_back()
    {
        var keymap = Open();
        var changes = 0;
        keymap.Changed += () => changes++;
        keymap.Assign(ShortcutCommands.Format, K("Ctrl+Alt+L"));

        Assert.Equal(ShortcutCommands.Format, keymap.Find(ShortcutAreas.QueryEditor, K("Ctrl+Alt+L")));
        Assert.Null(keymap.Find(ShortcutAreas.QueryEditor, K("Ctrl+Shift+F")));
        Assert.True(keymap.IsCustomized(ShortcutCommands.Format));
        Assert.Equal(1, changes);

        var again = Open();
        Assert.Equal("Ctrl+Alt+L", again.Display(ShortcutCommands.Format));
        Assert.False(again.IsCustomized(ShortcutCommands.RunScript));
        Assert.Equal("F5 / Ctrl+E", again.Display(ShortcutCommands.RunScript));
    }

    [Fact]
    public void Adding_keeps_the_existing_keys()
    {
        var keymap = Open();
        keymap.Assign(ShortcutCommands.RunStatement, K("Ctrl+Shift+Enter"), keepExisting: true);
        Assert.Equal("Ctrl+Enter / Ctrl+Shift+Enter", keymap.Display(ShortcutCommands.RunStatement));
        keymap.Remove(ShortcutCommands.RunStatement, K("Ctrl+Enter"));
        Assert.Equal("Ctrl+Shift+Enter", Open().Display(ShortcutCommands.RunStatement));
    }

    [Fact]
    public void A_taken_key_moves_from_the_other_command()
    {
        var keymap = Open();
        // Ctrl+D duplicates the line; giving it to Format takes it away from Duplicate.
        Assert.Equal([ShortcutCommands.Duplicate], keymap.Conflicts(ShortcutCommands.Format, K("Ctrl+D")).Select(c => c.Id));
        keymap.Assign(ShortcutCommands.Format, K("Ctrl+D"));
        Assert.Equal(ShortcutCommands.Format, keymap.Find(ShortcutAreas.QueryEditor, K("Ctrl+D")));
        Assert.Null(keymap.Display(ShortcutCommands.Duplicate));
        Assert.True(keymap.IsCustomized(ShortcutCommands.Duplicate));

        // A key used anywhere conflicts with the editor; a key of the Comparer does not.
        Assert.Equal([ShortcutCommands.Palette], keymap.Conflicts(ShortcutCommands.Format, K("Ctrl+K")).Select(c => c.Id));
        Assert.DoesNotContain(keymap.Conflicts(ShortcutCommands.NextWarning, K("Shift+F8")), c => c.Area == ShortcutAreas.Comparer);
        // The tab strip is live while the editor has focus.
        Assert.Equal([ShortcutCommands.NewTab], keymap.Conflicts(ShortcutCommands.Format, K("Ctrl+N")).Select(c => c.Id));
    }

    [Fact]
    public void Typing_keys_and_fixed_keys_are_refused()
    {
        var keymap = Open();
        Assert.NotNull(keymap.Check(ShortcutCommands.Format, K("F")));
        Assert.NotNull(keymap.Check(ShortcutCommands.Format, K("Shift+F")));
        Assert.NotNull(keymap.Check(ShortcutCommands.Format, K("Enter")));
        Assert.NotNull(keymap.Check(ShortcutCommands.Format, K("Esc")));
        Assert.Contains("Find", keymap.Check(ShortcutCommands.Format, K("Ctrl+F")));
        Assert.Contains("can't be changed", keymap.Check(ShortcutCommands.NewTab, K("Ctrl+Z")));
        Assert.Null(keymap.Check(ShortcutCommands.Format, K("Alt+F")));
        Assert.Null(keymap.Check(ShortcutCommands.Format, K("F9")));
        Assert.Null(keymap.Check(ShortcutCommands.Format, K("Ctrl+Alt+Shift+F")));
        Assert.Throws<ArgumentException>(() => keymap.Assign(ShortcutCommands.Format, K("Ctrl+F")));
    }

    [Fact]
    public void Reset_brings_the_defaults_back()
    {
        var keymap = Open();
        keymap.Assign(ShortcutCommands.Format, K("Ctrl+D"));
        keymap.Reset(ShortcutCommands.Format);
        Assert.Equal("Ctrl+Shift+F", keymap.Display(ShortcutCommands.Format));
        Assert.Null(keymap.Display(ShortcutCommands.Duplicate));
        keymap.ResetAll();
        Assert.Equal("Ctrl+D", keymap.Display(ShortcutCommands.Duplicate));
        Assert.False(keymap.HasCustomizations);
        Assert.False(Open().HasCustomizations);
    }

    [Fact]
    public void Assigning_the_default_back_is_not_a_change()
    {
        var keymap = Open();
        keymap.Assign(ShortcutCommands.Format, K("Ctrl+Alt+L"));
        keymap.Assign(ShortcutCommands.Format, K("Ctrl+Shift+F"));
        Assert.False(keymap.IsCustomized(ShortcutCommands.Format));
    }

    [Fact]
    public void No_keys_is_remembered()
    {
        var keymap = Open();
        keymap.Clear(ShortcutCommands.Duplicate);
        Assert.Null(Open().Display(ShortcutCommands.Duplicate));
        Assert.Null(Open().Find(ShortcutAreas.QueryEditor, K("Ctrl+D")));
    }

    [Fact]
    public void Vim_mode_is_saved()
    {
        Assert.False(Open().VimMode);
        Open().SetVimMode(true);
        Assert.True(Open().VimMode);
    }

    [Fact]
    public void A_damaged_or_odd_file_falls_back_to_defaults()
    {
        File.WriteAllText(Path.Combine(_root, "keymap.json"), "{ not json");
        Assert.Equal("Ctrl+Shift+F", Open().Display(ShortcutCommands.Format));

        File.WriteAllText(Path.Combine(_root, "keymap.json"),
            """{ "VimMode": true, "Keys": { "editor.format": ["Ctrl+Alt+L", "nonsense", "F"], "no.such.command": ["Ctrl+Q"] } }""");
        var keymap = Open();
        Assert.True(keymap.VimMode);
        Assert.Equal("Ctrl+Alt+L", keymap.Display(ShortcutCommands.Format));
    }

    // ----- The F1 sheet -----

    [Fact]
    public void Sheet_shows_current_keys_and_marks_changed_ones()
    {
        var keymap = Open();
        keymap.Assign(ShortcutCommands.Format, K("Ctrl+Alt+L"));
        var all = ShortcutCatalog.Build(keymap);
        var format = all.Single(s => s.Id == ShortcutCommands.Format);
        Assert.Equal("Ctrl+Alt+L", format.Keys);
        Assert.True(format.IsCustomized);
        Assert.Equal("Ctrl+Shift+F", format.DefaultKeys);
        Assert.False(all.Single(s => s.Id == ShortcutCommands.RunScript).IsCustomized);
        Assert.Contains(all, s => s is { Id: null, Keys: "Ctrl+F" });
    }

    [Fact]
    public void Sheet_lists_the_vim_keys_only_in_vim_mode()
    {
        var keymap = Open();
        Assert.DoesNotContain(ShortcutCatalog.Build(keymap), s => s.Area == ShortcutAreas.Vim);
        keymap.SetVimMode(true);
        Assert.Contains(ShortcutCatalog.Build(keymap), s => s.Area == ShortcutAreas.Vim && s.Keys.StartsWith("d c y", StringComparison.Ordinal));
    }

    [Fact]
    public void Sheet_records_a_key_warns_about_the_conflict_and_assigns_it()
    {
        var keymap = Open();
        var sheet = new ShortcutSheetViewModel(keymap);
        var format = sheet.Groups.SelectMany(g => g.Items).Single(s => s.Id == ShortcutCommands.Format);

        sheet.StartEditCommand.Execute(format);
        Assert.True(sheet.IsEditing);
        Assert.False(sheet.CanAssign);

        sheet.Record(K("Ctrl+F"));
        Assert.False(sheet.CanAssign);
        Assert.True(sheet.EditMessageIsError);

        sheet.Record(K("Ctrl+D"));
        Assert.True(sheet.CanAssign);
        Assert.Contains("Duplicate", sheet.EditMessage);

        sheet.AssignCommand.Execute(null);
        Assert.False(sheet.IsEditing);
        Assert.Equal("Ctrl+D", keymap.Display(ShortcutCommands.Format));
        Assert.True(sheet.HasCustomizations);
        // The sheet follows the change.
        Assert.Equal("Ctrl+D", sheet.Groups.SelectMany(g => g.Items).Single(s => s.Id == ShortcutCommands.Format).Keys);

        sheet.ResetAllCommand.Execute(null);
        Assert.Equal("Ctrl+Shift+F", keymap.Display(ShortcutCommands.Format));
        Assert.False(sheet.HasCustomizations);
        sheet.Detach();
    }

    [Fact]
    public void Sheet_switches_vim_mode_and_adds_keys()
    {
        var keymap = Open();
        var sheet = new ShortcutSheetViewModel(keymap);
        sheet.VimMode = true;
        Assert.True(keymap.VimMode);
        Assert.Contains(sheet.Groups, g => g.Area == ShortcutAreas.Vim);

        var run = sheet.Groups.SelectMany(g => g.Items).Single(s => s.Id == ShortcutCommands.RunScript);
        sheet.StartEditCommand.Execute(run);
        sheet.Record(K("Ctrl+R"));
        sheet.AddKeyCommand.Execute(null);
        Assert.Equal("F5 / Ctrl+E / Ctrl+R", keymap.Display(ShortcutCommands.RunScript));

        sheet.StartEditCommand.Execute(sheet.Groups.SelectMany(g => g.Items).Single(s => s.Id == ShortcutCommands.RunScript));
        sheet.RemoveKeysCommand.Execute(null);
        Assert.Null(keymap.Display(ShortcutCommands.RunScript));
        Assert.True(sheet.Groups.SelectMany(g => g.Items).Single(s => s.Id == ShortcutCommands.RunScript).HasNoKeys);
        sheet.Detach();
    }
}
