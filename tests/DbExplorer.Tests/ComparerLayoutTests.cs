using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbExplorer.Application.Compare;
using DbExplorer.Application.Sessions;
using DbExplorer.Application.Shortcuts;
using DbExplorer.Core.Models;
using DbExplorer.Desktop.ViewModels;
using DbExplorer.Desktop.Views;

namespace DbExplorer.Tests;

public class OverviewFilterTests
{
    private static ObjectComparisonEntry Entry(string name, DbObjectType type, ObjectCompareStatus status) =>
        new() { Schema = "dbo", Name = name, Type = type, Status = status };

    private static readonly ObjectComparisonEntry[] Entries =
    [
        Entry("Customers", DbObjectType.Table, ObjectCompareStatus.Different),
        Entry("Orders", DbObjectType.Table, ObjectCompareStatus.Identical),
        Entry("v_totals", DbObjectType.MaterializedView, ObjectCompareStatus.OnlyLeft),
        Entry("fn_total", DbObjectType.ScalarFunction, ObjectCompareStatus.OnlyRight),
        Entry("trg_touch", DbObjectType.Trigger, ObjectCompareStatus.Different),
        Entry("seq_orders", DbObjectType.Sequence, ObjectCompareStatus.NotCompared)
    ];

    [Fact]
    public void Default_shows_what_differs_or_exists_on_one_side()
    {
        var shown = new OverviewFilter().Apply(Entries).Select(e => e.Name);
        Assert.Equal(["Customers", "v_totals", "fn_total", "trg_touch"], shown);
    }

    [Theory]
    [InlineData(OverviewTypeGroup.Tables, "Customers,Orders")]
    [InlineData(OverviewTypeGroup.Views, "v_totals")]
    [InlineData(OverviewTypeGroup.Routines, "fn_total")]
    [InlineData(OverviewTypeGroup.Triggers, "trg_touch")]
    [InlineData(OverviewTypeGroup.Other, "seq_orders")]
    public void Type_group_narrows_to_one_kind(OverviewTypeGroup group, string expected)
    {
        var filter = new OverviewFilter { Identical = true, NotCompared = true, Type = group };
        Assert.Equal(expected, string.Join(',', filter.Apply(Entries).Select(e => e.Name)));
    }

    [Fact]
    public void Name_filter_ignores_case_and_status_counts_ignore_the_status_switches()
    {
        var filter = new OverviewFilter { Different = false, Text = " ORD " };
        Assert.DoesNotContain(filter.Apply(Entries), e => e.Status == ObjectCompareStatus.Different);
        Assert.Equal(["Orders", "seq_orders"], Entries.Where(filter.MatchesIgnoringStatus).Select(e => e.Name));
    }
}

[Collection(nameof(HeadlessUiCollection))]
public class ComparerLayoutTests
{
    private static ComparerViewModel NewViewModel() =>
        new(new SessionService(null!, null!, null!), null!, null!, DesktopTabTests.DialogFake.Create(out _));

    private static DatabaseSession Shop(string database, params (string Name, DbObjectType Type)[] objects) =>
        DesktopTabTests.Session(DesktopTabTests.ScriptedProvider.Create([]), database: database,
            objects: objects.Select(o => new DbObject { Database = database, Schema = "dbo", Name = o.Name, Type = o.Type }).ToArray());

    [Fact]
    public void Opens_on_the_overview_and_sections_switch_through_their_flags()
    {
        var vm = NewViewModel();
        Assert.True(vm.IsOverviewTab);
        Assert.False(vm.IsObjectTab);
        Assert.False(vm.IsSetupComplete);

        vm.IsStructureTab = true;
        Assert.Equal(ComparerViewModel.StructureTab, vm.SelectedTabIndex);
        Assert.True(vm.IsObjectTab);
        Assert.False(vm.IsOverviewTab);

        // A second click on the selected section keeps it selected.
        vm.IsStructureTab = false;
        Assert.Equal(ComparerViewModel.StructureTab, vm.SelectedTabIndex);

        vm.IsMigrationTab = true;
        Assert.False(vm.IsObjectTab);
    }

    [Fact]
    public async Task Overview_runs_by_itself_once_both_sides_are_connected()
    {
        var vm = NewViewModel();
        var session = Shop("shop", ("Customers", DbObjectType.Table), ("Orders", DbObjectType.Table));
        vm.Attach(session);

        await vm.UseCurrentAsLeftCommand.ExecuteAsync(null);
        Assert.False(vm.HasOverview);
        Assert.Contains("right", vm.RightStepText, StringComparison.OrdinalIgnoreCase);

        await vm.UseCurrentAsRightCommand.ExecuteAsync(null);
        Assert.True(vm.IsSetupComplete);
        Assert.True(vm.HasOverview);
        Assert.Equal(2, vm.OverviewIdenticalCount);
        Assert.True(vm.IsOverviewFilteredEmpty); // identical objects are hidden by default
        Assert.Contains("match", vm.OverviewFilteredEmptyText);

        vm.ShowOverviewIdentical = true;
        Assert.Equal(2, vm.OverviewEntries.Count);
        Assert.False(vm.IsOverviewFilteredEmpty);

        await vm.DisconnectRightCommand.ExecuteAsync(null);
        Assert.False(vm.HasOverview);
    }

    [Fact]
    public async Task Overview_waits_for_a_database_outside_the_snapshot_instead_of_treating_it_as_empty()
    {
        var vm = NewViewModel();
        var session = Shop("shop", ("Customers", DbObjectType.Table));
        vm.Attach(session);
        await vm.UseCurrentAsLeftCommand.ExecuteAsync(null);
        await vm.UseCurrentAsRightCommand.ExecuteAsync(null);
        Assert.True(vm.HasOverview);

        // "archive" is not in the snapshot, so its catalog must be read first (here the read fails).
        vm.SelectedRightDatabase = "archive";
        await Task.Delay(50);
        Assert.False(vm.HasOverview);
        Assert.Equal(0, vm.OverviewOnlyLeftCount);
        Assert.False(vm.CompareOverviewCommand.CanExecute(null));
        Assert.Contains("could not be read", vm.OverviewStatus);
    }

    [Fact]
    public async Task Objects_that_could_not_be_compared_are_not_reported_as_a_match()
    {
        var vm = NewViewModel();
        var session = Shop("shop", ("order_seq", DbObjectType.Sequence));
        vm.Attach(session);
        await vm.UseCurrentAsLeftCommand.ExecuteAsync(null);
        await vm.UseCurrentAsRightCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.OverviewNotComparedCount);
        Assert.True(vm.IsOverviewFilteredEmpty);
        Assert.DoesNotContain("match", vm.OverviewFilteredEmptyText);
        Assert.Contains("Not compared", vm.OverviewFilteredEmptyText);
    }

    [Fact]
    public void F5_runs_the_comparer_section()
    {
        var shortcut = ShortcutCommands.All.Single(s => s.Id == ShortcutCommands.RunComparison);
        Assert.Equal(new KeyChord(KeyMods.None, "F5"), shortcut.Defaults.Single());
    }

    [AvaloniaFact]
    public void Section_bar_replaces_the_tab_headers()
    {
        var vm = NewViewModel();
        var view = new ComparerView { DataContext = vm };
        var window = new Window { Width = 1400, Height = 900, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var tabs = view.GetVisualDescendants().OfType<TabControl>().Single();
        var headers = tabs.GetVisualDescendants().OfType<ItemsPresenter>().First(p => p.Name == "PART_ItemsPresenter");
        Assert.False(headers.IsVisible);

        var sections = view.GetVisualDescendants().OfType<ToggleButton>().Where(b => b.Classes.Contains("section")).ToList();
        Assert.Equal(["Overview", "Migration", "Definition", "Structure", "Data", "Copy & sync"], sections.Select(b => b.Content as string));
        Assert.True(sections[0].IsChecked);

        sections.Single(b => (string?)b.Content == "Data").IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(ComparerViewModel.DataTab, vm.SelectedTabIndex);
        Assert.Equal(ComparerViewModel.DataTab, tabs.SelectedIndex);
        Assert.False(sections[0].IsChecked);

        window.Width = 800;
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("narrow", view.Classes);
        window.Close();
    }
}
