using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.Desktop.Services;

namespace DbExplorer.Tests;

[Collection(nameof(HeadlessUiCollection))]
public class AccessibleNamesTests
{
    [Theory]
    [InlineData("Close (Ctrl+W)", "Close")]
    [InlineData("Swap sides: the right becomes the baseline", "Swap sides")]
    [InlineData("Pin: keep this result when the query runs again", "Pin")]
    [InlineData("Editor zoom · click to reset (Ctrl+0)", "Editor zoom")]
    [InlineData("New connection…", "New connection…")]
    [InlineData("Hide the navigator (Ctrl+B or the sidebar button at the top left shows it again)", "Hide the navigator")]
    [InlineData("Replace the tab's text with this version. Ctrl+Z in the editor undoes it.", "Replace the tab's text with this version")]
    [InlineData("✕", null)]
    [InlineData("", null)]
    public void Short_tip_is_the_first_phrase(string tip, string? expected) =>
        Assert.Equal(expected, AccessibleNames.ShortTip(tip));

    [Fact]
    public void Very_long_first_phrase_is_cut_at_a_word()
    {
        var name = AccessibleNames.ShortTip(string.Join(' ', Enumerable.Repeat("column", 30)))!;
        Assert.True(name.Length <= 81);
        Assert.EndsWith("column…", name);
    }

    [Theory]
    [InlineData("Close (Ctrl+W)", "Ctrl+W")]
    [InlineData("Next change (F8)", "F8")]
    [InlineData("Previous change (Shift+F8)", "Shift+F8")]
    [InlineData("Close tab (Ctrl+F4)", "Ctrl+F4")]
    [InlineData("Give the results most of the space (Ctrl+Shift+↑)", "Ctrl+Shift+↑")]
    [InlineData("Zoom in (Ctrl+wheel)", null)]
    [InlineData("Collapse all", null)]
    public void Shortcut_is_read_from_the_tip(string tip, string? expected) =>
        Assert.Equal(expected, AccessibleNames.Shortcut(tip));

    [Theory]
    [InlineData("Connect", "Connect to the selected database", "Connect")]
    [InlineData("−", "Zoom out", "Zoom out")]
    [InlineData("75%", "Reset zoom", "Reset zoom, 75%")]
    [InlineData(null, "Collapse all", "Collapse all")]
    [InlineData("👁", null, null)]
    public void Button_name_prefers_its_words_then_its_tip(string? caption, string? tip, string? expected) =>
        Assert.Equal(expected, AccessibleNames.NameFor(caption, tip));

    private static (Window Window, T Control) Show<T>(T control, Control? root = null) where T : Control
    {
        var window = new Window { Width = 600, Height = 400, Content = root ?? control };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, control);
    }

    private static string? Name(Control c) => ControlAutomationPeer.CreatePeerForElement(c).GetName();

    [AvaloniaFact]
    public void Icon_button_is_named_from_its_tooltip()
    {
        var button = new Button { Content = "＋" };
        ToolTip.SetTip(button, "Zoom in (Ctrl+=)");
        var (window, _) = Show(button);

        Assert.Equal("Zoom in", Name(button));
        Assert.Equal("Zoom in (Ctrl+=)", ControlAutomationPeer.CreatePeerForElement(button).GetHelpText());
        Assert.Equal("Ctrl+=", AutomationProperties.GetAcceleratorKey(button));
        window.Close();
    }

    [AvaloniaFact]
    public void Name_follows_a_bound_tooltip()
    {
        var button = new Button { Content = "✕" };
        ToolTip.SetTip(button, "Delete connection…");
        var (window, _) = Show(button);
        ToolTip.SetTip(button, "The active connection can't be deleted");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("The active connection can't be deleted", Name(button));
        window.Close();
    }

    [AvaloniaFact]
    public void Icon_and_caption_button_reads_the_caption()
    {
        var button = new Button { Content = new StackPanel { Children = { new Border { Width = 8 }, new TextBlock { Text = "Connect" } } } };
        ToolTip.SetTip(button, "Connect to the selected database");
        var (window, _) = Show(button);

        Assert.Equal("Connect", Name(button));
        ((TextBlock)((StackPanel)button.Content!).Children[1]).Text = "Disconnect";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Disconnect", Name(button));
        window.Close();
    }

    [AvaloniaFact]
    public void Text_button_keeps_its_own_text()
    {
        var button = new Button { Content = "Run" };
        ToolTip.SetTip(button, "Run the script (F5)");
        var (window, _) = Show(button);

        Assert.Equal("Run", Name(button));
        Assert.Null(AutomationProperties.GetName(button));
        Assert.Equal("F5", AutomationProperties.GetAcceleratorKey(button));
        window.Close();
    }

    [AvaloniaFact]
    public void Name_set_in_xaml_wins()
    {
        var button = new Button { Content = "✕" };
        AutomationProperties.SetName(button, "Close the script");
        ToolTip.SetTip(button, "Close");
        var (window, _) = Show(button);

        Assert.Equal("Close the script", Name(button));
        window.Close();
    }

    [AvaloniaFact]
    public void Name_bound_in_xaml_is_never_overwritten()
    {
        var button = new Button { Content = new Grid() };
        button.Bind(AutomationProperties.NameProperty, new Avalonia.Data.Binding("Text") { Source = new StatusSource() });
        ToolTip.SetTip(button, "Connecting…");
        var (window, _) = Show(button);
        ToolTip.SetTip(button, "Connected to Shop");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Ready", Name(button));
        window.Close();
    }

    private sealed class StatusSource { public string Text => "Ready"; }

    [AvaloniaFact]
    public void Check_box_in_a_grid_is_named_after_its_column()
    {
        var grid = new DataGrid { ItemsSource = new[] { new GridItem(true) }, AutoGenerateColumns = false };
        grid.Columns.Add(new DataGridCheckBoxColumn { Header = "PK", Binding = new Avalonia.Data.Binding(nameof(GridItem.IsKey)) });
        var (window, _) = Show(grid);

        var box = window.GetVisualDescendants().OfType<CheckBox>().Single();
        Assert.Equal("PK", Name(box));
        window.Close();
    }

    public sealed record GridItem(bool IsKey);

    [AvaloniaFact]
    public void Input_is_labelled_by_the_caption_to_its_left()
    {
        var host = new TextBox { Watermark = "localhost" };
        Grid.SetColumn(host, 1);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(new TextBlock { Text = "Host:" });
        grid.Children.Add(host);
        var (window, _) = Show(host, grid);

        Assert.Equal("Host:", Name(host)!.Trim());
        Assert.IsType<TextBlock>(AutomationProperties.GetLabeledBy(host));
        window.Close();
    }

    [AvaloniaFact]
    public void Input_follows_a_caption_bound_after_load()
    {
        var caption = new TextBlock();
        var source = new CaptionSource();
        caption.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(CaptionSource.Label)) { Source = source });
        var user = new TextBox();
        var (window, _) = Show(user, new StackPanel { Children = { caption, user } });

        Assert.Same(caption, AutomationProperties.GetLabeledBy(user));
        caption.Text = "Login";
        Assert.Equal("Login", Name(user));
        window.Close();
    }

    private sealed class CaptionSource { public string Label => ""; }

    [AvaloniaFact]
    public void Input_is_labelled_by_the_caption_above_it()
    {
        var box = new ComboBox();
        Grid.SetRow(box, 1);
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto") };
        grid.Children.Add(new TextBlock { Text = "Encoding" });
        grid.Children.Add(box);
        var (window, _) = Show(box, grid);

        Assert.Equal("Encoding", Name(box));
        window.Close();
    }

    [AvaloniaFact]
    public void Input_without_a_caption_uses_its_watermark()
    {
        var filter = new TextBox { Watermark = "Filter tables…" };
        var (window, _) = Show(filter, new StackPanel { Children = { new Button { Content = "x" }, filter } });

        Assert.Equal("Filter tables", Name(filter));
        window.Close();
    }

    /// <summary>
    /// Every view and dialog that can be opened without data: each visible button and input must have a name with
    /// words in it, so a screen reader never says only "button" or "edit". Views needing data are covered by the
    /// render harness's accessibility report.
    /// </summary>
    [AvaloniaFact]
    public void Every_control_in_the_views_has_a_name()
    {
        var types = typeof(AccessibleNames).Assembly.GetTypes()
            .Where(t => t.Namespace == "DbExplorer.Desktop.Views" && !t.IsAbstract && typeof(Control).IsAssignableFrom(t)
                        && t.GetConstructor(Type.EmptyTypes) is not null)
            .OrderBy(t => t.Name)
            .ToList();
        Assert.NotEmpty(types);

        var missing = new List<string>();
        var checkedViews = 0;
        foreach (var type in types)
        {
            Control view;
            try { view = (Control)Activator.CreateInstance(type)!; }
            catch { continue; } // needs services or data; the render harness covers it
            var window = view as Window ?? new Window { Width = 1400, Height = 900, Content = view };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                checkedViews++;
                foreach (var c in window.GetVisualDescendants().OfType<Control>().Where(AccessibleNames.IsLabelled))
                {
                    if (!c.IsEffectivelyVisible) continue;
                    if (c.TemplatedParent is not null) continue; // parts of a control template, e.g. a combo box's toggle
                    // Content bound to a view model that isn't there: the render harness checks these with real data.
                    if (c is ContentControl { Content: null } && ToolTip.GetTip(c) is null) continue;
                    // Labelled by a caption bound to the missing view model: it has words once the data is there.
                    if (AutomationProperties.GetLabeledBy(c) is TextBlock { Text: null or "" }) continue;
                    var name = Name(c);
                    if (!AccessibleNames.HasWords(name))
                        missing.Add($"{type.Name}: {c.GetType().Name} '{(c as ContentControl)?.Content}' name='{name}' x:Name='{c.Name}' watermark='{(c as TextBox)?.Watermark}' in {c.Parent?.GetType().Name} after '{(c.Parent as Panel)?.Children.TakeWhile(x => x != c).OfType<TextBlock>().LastOrDefault()?.Text}'");
                }
            }
            catch
            {
                // a view that can't be shown without its view model
            }
            finally
            {
                window.Close();
            }
        }

        Assert.True(checkedViews > 10, $"only {checkedViews} views could be shown");
        Assert.True(missing.Count == 0, "Controls without an accessible name:\n" + string.Join("\n", missing));
    }
}
