using System.Text;
using DbExplorer.Application.Copy;

namespace DbExplorer.Application.Design;

/// <summary>
/// Turns a <see cref="TableDesign"/> into the CREATE TABLE script for an engine: columns, primary key and foreign keys
/// as named constraints, then one CREATE INDEX per index. Constraints and indexes without a name get FK_/PK_/IX_ names
/// within the engine's identifier length.
/// </summary>
public static class TableScriptBuilder
{
    public static string Build(TableDesign design, string providerKey, bool createSchema = false)
    {
        var d = SqlDialect.For(providerKey);
        var table = d.Table(SchemaOf(design, providerKey), NameOf(design));
        var columns = design.Columns.Where(c => c.Name.Trim().Length > 0).ToList();
        var sb = new StringBuilder();

        if (createSchema) sb.Append(d.CreateSchemaIfMissing(SchemaOf(design, providerKey))).Append('\n');

        sb.Append("CREATE TABLE ").Append(table).Append(" (\n");
        var lines = new List<string>();
        foreach (var c in columns)
        {
            var line = new StringBuilder("    ").Append(d.Quote(c.Name.Trim())).Append(' ').Append(c.FullType.Length > 0 ? c.FullType : "?");
            if (c.IsIdentity) line.Append(d.IdentityClause);
            line.Append(c.WritesNotNull ? " NOT NULL" : " NULL");
            if (!string.IsNullOrWhiteSpace(c.Default) && !c.IsIdentity) line.Append(" DEFAULT ").Append(c.Default.Trim());
            lines.Add(line.ToString());
        }

        var pk = design.PrimaryKey.ToList();
        if (pk.Count > 0)
            lines.Add($"    CONSTRAINT {d.Quote(PrimaryKeyName(design, d))} PRIMARY KEY ({string.Join(", ", pk.Select(c => d.Quote(c.Name.Trim())))})");

        foreach (var fk in design.ForeignKeys.Where(f => f.Column.Length > 0 && f.ReferencedTable.Length > 0))
            lines.Add($"    CONSTRAINT {d.Quote(ForeignKeyName(design, fk, d))} FOREIGN KEY ({d.Quote(fk.Column.Trim())})\n" +
                      $"        REFERENCES {d.Table(fk.ReferencedSchema, fk.ReferencedTable)} ({d.Quote(fk.ReferencedColumn.Trim())})");

        sb.Append(string.Join(",\n", lines)).Append("\n);\n");

        foreach (var index in design.Indexes.Where(i => i.Columns.Count > 0))
            sb.Append("CREATE ").Append(index.IsUnique ? "UNIQUE " : "").Append("INDEX ").Append(d.Quote(IndexName(design, index, d)))
              .Append(" ON ").Append(table).Append(" (").Append(string.Join(", ", index.Columns.Select(c => d.Quote(c.Trim())))).Append(");\n");

        return sb.ToString();
    }

    /// <summary>The engine's usual schema when none is given: dbo or public.</summary>
    public static string DefaultSchema(string providerKey) => providerKey == SqlDialect.SqlServerKey ? "dbo" : "public";

    public static string SchemaOf(TableDesign design, string providerKey) =>
        string.IsNullOrWhiteSpace(design.Schema) ? DefaultSchema(providerKey) : design.Schema.Trim();

    internal static string NameOf(TableDesign design) => design.Name.Trim().Length > 0 ? design.Name.Trim() : "NewTable";

    public static string PrimaryKeyName(TableDesign design, SqlDialect d) =>
        design.PrimaryKeyName.Trim().Length > 0 ? design.PrimaryKeyName.Trim() : Fit($"PK_{NameOf(design)}", d);

    public static string ForeignKeyName(TableDesign design, ForeignKeyDesign fk, SqlDialect d)
    {
        if (fk.Name.Trim().Length > 0) return fk.Name.Trim();
        var byParent = $"FK_{NameOf(design)}_{fk.ReferencedTable}";
        // Two keys to the same parent (BillingAddressId, ShippingAddressId) are told apart by their column.
        var sameParent = design.ForeignKeys.Count(f => f.Name.Trim().Length == 0 && TableDesign.Same(f.ReferencedSchema, fk.ReferencedSchema) &&
                                                       TableDesign.Same(f.ReferencedTable, fk.ReferencedTable));
        return Fit(sameParent > 1 ? $"FK_{NameOf(design)}_{fk.Column.Trim()}" : byParent, d);
    }

    public static string IndexName(TableDesign design, IndexDesign index, SqlDialect d) =>
        index.Name.Trim().Length > 0
            ? index.Name.Trim()
            : Fit($"{(index.IsUnique ? "UX" : "IX")}_{NameOf(design)}_{string.Join("_", index.Columns.Select(c => c.Trim()))}", d);

    private static string Fit(string name, SqlDialect d) => d.TruncateIdentifier(name);
}
