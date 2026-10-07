using DbExplorer.Application.Copy;
using DbExplorer.Application.Metadata;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Design;

/// <summary>An existing table read into a <see cref="TableDesign"/>, with what the designer cannot show.</summary>
/// <param name="KeptIndexes">Indexes the designer has no row for (expressions, filters, included or descending columns,
/// non-btree): the ALTER script never touches them.</param>
/// <param name="KeptForeignKeys">Foreign keys over several columns, likewise left as they are.</param>
public sealed record LoadedTable(TableDesign Design, IReadOnlyList<string> KeptIndexes, IReadOnlyList<string> KeptForeignKeys);

/// <summary>Reads an existing table from the catalog into the designer's model, so it can be changed and diffed into an
/// ALTER TABLE script by <see cref="TableAlterScriptBuilder"/>.</summary>
public static class TableDesignLoader
{
    public static LoadedTable Load(DbObject table, MetadataSnapshot snapshot, DbTableConstraints constraints, string providerKey)
    {
        var columns = snapshot.ColumnsOf(table.Database, table.Schema, table.Name)
            .Where(c => !c.IsComputed)
            .OrderBy(c => c.Ordinal)
            .Select(c => Column(c, constraints, providerKey))
            .ToList();
        var names = columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var foreignKeys = new List<ForeignKeyDesign>();
        var keptKeys = new List<string>();
        foreach (var fk in snapshot.ForeignKeysOf(table.Database, table.Schema, table.Name).Where(f => !f.IsVirtual))
        {
            var column = fk.Columns?.Trim() ?? "";
            var referenced = fk.ReferencedColumns?.Trim() ?? "";
            if (column.Contains(',') || referenced.Contains(',') || !names.Contains(column))
            {
                keptKeys.Add(fk.Name);
                continue;
            }
            foreignKeys.Add(new ForeignKeyDesign
            {
                Name = fk.Name, Column = column, ReferencedSchema = fk.ReferencedSchema, ReferencedTable = fk.ReferencedTable, ReferencedColumn = referenced
            });
        }

        var indexes = new List<IndexDesign>();
        var keptIndexes = new List<string>();
        var primaryKeyName = "";
        foreach (var index in snapshot.IndexesOf(table.Database, table.Schema, table.Name))
        {
            if (index.IsPrimaryKey)
            {
                primaryKeyName = index.Name;
                continue;
            }
            var parts = (index.Columns ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(Unquote).ToList();
            var plain = parts.Count > 0 && parts.All(names.Contains) && string.IsNullOrWhiteSpace(index.IncludedColumns) &&
                        string.IsNullOrWhiteSpace(index.Filter) && !index.IsDisabled && IsPlainType(index.Type, providerKey);
            if (!plain)
            {
                keptIndexes.Add(index.Name);
                continue;
            }
            indexes.Add(new IndexDesign { Name = index.Name, Columns = parts.Select(p => names.First(n => TableDesign.Same(n, p))).ToList(), IsUnique = index.IsUnique });
        }

        var design = new TableDesign
        {
            Database = table.Database,
            Schema = table.Schema,
            Name = table.Name,
            Columns = columns,
            ForeignKeys = foreignKeys,
            Indexes = indexes,
            PrimaryKeyName = primaryKeyName
        };
        return new LoadedTable(design, keptIndexes, keptKeys);
    }

    private static ColumnDesign Column(DbColumn c, DbTableConstraints constraints, string providerKey)
    {
        var dataType = c.DataType.Length > 0 ? c.DataType : c.BaseType;
        var open = dataType.IndexOf('(');
        var close = dataType.LastIndexOf(')');
        // nvarchar(100) splits into type and size; timestamp(3) with time zone stays whole.
        var (type, size) = open > 0 && close == dataType.Length - 1
            ? (dataType[..open].Trim(), dataType[(open + 1)..close].Trim())
            : (dataType, (string?)null);
        constraints.Defaults.TryGetValue(c.Name, out var @default);
        return new ColumnDesign
        {
            Name = c.Name,
            OriginalName = c.Name,
            Type = type,
            Size = size,
            IsNullable = c.IsNullable && !c.IsPrimaryKey,
            IsPrimaryKey = c.IsPrimaryKey,
            IsIdentity = c.IsIdentity,
            Default = c.IsIdentity || string.IsNullOrWhiteSpace(@default) ? null : PlainDefault(@default, providerKey)
        };
    }

    /// <summary>SQL Server stores defaults wrapped in brackets: ((0)), (getdate()), (N'new') → 0, getdate(), N'new'.</summary>
    public static string PlainDefault(string expression, string providerKey)
    {
        var text = expression.Trim();
        if (providerKey != SqlDialect.SqlServerKey) return text;
        while (text.Length > 1 && text[0] == '(' && text[^1] == ')' && ClosesAtEnd(text)) text = text[1..^1].Trim();
        return text;
    }

    /// <summary>True when the bracket opened at 0 is the one closed at the end: (a)+(b) is not wrapped.</summary>
    private static bool ClosesAtEnd(string text)
    {
        var depth = 0;
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '\'') quoted = !quoted;
            if (quoted) continue;
            if (ch == '(') depth++;
            else if (ch == ')' && --depth == 0) return i == text.Length - 1;
        }
        return false;
    }

    private static string Unquote(string name) =>
        name.Length > 1 && (name[0] == '"' && name[^1] == '"' || name[0] == '[' && name[^1] == ']') ? name[1..^1] : name;

    private static bool IsPlainType(string type, string providerKey) =>
        providerKey == SqlDialect.SqlServerKey
            ? string.IsNullOrEmpty(type) || type.Contains("NONCLUSTERED", StringComparison.OrdinalIgnoreCase) && !type.Contains("COLUMNSTORE", StringComparison.OrdinalIgnoreCase)
            : string.IsNullOrEmpty(type) || type.Equals("btree", StringComparison.OrdinalIgnoreCase);
}
