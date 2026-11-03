using DbExplorer.Core.Connections;

namespace DbExplorer.Core.Abstractions;

/// <summary>Registration point for a database engine. Add a new engine by implementing this.</summary>
public interface IDatabaseProviderFactory
{
    string Key { get; }
    string DisplayName { get; }
    int DefaultPort { get; }
    bool SupportsIntegratedSecurity { get; }
    bool SupportsReadOnlyIntent { get; }

    /// <summary>
    /// The engine opens a file instead of a server (SQLite): <see cref="ConnectionProfile.Host"/> holds the file's path,
    /// and there is no port, sign-in, TLS or SSH tunnel.
    /// </summary>
    bool IsFileBased => false;

    /// <summary>File-based engines: creates an empty database file at <paramref name="path"/>.</summary>
    Task CreateDatabaseFileAsync(string path, CancellationToken ct = default) =>
        throw new NotSupportedException($"{DisplayName} databases live on a server, not in a file.");

    IDatabaseProvider Create(ConnectionProfile profile);

    Task<IReadOnlyList<string>> ListDatabasesAsync(ConnectionProfile profile, CancellationToken ct = default);
}
