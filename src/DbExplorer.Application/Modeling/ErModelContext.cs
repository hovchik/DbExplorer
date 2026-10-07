using DbExplorer.Application.Design;
using DbExplorer.Application.Metadata;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Modeling;

/// <summary>
/// What the Table designer's rules see while a model table is reviewed: the other tables of the model (so a foreign key
/// to a table drawn on the canvas is valid, and two tables with one name clash), plus the database's tables the model
/// does not have. A model table that was read from the database stands in for its server table.
/// </summary>
public static class ErModelContext
{
    /// <param name="database">The catalog of the database the model is checked against; null for the model alone.</param>
    /// <param name="except">The table under review: left out so its own name does not count as taken.</param>
    public static DesignContext For(ErModel model, MetadataSnapshot? database, string providerKey, string? except = null) =>
        DesignContext.From(Snapshot(model, database, providerKey, except), providerKey);

    /// <summary>The model's tables as a catalog, laid over the database's own.</summary>
    public static MetadataSnapshot Snapshot(ErModel model, MetadataSnapshot? database, string providerKey, string? except = null)
    {
        var covered = model.Tables.Where(t => t.Baseline is not null)
            .Select(t => Key(TableScriptBuilder.SchemaOf(t.Baseline!, providerKey), t.Baseline!.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Kept(string schema, string table) => !covered.Contains(Key(schema, table));

        var tables = model.Tables.Where(t => t.Id != except && t.Name.Length > 0).ToList();
        var objects = (database?.Objects.Where(o => o.Type != DbObjectType.Table || Kept(o.Schema, o.Name)) ?? [])
            .Concat(tables.Select(t => new DbObject { Schema = t.Schema(providerKey), Name = t.Name, Type = DbObjectType.Table }))
            .ToList();
        var columns = (database?.Columns.Where(c => Kept(c.Schema, c.Table)) ?? [])
            .Concat(tables.SelectMany(t => t.Design.Columns.Where(c => c.Name.Trim().Length > 0).Select((c, i) => new DbColumn
            {
                Schema = t.Schema(providerKey), Table = t.Name, Name = c.Name.Trim(), DataType = c.FullType, BaseType = c.BaseType,
                IsNullable = !c.WritesNotNull, Ordinal = i + 1, IsPrimaryKey = c.IsPrimaryKey, IsIdentity = c.IsIdentity
            })))
            .ToList();
        var foreignKeys = (database?.ForeignKeys.Where(f => Kept(f.Schema, f.Table)) ?? [])
            .Concat(tables.SelectMany(t => t.Design.ForeignKeys.Select(f => new DbForeignKey
            {
                Name = f.Name, Schema = t.Schema(providerKey), Table = t.Name, Columns = f.Column,
                ReferencedSchema = string.IsNullOrWhiteSpace(f.ReferencedSchema) ? TableScriptBuilder.DefaultSchema(providerKey, model.Database) : f.ReferencedSchema,
                ReferencedTable = f.ReferencedTable, ReferencedColumns = f.ReferencedColumn
            })))
            .ToList();

        // One database: the catalog's own database names would keep the lookups by (database, schema, table) apart.
        return new MetadataSnapshot
        {
            Objects = objects.Select(o => o with { Database = "" }).ToList(),
            Columns = columns.Select(c => c with { Database = "" }).ToList(),
            Modules = [],
            ForeignKeys = foreignKeys.Select(f => f with { Database = "" }).ToList(),
            Indexes = (database?.Indexes.Where(i => Kept(i.Schema, i.Table)) ?? []).Select(i => i with { Database = "" }).ToList(),
            RefreshedAt = database?.RefreshedAt ?? DateTimeOffset.Now
        };
    }

    private static string Key(string schema, string name) => schema.Trim() + "." + name.Trim();
}
