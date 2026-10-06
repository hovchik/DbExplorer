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

    /// <summary>Names of the databases the snapshot has objects from (several when connected to a whole server).</summary>
    public IReadOnlyList<string> Databases => Objects.Select(o => o.Database).Where(d => !string.IsNullOrEmpty(d))
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    public bool ContainsDatabase(string database) =>
        Objects.Any(o => string.Equals(o.Database, database, StringComparison.OrdinalIgnoreCase));

    /// <summary>The part of the catalog that belongs to one database, so same-named objects of other databases
    /// (every database's dbo.Transactions) do not mix in.</summary>
    public MetadataSnapshot ForDatabase(string database)
    {
        bool In(string db) => string.Equals(db, database, StringComparison.OrdinalIgnoreCase);
        return new MetadataSnapshot
        {
            Objects = Objects.Where(o => In(o.Database)).ToList(),
            Columns = Columns.Where(c => In(c.Database)).ToList(),
            Modules = Modules.Where(m => In(m.Database)).ToList(),
            ForeignKeys = ForeignKeys.Where(f => In(f.Database)).ToList(),
            Indexes = Indexes.Where(i => In(i.Database)).ToList(),
            RefreshedAt = RefreshedAt,
            IsStale = IsStale
        };
    }

    /// <summary>The same catalog with the user's accepted virtual foreign keys in place of any previous ones.</summary>
    public MetadataSnapshot WithVirtualForeignKeys(IReadOnlyList<DbForeignKey> virtualKeys) => new()
    {
        Objects = Objects,
        Columns = Columns,
        Modules = Modules,
        ForeignKeys = ForeignKeys.Where(f => !f.IsVirtual).Concat(virtualKeys.Select(f => f with { IsVirtual = true })).ToList(),
        Indexes = Indexes,
        RefreshedAt = RefreshedAt,
        IsStale = IsStale
    };

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

    /// <summary>Builds the per-table lookups now (they are otherwise built on first use), so a large catalog's
    /// indexing happens on the loading thread rather than on whichever UI handler asks first.</summary>
    public MetadataSnapshot Warm()
    {
        const string none = "\0";
        _ = ColumnsOf(none, none, none);
        _ = ForeignKeysOf(none, none, none);
        _ = ReferencesTo(none, none, none);
        _ = IndexesOf(none, none, none);
        return this;
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
