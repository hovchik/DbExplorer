using DbExplorer.Application.Design;
using DbExplorer.Application.Metadata;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Modeling;

/// <summary>Reads existing tables from the cached catalog into a model, the way the Table designer opens a table.</summary>
public static class ErModelReader
{
    public const int MaxTables = 150;

    /// <summary>The tables of one database (all schemas, or one), as the server has them, laid out like the Diagram tab.</summary>
    /// <param name="snapshot">The catalog of that one database.</param>
    /// <param name="constraints">Column defaults per table (read from the server); a missing entry means none known.</param>
    public static ErModel Read(
        MetadataSnapshot snapshot, string providerKey, string database, string? schema,
        IReadOnlyDictionary<DbObject, DbTableConstraints>? constraints = null, int maxTables = MaxTables)
    {
        var tables = TablesOf(snapshot, schema).Take(maxTables).ToList();
        var model = ErModel.Empty(providerKey, database) with
        {
            Name = string.IsNullOrEmpty(schema) ? database : $"{database}.{schema}",
            Tables = tables.Select(t => Table(t, snapshot, providerKey, database, constraints)).ToList()
        };
        return ErModelLayout.Arrange(model);
    }

    /// <summary>The tables <see cref="Read"/> would take, most connected first when there are more than it takes.</summary>
    public static IEnumerable<DbObject> TablesOf(MetadataSnapshot snapshot, string? schema)
    {
        var tables = snapshot.Objects
            .Where(o => o.Type == DbObjectType.Table && (string.IsNullOrEmpty(schema) || TableDesign.Same(o.Schema, schema)))
            .ToList();
        int Links(DbObject t) => snapshot.ForeignKeysOf(t.Database, t.Schema, t.Name).Count() + snapshot.ReferencesTo(t.Database, t.Schema, t.Name).Count();
        return tables.OrderByDescending(Links).ThenBy(t => t.FullName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>One table read from the catalog: the design and its baseline are the same until it is edited.</summary>
    public static ModelTable Table(
        DbObject table, MetadataSnapshot snapshot, string providerKey, string database,
        IReadOnlyDictionary<DbObject, DbTableConstraints>? constraints = null)
    {
        var loaded = TableDesignLoader.Load(table, snapshot, constraints?.GetValueOrDefault(table) ?? DbTableConstraints.None, providerKey);
        var design = loaded.Design with { Database = database };
        return new ModelTable { Design = design, Baseline = design };
    }

    /// <summary>The model's table re-read after it was changed on the server: same place, the server's names for what the
    /// script named automatically.</summary>
    public static ErModel Rebase(ErModel model, string id, ModelTable fresh) =>
        model with { Tables = model.Tables.Select(t => t.Id == id ? fresh with { Id = t.Id, X = t.X, Y = t.Y } : t).ToList() };
}
