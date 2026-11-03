using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.Copy;
using DbExplorer.Application.Search;
using DbExplorer.Application.Sessions;
using DbExplorer.Application.Shortcuts;
using DbExplorer.Core.Abstractions;
using DbExplorer.Core.Connections;
using DbExplorer.Core.Models;
using DbExplorer.Desktop.ViewModels;
using DbExplorer.Desktop.Views;

namespace DbExplorer.Tests;

/// <summary>The redesigned Search names &amp; code tab: grouping and ranking, overload line numbers, highlighting, the
/// view model's live search, filters, preview and actions, and the view itself.</summary>
[Collection(nameof(HeadlessUiCollection))]
public class MetadataSearchRedesignTests
{
    private static MetadataSnapshot Snapshot() => new()
    {
        Objects =
        [
            new DbObject { Schema = "sales", Name = "order_lines", Type = DbObjectType.Table },
            new DbObject { Schema = "sales", Name = "orders", Type = DbObjectType.Table },
            new DbObject { Schema = "dbo", Name = "Order", Type = DbObjectType.Table },
            new DbObject { Schema = "dbo", Name = "customers", Type = DbObjectType.Table },
            new DbObject { Schema = "dbo", Name = "usp_total", Type = DbObjectType.Procedure },
            new DbObject { Schema = "dbo", Name = "f", Type = DbObjectType.Function },
            new DbObject { Schema = "dbo", Name = "count_seq", Type = DbObjectType.Sequence }
        ],
        Columns =
        [
            new DbColumn { Schema = "dbo", Table = "customers", Name = "id", DataType = "int", Ordinal = 1 },
            new DbColumn { Schema = "dbo", Table = "customers", Name = "last_order", DataType = "date", Ordinal = 2 },
            new DbColumn { Schema = "sales", Table = "orders", Name = "id", DataType = "int", Ordinal = 1 }
        ],
        Modules =
        [
            new DbModule { Schema = "dbo", Name = "usp_total", Type = DbObjectType.Procedure, Definition = "CREATE PROCEDURE usp_total\nAS\nSELECT * FROM sales.orders" },
            // Two overloads: the second one's lines count on from the first (3 lines + a blank one).
            new DbModule { Schema = "dbo", Name = "f", Type = DbObjectType.Function, Definition = "CREATE FUNCTION f(a int)\nRETURNS int\nAS 1" },
            new DbModule { Schema = "dbo", Name = "f", Type = DbObjectType.Function, Definition = "CREATE FUNCTION f(a text)\nRETURNS int\nAS (SELECT count(*) FROM orders)" }
        ],
        ForeignKeys = [],
        Indexes = [],
        RefreshedAt = DateTimeOffset.Now
    };

    // ----- Application -----

    [Fact]
    public void Groups_rank_exact_name_then_prefix_then_name_then_column_then_code()
    {
        var query = new MetadataSearchQuery("order", MetadataSearchScope.All);
        var groups = MetadataSearchGrouping.Group(new MetadataSearchService().Search(Snapshot(), query), query);

        Assert.Equal(
            ["dbo.Order", "sales.orders", "sales.order_lines", "dbo.customers", "dbo.f", "dbo.usp_total"],
            groups.Select(g => g.FullName));
        Assert.Equal(MetadataSearchRank.ExactName, groups[0].Rank);
        Assert.Equal(MetadataSearchRank.Column, groups[3].Rank);
        Assert.Equal("last_order", Assert.Single(groups[3].Columns).Column);
        Assert.Equal(MetadataSearchRank.Code, groups[^1].Rank);
    }

    [Fact]
    public void Match_case_changes_what_counts_as_exact()
    {
        var query = new MetadataSearchQuery("order", MetadataSearchScope.ObjectNames, MatchCase: true);
        var groups = MetadataSearchGrouping.Group(new MetadataSearchService().Search(Snapshot(), query), query);
        Assert.DoesNotContain(groups, g => g.ObjectName == "Order");
        Assert.All(groups, g => Assert.Equal(MetadataSearchRank.NameStart, g.Rank));
    }

    [Fact]
    public void Overload_lines_count_from_the_start_of_the_joined_source()
    {
        var hit = Assert.Single(new MetadataSearchService().Search(Snapshot(),
            new MetadataSearchQuery("count", MetadataSearchScope.Definitions)));
        Assert.Equal(7, hit.Line);

        var text = MetadataSearchGrouping.ModuleText(Snapshot(), "", "dbo", "f", DbObjectType.Function)!;
        Assert.Contains("count(*)", text.Split('\n')[hit.Line!.Value - 1]);
    }

    [Fact]
    public void Segments_split_text_into_plain_and_matching_runs()
    {
        var regex = SearchTextMatcher.BuildRegex(new MetadataSearchQuery("or", MetadataSearchScope.All));
        Assert.Equal([("", false)], SearchTextMatcher.Segments(regex, "").Select(s => (s.Text, s.IsMatch)).ToList());
        Assert.Equal(
            [("w", false), ("or", true), ("k ", false), ("Or", true), ("der", false)],
            SearchTextMatcher.Segments(regex, "work Order").Select(s => (s.Text, s.IsMatch)).ToList());
        Assert.Equal([(1, 2), (5, 2)], SearchTextMatcher.Spans(regex, "work Order"));
    }

    [Theory]
    [InlineData(MetadataTypeFilter.Tables, DbObjectType.ForeignTable, true)]
    [InlineData(MetadataTypeFilter.Views, DbObjectType.MaterializedView, true)]
    [InlineData(MetadataTypeFilter.Routines, DbObjectType.TableFunction, true)]
    [InlineData(MetadataTypeFilter.Routines, DbObjectType.Trigger, false)]
    [InlineData(MetadataTypeFilter.Other, DbObjectType.Sequence, true)]
    [InlineData(MetadataTypeFilter.All, DbObjectType.Synonym, true)]
    public void Type_filter_covers_its_kinds(MetadataTypeFilter filter, DbObjectType type, bool expected) =>
        Assert.Equal(expected, filter.Matches(type));

    [Fact]
    public void Search_shortcut_is_registered_and_free()
    {
        var command = ShortcutCommands.Get(ShortcutCommands.SearchNames)!;
        Assert.Equal("Ctrl+Shift+N", command.Defaults[0].ToString());
        Assert.Single(ShortcutCommands.All, c => c.Defaults.Any(k => k.ToString() == "Ctrl+Shift+N"));
    }

    // ----- View model -----

    private static (MetadataSearchViewModel Vm, DatabaseSession Session) NewViewModel()
    {
        MetadataSearchViewModel.TypingDelay = TimeSpan.FromMilliseconds(1);
        var session = new DatabaseSession(new ConnectionProfile(), null!, QuotingProvider.Create(), "1", Snapshot());
        var vm = new MetadataSearchViewModel(new MetadataSearchService(), new DefinitionService());
        vm.Attach(session);
        return (vm, session);
    }

    private static async Task SearchAsync(MetadataSearchViewModel vm, string text)
    {
        vm.Query = text;
        await vm.SearchCommand.ExecuteAsync(null);
        await WaitAsync(() => !vm.IsSearching && vm.PreviewText.Length > 0);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Assert.True(condition());
    }

    [AvaloniaFact]
    public async Task Rows_are_grouped_with_hits_under_each_object_and_the_best_one_selected()
    {
        var (vm, _) = NewViewModel();
        await SearchAsync(vm, "order");

        Assert.True(vm.HasSearched);
        Assert.Equal(3, vm.NameCount);
        Assert.Equal(1, vm.ColumnCount);
        Assert.Equal(2, vm.CodeCount);
        Assert.Same(vm.Rows[0], vm.SelectedRow);
        Assert.Equal("dbo.Order", vm.Rows[0].Text);

        var customers = vm.Rows.First(r => r.IsObject && r.Group.ObjectName == "customers");
        var column = vm.Rows[vm.Rows.ToList().IndexOf(customers) + 1];
        Assert.Equal(MetadataSearchRowKind.Column, column.Kind);
        Assert.Equal("date", column.Hint);
        Assert.Contains(column.Segments, s => s.IsMatch && s.Text == "order");
        Assert.Contains("1 column", customers.Hint);
    }

    [AvaloniaFact]
    public async Task Chips_and_filters_narrow_the_rows_without_searching_again()
    {
        var (vm, _) = NewViewModel();
        await SearchAsync(vm, "order");

        vm.ShowNames = false;
        vm.ShowColumns = false;
        Assert.Equal(["dbo.f", "dbo.usp_total"], vm.Rows.Where(r => r.IsObject).Select(r => r.Group.FullName));

        vm.ShowNames = vm.ShowColumns = true;
        vm.SelectedTypeOption = vm.TypeOptions.First(o => o.Type == MetadataTypeFilter.Routines);
        Assert.All(vm.Rows, r => Assert.True(r.Group.ObjectType is DbObjectType.Procedure or DbObjectType.Function));

        vm.SelectedTypeOption = vm.TypeOptions[0];
        vm.SelectedSchemaOption = vm.SchemaOptions.First(o => o.Schema == "sales");
        Assert.All(vm.Rows, r => Assert.Equal("sales", r.Group.Schema));
        Assert.Equal("All types · 6", vm.TypeOptions[0].Label);
    }

    [AvaloniaFact]
    public async Task Collapsing_an_object_hides_its_hits_and_keeps_it_selected()
    {
        var (vm, _) = NewViewModel();
        await SearchAsync(vm, "order");
        var f = vm.Rows.First(r => r.IsObject && r.Group.ObjectName == "f");
        var before = vm.Rows.Count;

        vm.SetExpanded(f, false);
        Assert.Equal(before - 1, vm.Rows.Count);
        Assert.Equal("f", vm.SelectedRow!.Group.ObjectName);
        Assert.False(vm.SelectedRow.IsExpanded);
        Assert.Equal("▸", vm.SelectedRow.Chevron);

        vm.ExpandAllCommand.Execute(null);
        Assert.Equal(before, vm.Rows.Count);
    }

    [AvaloniaFact]
    public async Task Preview_shows_the_source_with_hits_and_the_selected_line()
    {
        var (vm, _) = NewViewModel();
        await SearchAsync(vm, "count");

        var line = vm.Rows.First(r => r.IsCode);
        vm.SelectedRow = line;
        await WaitAsync(() => vm.PreviewLine == 7);
        Assert.Equal(DbObjectType.Function, vm.PreviewType);
        Assert.StartsWith("CREATE FUNCTION f(a int)", vm.PreviewText);
        Assert.Single(vm.PreviewMatches);
        Assert.Equal("7 lines · 1 hit", vm.PreviewSummary);

        (string Sql, string? Database, string Title, int? Offset)? opened = null;
        vm.OpenSqlRequested += (sql, db, title, offset) => opened = (sql, db, title, offset);
        vm.Activate(line);
        Assert.NotNull(opened);
        Assert.Equal("f", opened!.Value.Title);
        Assert.StartsWith("AS (SELECT count", opened.Value.Sql[opened.Value.Offset!.Value..]);
    }

    [AvaloniaFact]
    public async Task Table_preview_is_its_script_with_the_column_line_marked()
    {
        var (vm, _) = NewViewModel();
        await SearchAsync(vm, "last_order");

        Assert.Equal("dbo.customers", vm.PreviewTitle);
        Assert.Contains("CREATE TABLE [dbo].[customers]", vm.PreviewText);
        var column = vm.Rows.First(r => r.Kind == MetadataSearchRowKind.Column);
        vm.SelectedRow = column;
        await WaitAsync(() => vm.PreviewLine == 3);
        Assert.True(vm.IsTableLikePreview);
        Assert.True(vm.SelectRowsCommand.CanExecute(null));

        string? copied = null;
        vm.CopyRequested += text => copied = text;
        vm.CopyNameCommand.Execute(null);
        Assert.Equal("last_order", copied);

        DbObject? shown = null;
        vm.ShowObjectRequested += o => shown = o;
        vm.SelectedRow = vm.Rows[0];
        vm.Activate(vm.Rows[0]);
        Assert.Equal("customers", shown?.Name);
    }

    [AvaloniaFact]
    public async Task Typing_searches_by_itself_and_clearing_goes_back_to_the_help()
    {
        var (vm, _) = NewViewModel();
        vm.Query = "o";
        await Task.Delay(30);
        Assert.False(vm.HasSearched); // one letter waits for Enter

        vm.Query = "usp";
        await WaitAsync(() => vm.HasSearched && vm.Rows.Count > 0);
        Assert.Equal("dbo.usp_total", vm.Rows[0].Group.FullName);

        vm.ClearCommand.Execute(null);
        Assert.True(vm.ShowEmptyHelp);
        Assert.Empty(vm.Rows);
        Assert.False(vm.HasPreview);
    }

    [AvaloniaFact]
    public async Task Nothing_found_and_a_broken_regex_say_so()
    {
        var (vm, _) = NewViewModel();
        vm.Query = "zzz_nothing";
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.True(vm.ShowNoResults);
        Assert.Contains("zzz_nothing", vm.EmptyMessage);

        vm.UseRegex = true;
        vm.Query = "(";
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.StartsWith("Not a valid regular expression", vm.Status);
    }

    [AvaloniaFact]
    public async Task Switching_to_an_object_that_loads_from_the_server_never_opens_the_previous_source()
    {
        var (vm, _) = NewViewModel();
        await SearchAsync(vm, "count");
        vm.SelectedRow = vm.Rows.First(r => r.IsCode && r.Group.ObjectName == "f");
        await WaitAsync(() => vm.PreviewLine == 7);
        Assert.True(vm.OpenInQueryCommand.CanExecute(null));

        // The sequence's source comes from the server, which has not answered yet.
        var pending = new TaskCompletionSource<string?>();
        QuotingProvider.Definition = pending.Task;
        vm.SelectedRow = vm.Rows.First(r => r.IsObject && r.Group.ObjectName == "count_seq");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("dbo.count_seq", vm.PreviewTitle);
        Assert.Equal("", vm.PreviewText);
        Assert.False(vm.OpenInQueryCommand.CanExecute(null));
        var opened = false;
        vm.OpenSqlRequested += (_, _, _, _) => opened = true;
        vm.OpenInQueryCommand.Execute(null);
        Assert.False(opened);

        pending.SetResult("CREATE SEQUENCE dbo.count_seq");
        await WaitAsync(() => vm.PreviewText.Length > 0);
        Assert.Equal("CREATE SEQUENCE dbo.count_seq", vm.PreviewText);
        Assert.True(vm.OpenInQueryCommand.CanExecute(null));
        QuotingProvider.Definition = QuotingProvider.ServerText;
    }

    // ----- View -----

    [AvaloniaFact]
    public async Task View_shows_rows_and_puts_the_source_in_the_preview_editor()
    {
        var (vm, _) = NewViewModel();
        var view = new MetadataSearchView { DataContext = vm };
        var window = new Window { Width = 1400, Height = 900, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.False(view.FindControl<Grid>("Body")!.IsVisible);
        await SearchAsync(vm, "count");
        Dispatcher.UIThread.RunJobs();

        var list = view.FindControl<ListBox>("ResultsList")!;
        Assert.True(list.IsEffectivelyVisible);
        Assert.Equal(vm.Rows.Count, list.ItemCount);
        var editor = view.FindControl<TextEditor>("PreviewEditor")!;
        Assert.Equal(vm.PreviewText, editor.Text);
        Assert.Contains(window.GetVisualDescendants().OfType<Desktop.Controls.HighlightedText>(),
            t => t.Inlines!.OfType<Avalonia.Controls.Documents.Run>().Any(r => r.Text == "count" && r.Background is not null));
        window.Close();
    }

    /// <summary>A provider that only quotes names SQL Server style; everything else is not needed here.</summary>
    public class QuotingProvider : DispatchProxy
    {
        internal static IDatabaseProvider Create() => Create<IDatabaseProvider, QuotingProvider>();

        internal static readonly Task<string?> ServerText = Task.FromResult<string?>("-- from the server");

        /// <summary>What GetDefinitionAsync returns (the source of objects the snapshot has none for).</summary>
        internal static Task<string?> Definition { get; set; } = ServerText;

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "get_ProviderKey" => SqlDialect.SqlServerKey,
            nameof(IDatabaseProvider.QuoteIdentifier) => $"[{args![0]}]",
            nameof(IDatabaseProvider.GetDefinitionAsync) => Definition,
            nameof(IAsyncDisposable.DisposeAsync) => ValueTask.CompletedTask,
            _ => throw new NotSupportedException(method?.Name)
        };
    }
}
