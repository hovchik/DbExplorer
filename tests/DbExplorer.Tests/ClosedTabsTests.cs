using DbExplorer.Application;
using DbExplorer.Application.Query;

namespace DbExplorer.Tests;

public sealed class ClosedTabsTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 11, 2, 20, 0, 0, TimeSpan.Zero);
    private readonly string _temp = Directory.CreateTempSubdirectory("dbx-closed-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); }
        catch (IOException) { }
    }

    private static QueryTabState Tab(string title, string sql = "select 1", string? file = null, Guid? id = null) =>
        new() { Title = title, Sql = sql, FilePath = file, Id = id ?? Guid.NewGuid() };

    [Fact]
    public void The_last_closed_tab_comes_back_first()
    {
        var closed = new ClosedTabs();
        closed.Add(Tab("A"), 0, At);
        closed.Add(Tab("B"), 2, At.AddMinutes(1));

        var latest = closed.TakeLatest();
        Assert.Equal("B", latest!.Tab.Title);
        Assert.Equal(2, latest.Index);
        Assert.Equal("A", closed.TakeLatest()!.Tab.Title);
        Assert.Null(closed.TakeLatest());
    }

    [Fact]
    public void Empty_scratch_tabs_are_not_kept_but_file_tabs_are()
    {
        var closed = new ClosedTabs();

        Assert.False(closed.Add(Tab("Query 3", "   \n"), 0, At));
        Assert.True(closed.Add(Tab("empty.sql", "", file: Path.Combine(Path.GetTempPath(), "empty.sql")), 0, At));
        Assert.Equal("empty.sql", Assert.Single(closed.Items).Tab.Title);
    }

    [Fact]
    public void A_tab_closed_again_replaces_its_older_entry()
    {
        var id = Guid.NewGuid();
        var file = Path.Combine(Path.GetTempPath(), "report.sql");
        var closed = new ClosedTabs();
        closed.Add(Tab("Query 1", "select 1", id: id), 0, At);
        closed.Add(Tab("report.sql", "select 2", file), 1, At);
        closed.Add(Tab("Query 1", "select 3", id: id), 0, At);
        closed.Add(Tab("report.sql", "select 4", file), 1, At);

        Assert.Equal(["select 4", "select 3"], closed.Items.Select(i => i.Tab.Sql));
    }

    [Fact]
    public void Only_the_newest_twenty_are_kept()
    {
        var closed = new ClosedTabs();
        for (var i = 0; i < ClosedTabs.MaxTabs + 5; i++) closed.Add(Tab($"T{i}"), 0, At);

        Assert.Equal(ClosedTabs.MaxTabs, closed.Count);
        Assert.Equal($"T{ClosedTabs.MaxTabs + 4}", closed.Items[0].Tab.Title);
        Assert.Equal("T5", closed.Items[^1].Tab.Title);
    }

    [Fact]
    public void A_picked_tab_is_taken_off_the_list_once()
    {
        var closed = new ClosedTabs();
        closed.Add(Tab("A"), 0, At);
        closed.Add(Tab("B"), 0, At);
        var a = closed.Items[1];

        Assert.Same(a, closed.Take(a));
        Assert.Null(closed.Take(a));
        Assert.Equal("B", Assert.Single(closed.Items).Tab.Title);
    }

    [Theory]
    [InlineData(0, 3, 0)]
    [InlineData(2, 3, 2)]
    [InlineData(7, 3, 3)]
    [InlineData(-1, 3, 0)]
    [InlineData(4, 0, 0)]
    public void A_reopened_tab_goes_back_to_its_place_or_the_end(int closedIndex, int open, int expected) =>
        Assert.Equal(expected, ClosedTabs.InsertIndex(closedIndex, open));

    [Fact]
    public async Task The_list_survives_a_restart_and_a_damaged_file_reads_as_empty()
    {
        var store = new ScriptStore(new AppPaths(_temp));
        var closed = new ClosedTabs();
        var id = Guid.NewGuid();
        closed.Add(new QueryTabState { Title = "Orders", Sql = "select * from orders", Id = id, Database = "shop", AutoTitle = false }, 1, At);
        await store.SaveClosedTabsAsync(closed.Items);

        var reloaded = new ClosedTabs(await store.LoadClosedTabsAsync());
        var item = Assert.Single(reloaded.Items);
        Assert.Equal(id, item.Tab.Id);
        Assert.Equal("select * from orders", item.Tab.Sql);
        Assert.Equal("shop", item.Tab.Database);
        Assert.Equal(1, item.Index);
        Assert.Equal(At, item.ClosedAt);

        await File.WriteAllTextAsync(Path.Combine(_temp, "closed-query-tabs.json"), "[{]");
        Assert.Empty(new ClosedTabs(await store.LoadClosedTabsAsync()).Items);
    }

    [Fact]
    public void A_saved_list_drops_duplicates_and_empty_tabs()
    {
        var id = Guid.NewGuid();
        var reloaded = new ClosedTabs([
            new ClosedQueryTab { Tab = Tab("A", id: id) },
            new ClosedQueryTab { Tab = Tab("A older", id: id) },
            new ClosedQueryTab { Tab = Tab("empty", "") },
        ]);

        Assert.Equal("A", Assert.Single(reloaded.Items).Tab.Title);
    }
}
