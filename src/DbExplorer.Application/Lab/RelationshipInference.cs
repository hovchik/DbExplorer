using System.Globalization;
using System.Text.Json;
using DbExplorer.Application.Copy;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Models;
using DbExplorer.Core.Search;

namespace DbExplorer.Application.Lab;

/// <summary>A relationship the schema does not declare, guessed from names and types: <c>Child.Column → Parent.Key</c>.</summary>
public sealed record InferredRelationship(
    string Database, string ChildSchema, string ChildTable, string ChildColumn,
    string ParentSchema, string ParentTable, string ParentColumn,
    int Score, string Reason)
{
    public string Child => $"{ChildSchema}.{ChildTable}.{ChildColumn}";
    public string Parent => $"{ParentSchema}.{ParentTable}.{ParentColumn}";

    public string Confidence => Score >= 90 ? "high" : Score >= 70 ? "medium" : "low";

    public DbForeignKey ToForeignKey() => new()
    {
        Database = Database,
        Name = $"inferred_{ChildTable}_{ChildColumn}",
        Schema = ChildSchema,
        Table = ChildTable,
        Columns = ChildColumn,
        ReferencedSchema = ParentSchema,
        ReferencedTable = ParentTable,
        ReferencedColumns = ParentColumn,
        IsVirtual = true
    };
}

/// <summary>How many sampled child values exist in the parent column.</summary>
public sealed record ContainmentResult(long Sampled, long Matched)
{
    public double Percent => Sampled == 0 ? 0 : 100d * Matched / Sampled;
    public string Summary => Sampled == 0 ? "no values" : $"{Percent:0.#}% of {Sampled:N0} sampled values found";
}

/// <summary>
/// Finds foreign keys the schema does not declare. Names propose candidates (Orders.CustomerId → Customers.Id,
/// order_lines.order_id → orders.id, Invoices.CustomerCode → Customers.CustomerCode when that is a key) and types
/// must agree; <see cref="VerifyAsync"/> then checks a sample of the child values against the parent with one bounded,
/// non-blocking query.
/// </summary>
public static class RelationshipInference
{
    public static IReadOnlyList<InferredRelationship> Infer(MetadataSnapshot snapshot)
    {
        var tables = snapshot.Objects.Where(o => o.Type == DbObjectType.Table).ToList();

        // Parent keys: single-column primary keys and single-column unique indexes.
        var keys = new List<(DbObject Table, DbColumn Column, bool Primary)>();
        foreach (var t in tables)
        {
            var columns = snapshot.ColumnsOf(t.Database, t.Schema, t.Name).ToList();
            var pk = columns.Where(c => c.IsPrimaryKey).ToList();
            if (pk.Count == 1) keys.Add((t, pk[0], true));
            foreach (var index in snapshot.IndexesOf(t.Database, t.Schema, t.Name).Where(i => i.IsUnique && !i.IsPrimaryKey && i.Filter is null))
            {
                var cols = (index.Columns ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (cols.Length != 1) continue;
                var name = cols[0].EndsWith(" DESC", StringComparison.OrdinalIgnoreCase) ? cols[0][..^5] : cols[0];
                name = name.Trim().Trim('[', ']', '"');
                if (columns.FirstOrDefault(c => Same(c.Name, name)) is { } col) keys.Add((t, col, false));
            }
        }
        if (keys.Count == 0) return [];

        var declared = snapshot.ForeignKeys
            .SelectMany(f => SplitColumns(f.Columns).Select(c => Key(f.Database, f.Schema, f.Table, c)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var keysByDatabase = keys.ToLookup(k => k.Table.Database, StringComparer.OrdinalIgnoreCase);
        var result = new List<InferredRelationship>();
        foreach (var child in tables)
        {
            var childColumns = snapshot.ColumnsOf(child.Database, child.Schema, child.Name).ToList();
            var singlePk = childColumns.Count(c => c.IsPrimaryKey) == 1;
            foreach (var column in childColumns)
            {
                if (column.IsComputed || declared.Contains(Key(child.Database, child.Schema, child.Name, column.Name))) continue;
                // The table's own single-column key refers to itself, not to a parent (1:1 tables aside).
                if (column.IsPrimaryKey && singlePk) continue;

                InferredRelationship? best = null;
                foreach (var (parent, key, primary) in keysByDatabase[child.Database])
                {
                    if (Same(parent.Schema, child.Schema) && Same(parent.Name, child.Name)) continue;
                    if (!CompatibleTypes(column, key)) continue;
                    var (score, reason) = NameScore(column.Name, parent.Name, key.Name, primary);
                    if (score == 0) continue;
                    if (!Same(parent.Schema, child.Schema)) score -= 5;
                    if (best is null || score > best.Score)
                        best = new InferredRelationship(child.Database, child.Schema, child.Name, column.Name,
                            parent.Schema, parent.Name, key.Name, score, reason);
                }
                if (best is not null) result.Add(best);
            }
        }

        return result.OrderByDescending(r => r.Score).ThenBy(r => r.Child, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// How well a child column name points at a parent key. 0 = no match. Recognised shapes, for parent "Customers"
    /// with key "Id": CustomerId, Customer_Id, customer_id, CustomersId, IdCustomer; for a key that already carries the
    /// table name (CustomerId, customer_code), the same column name in the child.
    /// </summary>
    public static (int Score, string Reason) NameScore(string childColumn, string parentTable, string parentKey, bool primaryKey)
    {
        var column = Simplify(childColumn);
        var key = Simplify(parentKey);
        var bonus = primaryKey ? 0 : -10;
        var stems = TableStems(parentTable).ToList();

        // Same name as a distinctive key (not a bare "id"/"code"): Orders.CustomerId → Customers.CustomerId.
        if (column == key && !GenericKeyNames.Contains(key) && stems.Any(s => key.Contains(s, StringComparison.Ordinal)))
            return (95 + bonus, "same column name as the parent key");
        if (column == key && !GenericKeyNames.Contains(key))
            return (75 + bonus, "same column name as the parent key");

        foreach (var stem in stems)
        {
            if (column == stem + key || column == key + stem)
                return (90 + bonus, $"{childColumn} = {parentTable} + {parentKey}");
            // Role prefixes: BillingCustomerId, ParentOrderId, created_by_user_id.
            if (column.EndsWith(stem + key, StringComparison.Ordinal) && column.Length > (stem + key).Length)
                return (70 + bonus, $"{childColumn} ends with {parentTable} + {parentKey}");
        }

        return (0, "");
    }

    private static readonly HashSet<string> GenericKeyNames = new(StringComparer.Ordinal) { "id", "code", "key", "no", "num", "number", "uuid", "guid" };

    /// <summary>Lowercase, without separators: Customer_ID → customerid.</summary>
    private static string Simplify(string name) =>
        new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>The table name and its singular forms, without common prefixes: tblCustomers → customers, customer.</summary>
    public static IEnumerable<string> TableStems(string table)
    {
        var name = Simplify(table);
        foreach (var prefix in new[] { "tbl", "tb", "t" })
            if (name.Length > prefix.Length + 2 && name.StartsWith(prefix, StringComparison.Ordinal) &&
                table.Length > prefix.Length && (char.IsUpper(table[prefix.Length]) || table[prefix.Length] == '_'))
            {
                name = name[prefix.Length..];
                break;
            }

        var stems = new List<string> { name };
        if (name.EndsWith("ies", StringComparison.Ordinal)) stems.Add(name[..^3] + "y");
        if (name.EndsWith("ses", StringComparison.Ordinal) || name.EndsWith("xes", StringComparison.Ordinal) ||
            name.EndsWith("ches", StringComparison.Ordinal) || name.EndsWith("shes", StringComparison.Ordinal))
            stems.Add(name[..^2]);
        if (name.EndsWith('s') && !name.EndsWith("ss", StringComparison.Ordinal)) stems.Add(name[..^1]);
        return stems.Distinct();
    }

    /// <summary>Same type family: integers with integers, text with text, uuid with uuid, and so on.</summary>
    public static bool CompatibleTypes(DbColumn a, DbColumn b) => Family(a.BaseType) is { } fa && fa == Family(b.BaseType);

    private static string? Family(string baseType) => baseType.ToLowerInvariant() switch
    {
        "tinyint" or "smallint" or "int" or "bigint" or "int2" or "int4" or "int8" or "integer" or "serial" or "bigserial" or "smallserial" => "int",
        "decimal" or "numeric" => "decimal",
        "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext" or "bpchar" or "character" or "character varying" or "citext" or "sysname" => "text",
        "uniqueidentifier" or "uuid" => "uuid",
        "date" => "date",
        _ => null
    };

    /// <summary>
    /// Samples up to <paramref name="sampleRows"/> non-null child values and counts how many exist in the parent key: one
    /// query, run with the non-blocking data-search settings.
    /// </summary>
    public static async Task<ContainmentResult> VerifyAsync(
        DatabaseSession session, InferredRelationship relationship, int sampleRows, DataSearchOptions options, CancellationToken ct = default)
    {
        var sql = ContainmentSql(session.Provider.ProviderKey, relationship, sampleRows);
        var result = await session.Provider.QueryReadOnlyAsync(sql, string.IsNullOrEmpty(relationship.Database) ? null : relationship.Database, options, 1, ct);
        if (result.Rows.Count == 0) return new ContainmentResult(0, 0);
        var row = result.Rows[0];
        return new ContainmentResult(ToLong(row[0]), ToLong(row[1]));
    }

    public static string ContainmentSql(string providerKey, InferredRelationship r, int sampleRows)
    {
        var d = SqlDialect.For(providerKey);
        var child = d.Table(r.ChildSchema, r.ChildTable);
        var parent = d.Table(r.ParentSchema, r.ParentTable);
        var c = d.Quote(r.ChildColumn);
        var p = d.Quote(r.ParentColumn);
        var n = Math.Max(1, sampleRows).ToString(CultureInfo.InvariantCulture);
        var sample = providerKey == SqlDialect.SqlServerKey
            ? $"SELECT TOP ({n}) {c} AS v FROM {child} WHERE {c} IS NOT NULL"
            : $"SELECT {c} AS v FROM {child} WHERE {c} IS NOT NULL LIMIT {n}";
        return $"SELECT COUNT(*) AS sampled, " +
               $"SUM(CASE WHEN EXISTS (SELECT 1 FROM {parent} x WHERE x.{p} = s.v) THEN 1 ELSE 0 END) AS matched " +
               $"FROM ({sample}) s;";
    }

    private static long ToLong(object? value) => value is null ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);

    private static IEnumerable<string> SplitColumns(string? columns) =>
        (columns ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static string Key(string database, string schema, string table, string column) =>
        $"{database}\u0001{schema}\u0001{table}\u0001{column}";

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The relationships a user accepted for one connection, kept beside its metadata cache as JSON.</summary>
public sealed class VirtualForeignKeyStore(AppPaths paths)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private string FilePath(string key) => Path.Combine(paths.CacheDirectory, $"{key}.relations.json");

    public IReadOnlyList<DbForeignKey> Load(string key)
    {
        try
        {
            var file = FilePath(key);
            if (!File.Exists(file)) return [];
            var keys = JsonSerializer.Deserialize<List<DbForeignKey>>(File.ReadAllText(file)) ?? [];
            return keys.Select(k => k with { IsVirtual = true }).ToList();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public void Save(string key, IReadOnlyList<DbForeignKey> keys) =>
        File.WriteAllText(FilePath(key), JsonSerializer.Serialize(keys.Select(k => k with { IsVirtual = true }).ToList(), Json));
}
