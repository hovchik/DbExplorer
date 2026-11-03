using DbExplorer.Application.Connections;
using DbExplorer.Core.Connections;
using DbExplorer.Providers.Sqlite;

namespace DbExplorer.Tests;

public sealed class DatabaseFilesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dbx-dbfiles-").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task A_sqlite_file_is_told_from_a_script_by_its_header()
    {
        var db = Path.Combine(_dir, "shop.data");
        await SqliteProviderFactory.CreateFileAsync(db);
        await using (var provider = new SqliteProviderFactory().Create(new ConnectionProfile { ProviderKey = "Sqlite", Host = db }))
            await provider.ExecuteScriptAsync("CREATE TABLE t (id INTEGER);", null, 30);
        var script = Path.Combine(_dir, "report.sql");
        await File.WriteAllTextAsync(script, "select 1;");
        var text = Path.Combine(_dir, "notes.db");
        await File.WriteAllTextAsync(text, "not a database");
        var empty = Path.Combine(_dir, "new.sqlite");
        await File.WriteAllBytesAsync(empty, []);

        Assert.True(DatabaseFiles.IsDatabaseFile(db));
        Assert.False(DatabaseFiles.IsDatabaseFile(script));
        Assert.False(DatabaseFiles.IsDatabaseFile(text));
        Assert.True(DatabaseFiles.IsDatabaseFile(empty));
        Assert.False(DatabaseFiles.IsDatabaseFile(Path.Combine(_dir, "missing.sql")));
    }

    [Fact]
    public void An_opened_file_reuses_its_saved_connection_or_gets_one_named_after_it()
    {
        var path = Path.Combine(_dir, "shop.db");
        var saved = new ConnectionProfile { Name = "Shop file", ProviderKey = "Sqlite", Host = path };
        var server = new ConnectionProfile { Name = "Server", ProviderKey = "Postgres", Host = path };

        var (found, isNew) = DatabaseFiles.ProfileFor([server, saved], Path.Combine(_dir, ".", "shop.db"));
        Assert.False(isNew);
        Assert.Same(saved, found);

        var (created, created2) = DatabaseFiles.ProfileFor([server], path);
        Assert.True(created2);
        Assert.Equal("shop", created.Name);
        Assert.Equal("Sqlite", created.ProviderKey);
        Assert.Equal(path, created.Host);
        Assert.True(created.IsFile);
    }
}
