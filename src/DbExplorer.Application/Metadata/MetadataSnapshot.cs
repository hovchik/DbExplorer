using DbExplorer.Core.Models;

namespace DbExplorer.Application.Metadata;

/// <summary>An immutable copy of the database catalog. All name searches run against this.</summary>
public sealed class MetadataSnapshot
{
    private ILookup<(string Schema, string Table), DbColumn>? _columnsByTable;

    public required IReadOnlyList<DbObject> Objects { get; init; }
    public required IReadOnlyList<DbColumn> Columns { get; init; }
    public required IReadOnlyList<DbModule> Modules { get; init; }
    public required DateTimeOffset RefreshedAt { get; init; }

    public IEnumerable<DbColumn> ColumnsOf(string schema, string table)
    {
        var lookup = LazyInitializer.EnsureInitialized(
            ref _columnsByTable, () => Columns.ToLookup(c => (c.Schema, c.Table)));
        return lookup[(schema, table)];
    }
}
