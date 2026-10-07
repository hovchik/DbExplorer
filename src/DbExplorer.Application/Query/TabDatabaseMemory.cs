namespace DbExplorer.Application.Query;

/// <summary>
/// The database a query tab works in, remembered per saved connection: a tab picked "Sales" on one connection and
/// "postgres" on another gets each back when that connection is opened again, after a disconnect or a restart.
/// </summary>
public sealed class TabDatabaseMemory
{
    private readonly Dictionary<Guid, string> _byConnection = [];

    /// <summary>
    /// A database not tied to a connection: from a tabs file written before databases were kept per connection, or
    /// asked for by the code that opened the tab. Used once, for the first connection without a remembered database.
    /// </summary>
    public string? Pending { get; set; }

    public IReadOnlyDictionary<Guid, string> ByConnection => _byConnection;

    /// <summary>
    /// The database to start in on <paramref name="connectionId"/>; null for the connection's default.
    /// <paramref name="remembered"/> is true when the user picked it on this connection before, so a database that is
    /// gone should be reported rather than quietly replaced.
    /// </summary>
    public string? Resolve(Guid connectionId, out bool remembered)
    {
        if (_byConnection.TryGetValue(connectionId, out var db))
        {
            remembered = true;
            Pending = null;
            return db;
        }
        remembered = false;
        var pending = Pending;
        Pending = null;
        return string.IsNullOrWhiteSpace(pending) ? null : pending;
    }

    /// <summary>Remembers <paramref name="database"/> for <paramref name="connectionId"/>; null or blank forgets it.</summary>
    public void Remember(Guid connectionId, string? database)
    {
        if (string.IsNullOrWhiteSpace(database)) _byConnection.Remove(connectionId);
        else _byConnection[connectionId] = database;
    }

    /// <summary>A copy for a tab opened from this one (new tab, duplicate).</summary>
    public TabDatabaseMemory Clone()
    {
        var copy = new TabDatabaseMemory { Pending = Pending };
        foreach (var (id, db) in _byConnection) copy._byConnection[id] = db;
        return copy;
    }

    /// <summary>Reads what <see cref="SaveTo"/> wrote, or the single <see cref="QueryTabState.Database"/> of older files.</summary>
    public static TabDatabaseMemory From(QueryTabState state)
    {
        var memory = new TabDatabaseMemory();
        if (state.Databases is { Count: > 0 } saved)
            foreach (var (id, db) in saved) memory.Remember(id, db);
        else memory.Pending = string.IsNullOrWhiteSpace(state.Database) ? null : state.Database;
        return memory;
    }

    /// <param name="current">The database shown in the tab now, kept in <see cref="QueryTabState.Database"/> as well.</param>
    public void SaveTo(QueryTabState state, string? current)
    {
        state.Database = string.IsNullOrWhiteSpace(current) ? Pending : current;
        state.Databases = _byConnection.Count == 0 ? null : new Dictionary<Guid, string>(_byConnection);
    }
}
