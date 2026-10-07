using DbExplorer.Application;
using DbExplorer.Application.Query;

namespace DbExplorer.Tests;

public sealed class ScriptStoreTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("dbx-scripts-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task Runs_finishing_together_all_reach_the_history()
    {
        var store = new ScriptStore(new AppPaths(_temp));

        await Task.WhenAll(Enumerable.Range(0, 25).Select(i =>
            Task.Run(() => store.AppendHistoryAsync(new ScriptHistoryEntry { Sql = $"select {i}", Succeeded = true }))));

        var history = await store.LoadHistoryAsync();
        Assert.Equal(25, history.Count);
        Assert.Equal(25, history.Select(h => h.Sql).Distinct().Count());
    }

    [Fact]
    public async Task A_damaged_history_file_reads_as_empty_and_is_replaced_on_the_next_run()
    {
        var store = new ScriptStore(new AppPaths(_temp));
        await File.WriteAllTextAsync(Path.Combine(_temp, "script-history.json"), "[{\"Sql\": \"select 1\"}]]");

        Assert.Empty(await store.LoadHistoryAsync());

        await store.AppendHistoryAsync(new ScriptHistoryEntry { Sql = "select 2" });
        Assert.Equal("select 2", Assert.Single(await store.LoadHistoryAsync()).Sql);
    }
}
