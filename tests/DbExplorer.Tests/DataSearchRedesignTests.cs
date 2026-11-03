using DbExplorer.Application;
using DbExplorer.Application.Lab;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.Query;
using DbExplorer.Application.Search;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Connections;
using DbExplorer.Core.Search;
using DbExplorer.Desktop.ViewModels;
using DbExplorer.Providers.Sqlite;
using Avalonia.Controls;
using Microsoft.Data.Sqlite;

namespace DbExplorer.Tests;

public sealed class DataMatchHighlightTests
{
    [Fact]
    public void Contains_splits_around_the_first_hit_ignoring_case()
    {
        var h = DataMatchHighlight.Split("customer12@Example.com", "example", SearchMatchMode.Contains);
        Assert.Equal(("customer12@", "Example", ".com"), (h.Before, h.Hit, h.After));
    }

    [Fact]
    public void Starts_and_ends_with_highlight_only_at_that_end()
    {
        var starts = DataMatchHighlight.Split("SKU-0012 SKU", "sku", SearchMatchMode.StartsWith);
        Assert.Equal(("", "SKU", "-0012 SKU"), (starts.Before, starts.Hit, starts.After));
        var ends = DataMatchHighlight.Split("SKU-0012 SKU", "sku", SearchMatchMode.EndsWith);
        Assert.Equal(("SKU-0012 ", "SKU", ""), (ends.Before, ends.Hit, ends.After));
    }

    [Fact]
    public void A_value_that_matched_another_way_is_highlighted_whole()
    {
        // A number compared exactly, or a collation that ignores accents: the text itself doesn't contain the term.
        var h = DataMatchHighlight.Split("12.00", "12", SearchMatchMode.Contains);
        Assert.Equal("12", h.Hit[..2]);
        var accent = DataMatchHighlight.Split("Café", "cafe", SearchMatchMode.Contains);
        Assert.Equal(("", "Café", ""), (accent.Before, accent.Hit, accent.After));
        var exact = DataMatchHighlight.Split("Acme", "acme", SearchMatchMode.Exact);
        Assert.Equal("Acme", exact.Hit);
    }

    [Fact]
    public void Line_breaks_become_spaces_and_long_values_are_cut_around_the_hit()
    {
        Assert.Equal("a b c", DataMatchHighlight.Split("a\r\nb\tc", "b", SearchMatchMode.Contains) is var h ? h.Before + h.Hit + h.After : "");

        var value = new string('x', 1000) + "NEEDLE" + new string('y', 1000);
        var cut = DataMatchHighlight.Split(value, "needle", SearchMatchMode.Contains);
        Assert.Equal("NEEDLE", cut.Hit);
        Assert.StartsWith("…", cut.Before);
        Assert.EndsWith("…", cut.After);
        Assert.True((cut.Before + cut.Hit + cut.After).Length <= DataMatchHighlight.MaxLength + 2);
    }

    [Fact]
    public void Null_or_empty_values_have_nothing_to_highlight()
    {
        Assert.Equal(new DataMatchHighlight("", "", ""), DataMatchHighlight.Split(null, "x", SearchMatchMode.Contains));
    }
}

/// <summary>The Search data tab against a real SQLite file (no server needed).</summary>
public sealed class DataSearchRedesignTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "dbx-datasearch-" + Guid.NewGuid().ToString("N"));
    private DatabaseSession _session = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        var file = Path.Combine(_folder, "shop.db");
        await SqliteProviderFactory.CreateFileAsync(file);
        var profile = new ConnectionProfile { ProviderKey = SqliteProviderFactory.ProviderKey, Host = file, Database = "", Name = "shop" };
        var factory = new SqliteProviderFactory();
        var provider = factory.Create(profile);
        await provider.ExecuteScriptAsync("""
            CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT NOT NULL, email TEXT, city TEXT);
            CREATE TABLE suppliers (id INTEGER PRIMARY KEY, company TEXT NOT NULL, contact TEXT);
            CREATE TABLE notes (body TEXT);
            INSERT INTO customers (name, email, city) VALUES ('Acme', 'acme@example.com', 'Oslo'), ('Globex', NULL, 'Rome'), ('Initech', 'it@example.com', NULL);
            INSERT INTO suppliers (company, contact) VALUES ('Parts Ltd', 'sales@example.com'), ('Bolts Inc', 'Bob');
            INSERT INTO notes (body) VALUES ('call acme@example.com back');
            """, null, 30);
        var paths = new AppPaths(Path.Combine(_folder, "app"));
        var metadata = new MetadataService(new MetadataCache(paths), new SchemaHistoryStore(paths), new VirtualForeignKeyStore(paths));
        var snapshot = await metadata.LoadAsync(profile, provider, forceRefresh: true);
        _session = new DatabaseSession(profile, factory, provider, await provider.GetServerVersionAsync(), snapshot);
    }

    public async Task DisposeAsync()
    {
        await _session.Provider.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    internal DataSearchViewModel CreateViewModel() => ViewModel();

    private DataSearchViewModel ViewModel()
    {
        var vm = new DataSearchViewModel(new DataSearchService(), new QueryExecutionService(), DesktopTabTests.DialogFake.Create(out _));
        vm.Attach(_session);
        return vm;
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Results_are_grouped_by_table_with_counts_and_the_hit_split_out()
    {
        var vm = ViewModel();
        Assert.True(vm.ShowWelcome);

        vm.Term = "example.com";
        await vm.StartCommand.ExecuteAsync(null);

        Assert.False(vm.ShowWelcome);
        Assert.True(vm.ShowResults);
        Assert.Equal(4, vm.Matches.Count);
        Assert.Equal(["All tables", "main.customers", "main.notes", "main.suppliers"], vm.Tables.Select(t => t.Label));
        Assert.Equal([4, 2, 1, 1], vm.Tables.Select(t => t.Count));
        Assert.Equal("4 matches in 3 tables", vm.FoundInSummary);

        var acme = vm.Matches.Single(m => m.Match.Value == "acme@example.com");
        Assert.Equal(("acme@", "example.com", ""), (acme.ValueBefore, acme.ValueHit, acme.ValueAfter));
        Assert.Equal(["example.com"], vm.RecentTerms);
    }

    [Fact]
    public async Task Picking_a_table_or_typing_a_filter_narrows_the_results()
    {
        var vm = ViewModel();
        vm.Term = "example";
        await vm.StartCommand.ExecuteAsync(null);

        vm.SelectedTable = vm.Tables.Single(t => t.Table == "customers");
        Assert.Equal(2, vm.Matches.Count);
        Assert.All(vm.Matches, m => Assert.Equal("customers", m.Match.Table));
        Assert.Equal("4 matches in 3 tables · showing 2", vm.FoundInSummary);

        vm.ResultFilter = "it@";
        Assert.Equal("it@example.com", Assert.Single(vm.Matches).Match.Value);
        Assert.Same(vm.Matches[0], vm.SelectedMatch);

        vm.SelectedTable = vm.Tables[0];
        vm.ResultFilter = "";
        Assert.Equal(4, vm.Matches.Count);
    }

    [Fact]
    public async Task Selecting_a_result_reads_its_whole_row()
    {
        var vm = ViewModel();
        vm.Term = "Oslo";
        await vm.StartCommand.ExecuteAsync(null);

        // The first result is selected when the search ends.
        Assert.NotNull(vm.SelectedMatch);
        await Until(() => !vm.IsRecordLoading && vm.RecordFields.Count > 0);

        Assert.Equal(["id", "name", "email", "city"], vm.RecordFields.Select(f => f.Column));
        Assert.True(vm.RecordFields.Single(f => f.Column == "id").IsKey);
        Assert.True(vm.RecordFields.Single(f => f.Column == "city").IsMatch);
        Assert.Equal("acme@example.com", vm.RecordFields.Single(f => f.Column == "email").Value);
        Assert.Equal("", vm.RecordStatus);
        Assert.Contains("main.customers · city · id=1", vm.SelectedTitle);
    }

    [Fact]
    public async Task A_row_from_a_table_without_a_primary_key_says_so_and_null_values_are_marked()
    {
        var vm = ViewModel();
        vm.Term = "Rome";
        await vm.StartCommand.ExecuteAsync(null);
        await Until(() => !vm.IsRecordLoading && vm.RecordFields.Count > 0);
        var email = vm.RecordFields.Single(f => f.Column == "email");
        Assert.True(email.IsNull);
        Assert.Equal("NULL", email.Value);

        vm.Term = "call acme";
        await vm.StartCommand.ExecuteAsync(null);
        await Until(() => !vm.IsRecordLoading && vm.RecordFields.Count > 0);
        Assert.Contains("no primary key", vm.RecordStatus);
    }

    [Fact]
    public async Task Nothing_found_shows_the_full_message_with_a_hint_for_the_mode()
    {
        var vm = ViewModel();
        vm.Term = "acme";
        vm.Mode = SearchMatchMode.Exact;
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Single(vm.Matches); // 'Acme' as a whole value

        vm.Term = "acm";
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Empty(vm.Matches);
        Assert.Empty(vm.Tables);
        Assert.True(vm.ShowNothingFound);
        Assert.False(vm.ShowResults);
        Assert.Contains("Whole value", vm.NoMatchesHint);
        Assert.Contains("“acm”", vm.NoMatchesTitle);
    }

    [Fact]
    public async Task Recent_terms_are_newest_first_without_duplicates()
    {
        var vm = ViewModel();
        foreach (var term in new[] { "Oslo", "Rome", "Oslo" })
        {
            vm.Term = term;
            await vm.StartCommand.ExecuteAsync(null);
        }
        Assert.Equal(["Oslo", "Rome"], vm.RecentTerms);

        vm.SelectedRecentTerm = "Rome";
        await Until(() => !vm.IsRunning);
        Assert.Equal("Rome", vm.Term);
        Assert.Equal(["Rome", "Oslo"], vm.RecentTerms);
        Assert.Null(vm.SelectedRecentTerm);
    }

    [Fact]
    public void Scope_and_limits_are_summarised_and_can_be_reset()
    {
        var vm = ViewModel();
        Assert.False(vm.ScopeChanged);
        Assert.Equal("50 rows/table · 30 s timeout · 3 at a time", vm.LimitsSummary);

        vm.TableChoices.Single(c => c.Table == "customers").IsSelected = true;
        Assert.True(vm.ScopeChanged);
        vm.IncludeViews = true;
        vm.MaxTableRows = 1_000_000;
        Assert.True(vm.ScopeChanged);
        Assert.EndsWith("skip > 1,000,000 rows", vm.LimitsSummary);

        vm.ResetScopeCommand.Execute(null);
        Assert.False(vm.ScopeChanged);
        Assert.DoesNotContain(vm.TableChoices, c => c.IsSelected);
    }

    [Fact]
    public async Task Ticking_tables_searches_only_those()
    {
        var vm = ViewModel();
        Assert.Equal(["main"], vm.SchemaChoices.Select(c => c.Name));
        Assert.Equal(["customers", "notes", "suppliers"], vm.TableChoices.Select(c => c.Name));
        Assert.Equal("All tables", vm.TableSummary);

        vm.TableChoices.Single(c => c.Name == "customers").IsSelected = true;
        vm.TableChoices.Single(c => c.Name == "notes").IsSelected = true;
        Assert.Equal("customers, notes", vm.TableSummary);

        vm.Term = "example.com";
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal(3, vm.Matches.Count);
        Assert.DoesNotContain(vm.Matches, m => m.Match.Table == "suppliers");
    }

    [Fact]
    public void Schemas_list_the_connection_and_narrow_the_tables()
    {
        var snapshot = new MetadataSnapshot
        {
            Objects =
            [
                Table("sales", "orders"), Table("sales", "customers"), Table("hr", "people"), Table("hr", "orders"),
                new DbExplorer.Core.Models.DbObject { Database = "db", Schema = "reports", Name = "v_totals", Type = DbExplorer.Core.Models.DbObjectType.View }
            ],
            Columns = [], Modules = [], ForeignKeys = [], Indexes = [], RefreshedAt = DateTimeOffset.Now
        };
        var vm = new DataSearchViewModel(new DataSearchService(), new QueryExecutionService(), DesktopTabTests.DialogFake.Create(out _));
        vm.Attach(new DatabaseSession(new ConnectionProfile { Name = "x" }, null!, _session.Provider, "1", snapshot));

        // Views' schemas only once views are searched too.
        Assert.Equal(["hr", "sales"], vm.SchemaChoices.Select(c => c.Name));
        Assert.Equal(["hr.orders", "hr.people", "sales.customers", "sales.orders"], vm.TableChoices.Select(c => c.Name));

        vm.TableChoices.Single(c => c.Name == "sales.orders").IsSelected = true;
        vm.SchemaChoices.Single(c => c.Name == "sales").IsSelected = true;
        Assert.Equal("sales", vm.SchemaSummary);
        Assert.Equal(["customers", "orders"], vm.TableChoices.Select(c => c.Name)); // one schema: plain names
        Assert.True(vm.TableChoices.Single(c => c.Name == "orders").IsSelected);   // the tick survives
        Assert.Equal([("sales", "orders")], vm.SelectedTables);

        vm.SchemaChoices.Single(c => c.Name == "hr").IsSelected = true;
        Assert.Equal("hr, sales", vm.SchemaSummary);
        Assert.Equal(4, vm.TableChoices.Count);

        vm.IncludeViews = true;
        Assert.Equal(["hr", "reports", "sales"], vm.SchemaChoices.Select(c => c.Name));
        Assert.True(vm.SchemaChoices.Single(c => c.Name == "hr").IsSelected);
        vm.SchemaChoices.Single(c => c.Name == "reports").IsSelected = true;
        Assert.Equal("3 schemas", vm.SchemaSummary);

        static DbExplorer.Core.Models.DbObject Table(string schema, string name) =>
            new() { Database = "db", Schema = schema, Name = name, Type = DbExplorer.Core.Models.DbObjectType.Table };
    }

    [Fact]
    public async Task The_service_can_search_only_the_given_tables()
    {
        var events = new List<DataSearchEvent>();
        var request = new DataSearchRequest { Term = "example", OnlyTables = [("MAIN", "Suppliers")] };
        await foreach (var e in new DataSearchService().SearchAsync(_session, request)) events.Add(e);

        var found = events.OfType<DataMatchFound>().Select(f => f.Match).ToList();
        Assert.Equal("sales@example.com", Assert.Single(found).Value);
        Assert.Equal(1, events.OfType<TableSearched>().Single().Total);
    }
}

[Collection(nameof(HeadlessUiCollection))]
public sealed class DataSearchViewTests
{
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task Enter_searches_and_down_moves_from_the_search_box_to_the_results()
    {
        var data = new DataSearchRedesignTests();
        await data.InitializeAsync();
        try
        {
            var vm = data.CreateViewModel();
            var view = new DbExplorer.Desktop.Views.DataSearchView { DataContext = vm };
            var window = new Avalonia.Controls.Window { Width = 1200, Height = 800, Content = view };
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var termBox = view.FindControl<Avalonia.Controls.TextBox>("TermBox")!;
            termBox.Focus();
            vm.Term = "example";
            Avalonia.Headless.HeadlessWindowExtensions.KeyPress(window, Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None,
                Avalonia.Input.PhysicalKey.Enter, null);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while ((vm.IsRunning || vm.Matches.Count == 0) && DateTime.UtcNow < deadline)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                await Task.Delay(20);
            }
            Assert.Equal(4, vm.Matches.Count);

            termBox.Focus();
            Avalonia.Headless.HeadlessWindowExtensions.KeyPress(window, Avalonia.Input.Key.Down, Avalonia.Input.RawInputModifiers.None,
                Avalonia.Input.PhysicalKey.ArrowDown, null);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var grid = view.FindControl<Avalonia.Controls.DataGrid>("ResultsGrid")!;
            Assert.True(grid.IsKeyboardFocusWithin);
            Assert.NotNull(vm.SelectedMatch);
            window.Close();
        }
        finally
        {
            await data.DisposeAsync();
        }
    }
}
