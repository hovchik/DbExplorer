using DbExplorer.Application;
using DbExplorer.Application.Query;

namespace DbExplorer.Tests;

public class TabDatabaseMemoryTests : IDisposable
{
    private static readonly Guid Sales = Guid.NewGuid();
    private static readonly Guid Reporting = Guid.NewGuid();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbx-tabdb-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Each_connection_gets_back_the_database_picked_on_it()
    {
        var memory = new TabDatabaseMemory();
        memory.Remember(Sales, "SalesDb");
        memory.Remember(Reporting, "postgres");

        Assert.Equal("SalesDb", memory.Resolve(Sales, out var remembered));
        Assert.True(remembered);
        Assert.Equal("postgres", memory.Resolve(Reporting, out _));
        // Reconnecting later still finds it.
        Assert.Equal("SalesDb", memory.Resolve(Sales, out _));
    }

    [Fact]
    public void An_unknown_connection_starts_in_its_default()
    {
        var memory = new TabDatabaseMemory();
        memory.Remember(Sales, "SalesDb");

        Assert.Null(memory.Resolve(Reporting, out var remembered));
        Assert.False(remembered);
    }

    [Fact]
    public void A_pending_database_is_used_once_and_not_reported_as_remembered()
    {
        var memory = new TabDatabaseMemory { Pending = "Legacy" };

        Assert.Equal("Legacy", memory.Resolve(Sales, out var remembered));
        Assert.False(remembered);
        Assert.Null(memory.Resolve(Reporting, out _));
    }

    [Fact]
    public void Forgetting_with_null_returns_to_the_default()
    {
        var memory = new TabDatabaseMemory();
        memory.Remember(Sales, "SalesDb");
        memory.Remember(Sales, null);

        Assert.Null(memory.Resolve(Sales, out var remembered));
        Assert.False(remembered);
    }

    [Fact]
    public void A_clone_is_independent()
    {
        var memory = new TabDatabaseMemory();
        memory.Remember(Sales, "SalesDb");
        var copy = memory.Clone();
        copy.Remember(Sales, "Other");

        Assert.Equal("SalesDb", memory.Resolve(Sales, out _));
        Assert.Equal("Other", copy.Resolve(Sales, out _));
    }

    [Fact]
    public void Older_tabs_files_with_one_database_still_restore_it()
    {
        var memory = TabDatabaseMemory.From(new QueryTabState { Database = "Legacy" });

        Assert.Equal("Legacy", memory.Resolve(Sales, out var remembered));
        Assert.False(remembered);
    }

    [Fact]
    public async Task Databases_survive_a_restart_through_the_tabs_file()
    {
        var memory = new TabDatabaseMemory();
        memory.Remember(Sales, "SalesDb");
        memory.Remember(Reporting, "postgres");
        var state = new QueryTabState { Title = "Query 1", Sql = "select 1" };
        memory.SaveTo(state, current: "SalesDb");

        var store = new ScriptStore(new AppPaths(_root));
        await store.SaveTabsAsync([state]);
        var loaded = Assert.Single(await store.LoadTabsAsync());

        Assert.Equal("SalesDb", loaded.Database);
        var restored = TabDatabaseMemory.From(loaded);
        Assert.Equal("SalesDb", restored.Resolve(Sales, out var remembered));
        Assert.True(remembered);
        Assert.Equal("postgres", restored.Resolve(Reporting, out _));
    }

    [Fact]
    public void A_disconnected_tab_with_an_unused_pending_database_keeps_it_when_saved()
    {
        var memory = new TabDatabaseMemory { Pending = "Legacy" };
        var state = new QueryTabState();
        memory.SaveTo(state, current: null);

        Assert.Equal("Legacy", state.Database);
        Assert.Null(state.Databases);
    }
}
