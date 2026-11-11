namespace DbExplorer.Tests;

/// <summary>Preferences has its own toolbar button (gear icon) rather than an item at the bottom of the theme menu.</summary>
public class PreferencesEntryPointTests
{
    private static string Xaml() =>
        File.ReadAllText(Path.Combine(MainTabOrderTests.RepoRoot(), "src", "DbExplorer.Desktop", "Views", "MainWindow.axaml"));

    [Fact]
    public void Toolbar_has_a_preferences_button_that_opens_the_window()
    {
        var xaml = Xaml();
        var start = xaml.IndexOf("x:Name=\"PreferencesButton\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "PreferencesButton missing from MainWindow.axaml");
        var button = xaml[start..xaml.IndexOf("</Button>", start, StringComparison.Ordinal)];
        Assert.Contains("Command=\"{Binding OpenPreferencesCommand}\"", button);
        Assert.Contains("Icons.Settings", button);
        Assert.Contains("AutomationProperties.Name=\"Preferences\"", button);
    }

    [Fact]
    public void Theme_menu_only_switches_the_theme()
    {
        var xaml = Xaml();
        var start = xaml.IndexOf("ToolTip.Tip=\"Theme\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "theme button missing");
        var menu = xaml[start..xaml.IndexOf("</MenuFlyout>", start, StringComparison.Ordinal)];
        Assert.DoesNotContain("OpenPreferencesCommand", menu);
    }
}
