using DbExplorer.Core.Abstractions;
using DbExplorer.Core.Connections;
using Microsoft.Data.Sqlite;

namespace DbExplorer.Providers.Sqlite;

/// <summary>SQLite 3 database files. The connection's Host holds the file's path; there is no server, port or sign-in.</summary>
public sealed class SqliteProviderFactory : IDatabaseProviderFactory
{
    public const string ProviderKey = "Sqlite";

    public string Key => ProviderKey;
    public string DisplayName => "SQLite";
    public int DefaultPort => 0;
    public bool SupportsIntegratedSecurity => false;
    public bool SupportsReadOnlyIntent => false;
    public bool IsFileBased => true;

    public IDatabaseProvider Create(ConnectionProfile profile)
    {
        var path = SqliteSql.FilePath(profile.Host);
        if (path.Length == 0) throw new InvalidOperationException("Choose the SQLite database file.");
        if (!File.Exists(path))
            throw new FileNotFoundException($"There is no SQLite file at {path}. Use New file… to create one.", path);
        return new SqliteProvider(profile);
    }

    public Task CreateDatabaseFileAsync(string path, CancellationToken ct = default) => CreateFileAsync(path, ct);

    /// <summary>A file holds one database, which SQLite calls main.</summary>
    public Task<IReadOnlyList<string>> ListDatabasesAsync(ConnectionProfile profile, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>([SqliteSql.Main]);

    /// <summary>
    /// Creates an empty database file at <paramref name="path"/> (and its folder). An existing file is left as it is.
    /// SQLite writes nothing until the first table, so the header is written now: the file is a valid database at once.
    /// </summary>
    public static async Task CreateFileAsync(string path, CancellationToken ct = default)
    {
        var full = SqliteSql.FilePath(path);
        if (Path.GetDirectoryName(full) is { Length: > 0 } folder) Directory.CreateDirectory(folder);
        var cs = new SqliteConnectionStringBuilder { DataSource = full, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        await Task.Run(() =>
        {
            using var cn = new SqliteConnection(cs);
            cn.Open();
            using var cmd = cn.CreateCommand();
            // Setting user_version forces SQLite to write page 1, the database header.
            cmd.CommandText = "PRAGMA user_version = 0; PRAGMA journal_mode = DELETE; CREATE TABLE dbx_init(x); DROP TABLE dbx_init;";
            cmd.ExecuteNonQuery();
        }, ct);
    }
}
