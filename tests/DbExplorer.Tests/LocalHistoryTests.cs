using DbExplorer.Application;
using DbExplorer.Application.Query;
using DbExplorer.Core.Models;

namespace DbExplorer.Tests;

public sealed class LocalHistoryTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("dbx-local-history-").FullName;
    private DateTimeOffset _now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); }
        catch (IOException) { }
    }

    private LocalHistoryStore Store() => new(new AppPaths(_temp)) { Now = () => _now };

    private DateTimeOffset Tick(int minutes = 1) => _now = _now.AddMinutes(minutes);

    [Fact]
    public async Task Versions_are_kept_newest_first_and_identical_consecutive_text_once()
    {
        var store = Store();
        var key = LocalHistoryStore.KeyFor(Guid.NewGuid(), null);

        Assert.True(await store.RecordAsync(key, "Query 1", null, "select 1", LocalHistoryReason.Run));
        Tick();
        Assert.False(await store.RecordAsync(key, "Query 1", null, "select 1", LocalHistoryReason.Run));
        Assert.True(await store.RecordAsync(key, "orders", null, "select 2", LocalHistoryReason.Run));
        Tick();
        Assert.True(await store.RecordAsync(key, "orders", null, "select 1", LocalHistoryReason.Closed));

        var doc = await store.LoadAsync(key);
        Assert.NotNull(doc);
        Assert.Equal(["select 1", "select 2", "select 1"], doc.Versions.Select(v => v.Text));
        Assert.Equal([LocalHistoryReason.Closed, LocalHistoryReason.Run, LocalHistoryReason.Run], doc.Versions.Select(v => v.Reason));
        Assert.Equal("orders", doc.Title);
        Assert.Equal("Query 1", doc.Versions[^1].Title);
    }

    [Fact]
    public async Task Blank_and_huge_texts_are_not_kept()
    {
        var store = Store();
        var key = LocalHistoryStore.KeyFor(Guid.NewGuid(), null);

        Assert.False(await store.RecordAsync(key, "t", null, "  \n ", LocalHistoryReason.Run));
        Assert.False(await store.RecordAsync(key, "t", null, new string('x', LocalHistoryStore.MaxTextLength + 1), LocalHistoryReason.Run));
        Assert.Null(await store.LoadAsync(key));
    }

    [Fact]
    public async Task Old_versions_and_versions_past_the_cap_are_dropped_but_the_newest_stays()
    {
        var store = Store();
        var key = LocalHistoryStore.KeyFor(Guid.NewGuid(), null);
        for (var i = 0; i < LocalHistoryStore.MaxVersionsPerScript + 5; i++)
        {
            await store.RecordAsync(key, "t", null, $"select {i}", LocalHistoryReason.Run);
            Tick();
        }
        var doc = await store.LoadAsync(key);
        Assert.Equal(LocalHistoryStore.MaxVersionsPerScript, doc!.Versions.Count);
        Assert.Equal($"select {LocalHistoryStore.MaxVersionsPerScript + 4}", doc.Versions[0].Text);

        // 31 days later, one more run: everything older than 30 days goes.
        _now = _now.AddDays(31);
        await store.RecordAsync(key, "t", null, "select 'new'", LocalHistoryReason.Run);
        Assert.Equal(["select 'new'"], (await store.LoadAsync(key))!.Versions.Select(v => v.Text));
    }

    [Fact]
    public async Task Saving_a_tab_to_a_file_moves_its_versions_to_the_file()
    {
        var store = Store();
        var tab = Guid.NewGuid();
        var file = Path.Combine(_temp, "report.sql");
        var tabKey = LocalHistoryStore.KeyFor(tab, null);
        var fileKey = LocalHistoryStore.KeyFor(tab, file);
        Assert.NotEqual(tabKey, fileKey);
        // The file key does not depend on the tab: reopening the file in another tab finds the same history.
        Assert.Equal(fileKey, LocalHistoryStore.KeyFor(Guid.NewGuid(), file));

        await store.RecordAsync(tabKey, "Query 1", null, "select 1", LocalHistoryReason.Run);
        Tick();
        await store.MoveAsync(tabKey, fileKey, "report.sql", file);
        await store.RecordAsync(fileKey, "report.sql", file, "select 1 -- saved", LocalHistoryReason.Saved);

        Assert.Null(await store.LoadAsync(tabKey));
        var doc = await store.LoadAsync(fileKey);
        Assert.Equal(file, doc!.FilePath);
        Assert.Equal(["select 1 -- saved", "select 1"], doc.Versions.Select(v => v.Text));
    }

    [Fact]
    public async Task All_scripts_lists_closed_tabs_most_recent_first_and_clear_forgets_everything()
    {
        var store = Store();
        await store.RecordAsync(LocalHistoryStore.KeyFor(Guid.NewGuid(), null), "closed one", null, "select 'a'", LocalHistoryReason.Closed);
        Tick();
        await store.RecordAsync(LocalHistoryStore.KeyFor(Guid.NewGuid(), null), "open one", null, "select 'b'", LocalHistoryReason.Run);

        Assert.Equal(["open one", "closed one"], (await store.LoadAllAsync()).Select(d => d.Title));

        await store.ClearAsync();
        Assert.Empty(await store.LoadAllAsync());
    }

    [Fact]
    public async Task A_damaged_file_reads_as_empty_and_is_replaced()
    {
        var store = Store();
        var key = LocalHistoryStore.KeyFor(Guid.NewGuid(), null);
        Directory.CreateDirectory(store.Folder);
        await File.WriteAllTextAsync(Path.Combine(store.Folder, key + ".json"), "{\"Versions\": [");

        Assert.Null(await store.LoadAsync(key));
        Assert.Empty(await store.LoadAllAsync());
        Assert.True(await store.RecordAsync(key, "t", null, "select 1", LocalHistoryReason.Run));
        Assert.Single((await store.LoadAsync(key))!.Versions);
    }

    [Fact]
    public async Task Keys_that_could_leave_the_folder_are_refused()
    {
        var store = Store();
        await Assert.ThrowsAsync<ArgumentException>(() => store.LoadAsync("../settings"));
    }

    [Fact]
    public void Diff_shows_what_restoring_the_version_would_change()
    {
        var (rows, added, removed) = LocalHistoryDiff.Build(
            "select id\nfrom orders\nwhere total > 10",
            "select id, total\nfrom orders\nwhere total > 10",
            onlyChanges: false);

        Assert.Equal(1, added);
        Assert.Equal(1, removed);
        Assert.Equal(
            [(DiffLineKind.Removed, "select id"), (DiffLineKind.Added, "select id, total"), (DiffLineKind.Equal, "from orders"), (DiffLineKind.Equal, "where total > 10")],
            rows!.Select(r => (r.Kind, r.Text)));
        Assert.Equal(["−", "+", "", ""], rows!.Select(r => r.Sign));
    }

    [Fact]
    public void Only_changes_hides_long_unchanged_runs_and_crlf_does_not_count_as_a_change()
    {
        var lines = Enumerable.Range(1, 30).Select(i => $"-- line {i}").ToList();
        var before = string.Join("\r\n", lines);
        lines[15] = "-- changed";
        var after = string.Join("\n", lines);

        var (rows, added, removed) = LocalHistoryDiff.Build(before, after, onlyChanges: true);
        Assert.Equal((1, 1), (added, removed));
        Assert.Equal(2, rows!.Count(r => r.Kind == DiffLineKind.Skipped));
        Assert.True(rows!.Count < 15);
    }

    [Fact]
    public void Labels_and_previews()
    {
        var now = new DateTimeOffset(new DateTime(2026, 11, 2, 15, 0, 0, DateTimeKind.Local));
        Assert.StartsWith("Today ", LocalHistoryDiff.When(now.AddMinutes(-5), now));
        Assert.StartsWith("Yesterday ", LocalHistoryDiff.When(now.AddDays(-1), now));
        Assert.Equal("select 1", LocalHistoryDiff.Preview("\n\n   select 1  \nfrom x"));
        Assert.Equal("Before restore", LocalHistoryDiff.Label(LocalHistoryReason.BeforeRestore));
    }
}
