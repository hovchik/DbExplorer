using System.Text.RegularExpressions;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Tests;

/// <summary>The main tab strip binds SelectedIndex to <see cref="AppTab"/> by ordinal, so the enum and the TabItems in
/// MainWindow.axaml must list the tabs in the same order.</summary>
public class MainTabOrderTests
{
    private static readonly AppTab[] Expected =
    [
        AppTab.Objects, AppTab.Query, AppTab.QueryBuilder, AppTab.TableDesigner, AppTab.Comparer, AppTab.Diagram,
        AppTab.SearchNamesAndCode, AppTab.SearchData, AppTab.ErModel, AppTab.Indexes, AppTab.Locks, AppTab.Activity,
        AppTab.Lab, AppTab.Security
    ];

    private static readonly Dictionary<string, AppTab> Headers = new()
    {
        ["Objects"] = AppTab.Objects, ["Query"] = AppTab.Query, ["Query builder"] = AppTab.QueryBuilder,
        ["Table designer"] = AppTab.TableDesigner, ["Comparer"] = AppTab.Comparer, ["Diagram"] = AppTab.Diagram,
        ["Search names &amp; code"] = AppTab.SearchNamesAndCode, ["Search data"] = AppTab.SearchData,
        ["ER model"] = AppTab.ErModel, ["Indexes"] = AppTab.Indexes, ["Locks"] = AppTab.Locks,
        ["Activity"] = AppTab.Activity, ["Lab"] = AppTab.Lab, ["Security"] = AppTab.Security
    };

    [Fact]
    public void Enum_lists_the_tabs_in_display_order() => Assert.Equal(Expected, Enum.GetValues<AppTab>());

    [Fact]
    public void Main_window_tab_items_follow_the_enum()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "DbExplorer.Desktop", "Views", "MainWindow.axaml"));
        var strip = xaml[xaml.IndexOf("x:Name=\"MainTabs\"", StringComparison.Ordinal)..];
        strip = strip[..strip.IndexOf("</TabControl>", StringComparison.Ordinal)];
        var tabs = Regex.Matches(strip, "<TabItem Header=\"([^\"]+)\"").Select(m => Headers[m.Groups[1].Value]);
        Assert.Equal(Enum.GetValues<AppTab>(), tabs);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "DbExplorer.Desktop"))) return dir.FullName;
        throw new DirectoryNotFoundException("Repository root not found");
    }
}
