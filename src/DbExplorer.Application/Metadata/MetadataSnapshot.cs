using DbExplorer.Core.Models;

namespace DbExplorer.Application.Metadata;

/// <summary>An immutable copy of the database catalog. All name searches run against this.</summary>
public sealed class MetadataSnapshot
{
    private ILookup<(string Database, string Schema, string Table), DbColumn>? _columnsByTable;
    private ILookup<(string Database, string Schema, string Table), DbForeignKey>? _foreignKeysByTable;
    private ILookup<(string Database, string Schema, string Table), DbForeignKey>? _foreignKeysByReferencedTable;
    private ILookup<(string Database, string Schema, string Table), DbIndex>? _indexesByTable;

    public required IReadOnlyList<DbObject> Objects { get; init; }
    public required IReadOnlyList<DbColumn> Columns { get; init; }
    public required IReadOnlyList<DbModule> Modules { get; init; }
    public required IReadOnlyList<DbForeignKey> ForeignKeys { get; init; }
    public required IReadOnlyList<DbIndex> Indexes { get; init; }
    public required DateTimeOffset RefreshedAt { get; init; }

    /// <summary>Loaded from an older cache format that lacks some details: usable at once, but should be refreshed
    /// from the server in the background.</summary>
    public bool IsStale { get; init; }

    public IEnumerable<DbColumn> ColumnsOf(string database, string schema, string table)
    {
        var lookup = LazyInitializer.EnsureInitialized(
            ref _columnsByTable, () => Columns.ToLookup(c => (c.Database, c.Schema, c.Table), TableKeyComparer.Instance));
        return lookup[(database, schema, table)];
    }

    /// <summary>Foreign keys defined on the given table (this table is the child/referencing side).</summary>
    public IEnumerable<DbForeignKey> ForeignKeysOf(string database, string schema, string table)
    {
        var lookup = LazyInitializer.EnsureInitialized(
            ref _foreignKeysByTable, () => ForeignKeys.ToLookup(f => (f.Database, f.Schema, f.Table), TableKeyComparer.Instance));
        return lookup[(database, schema, table)];
    }

    /// <summary>Foreign keys in other tables that reference the given table (incoming references).</summary>
    public IEnumerable<DbForeignKey> ReferencesTo(string database, string schema, string table)
    {
        var lookup = LazyInitializer.EnsureInitialized(
            ref _foreignKeysByReferencedTable, () => ForeignKeys.ToLookup(f => (f.Database, f.ReferencedSchema, f.ReferencedTable), TableKeyComparer.Instance));
        return lookup[(database, schema, table)];
    }

    public IEnumerable<DbIndex> IndexesOf(string database, string schema, string table)
    {
        var lookup = LazyInitializer.EnsureInitialized(
            ref _indexesByTable, () => Indexes.ToLookup(i => (i.Database, i.Schema, i.Table), TableKeyComparer.Instance));
        return lookup[(database, schema, table)];
    }

    /// <summary>Database/schema/object names should match case-insensitively (e.g. SQL Server's default
    /// case-insensitive collation, or "dbo" vs "DBO"), otherwise lookups silently return no rows.</summary>
    private sealed class TableKeyComparer : IEqualityComparer<(string Database, string Schema, string Table)>
    {
        public static readonly TableKeyComparer Instance = new();

        public bool Equals((string Database, string Schema, string Table) x, (string Database, string Schema, string Table) y) =>
            string.Equals(x.Database, y.Database, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Schema, y.Schema, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Table, y.Table, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Database, string Schema, string Table) obj) =>
            HashCode.Combine(
                obj.Database?.ToUpperInvariant(),
                obj.Schema?.ToUpperInvariant(),
                obj.Table?.ToUpperInvariant());
    }
}
