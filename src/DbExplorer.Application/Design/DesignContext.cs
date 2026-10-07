using System.Text;
using DbExplorer.Application.Metadata;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Design;

public enum NamingStyle
{
    Unknown,
    /// <summary>OrderLines, CustomerId.</summary>
    Pascal,
    /// <summary>order_lines, customer_id.</summary>
    Snake
}

/// <summary>How the existing tables name their single-column primary key.</summary>
public enum KeyNaming
{
    Unknown,
    /// <summary>Customers.Id / customers.id.</summary>
    Id,
    /// <summary>Customers.CustomerId / customers.customer_id.</summary>
    TableId
}

/// <summary>An existing table a new one can reference.</summary>
public sealed record ExistingTable(DbObject Table, IReadOnlyList<DbColumn> Columns)
{
    public string Schema => Table.Schema;
    public string Name => Table.Name;
    public string FullName => Table.FullName;

    /// <summary>The single primary key column, or null when the key is composite or missing.</summary>
    public DbColumn? Key { get; } = Columns.Count(c => c.IsPrimaryKey) == 1 ? Columns.First(c => c.IsPrimaryKey) : null;
}

/// <summary>
/// What the table designer knows about the database a table is created in: its tables (for foreign keys and name
/// clashes) and the conventions most of them follow (naming style, plural names, key naming, audit columns, Unicode
/// text), learned from the cached catalog once per database.
/// </summary>
public sealed class DesignContext
{
    private const double Dominant = 0.7;
    private const int MinimumTables = 3;

    public required string ProviderKey { get; init; }
    public IReadOnlyList<ExistingTable> Tables { get; init; } = [];

    /// <summary>Every object name per schema (tables, views, routines…), for "already exists".</summary>
    public IReadOnlyList<DbObject> Objects { get; init; } = [];

    public IReadOnlyList<string> Schemas { get; init; } = [];
    public NamingStyle TableStyle { get; init; }
    public NamingStyle ColumnStyle { get; init; }

    /// <summary>True when most tables have plural names (Customers), false when most are singular, null when unclear.</summary>
    public bool? PluralTables { get; init; }

    public KeyNaming KeyNaming { get; init; }

    /// <summary>Audit columns (CreatedAt, created_at, CreatedDate…) most tables have, with a sample of each.</summary>
    public IReadOnlyList<DbColumn> CommonAuditColumns { get; init; } = [];

    /// <summary>SQL Server: most text columns are nvarchar/nchar.</summary>
    public bool PrefersUnicode { get; init; }

    /// <summary>The schema most tables are in; the designer starts there.</summary>
    public string? MainSchema { get; init; }

    public static DesignContext Empty(string providerKey) => new() { ProviderKey = providerKey };

    public ExistingTable? FindTable(string schema, string name) =>
        Tables.FirstOrDefault(t => TableDesign.Same(t.Schema, schema) && TableDesign.Same(t.Name, name));

    public bool Exists(string schema, string name) =>
        Objects.Any(o => TableDesign.Same(o.Schema, schema) && TableDesign.Same(o.Name, name) && o.Type != DbObjectType.Trigger);

    /// <param name="snapshot">The catalog of the one database the table goes in.</param>
    public static DesignContext From(MetadataSnapshot snapshot, string providerKey)
    {
        var tables = snapshot.Objects.Where(o => o.Type == DbObjectType.Table)
            .OrderBy(o => o.FullName, StringComparer.OrdinalIgnoreCase)
            .Select(t => new ExistingTable(t, snapshot.ColumnsOf(t.Database, t.Schema, t.Name).OrderBy(c => c.Ordinal).ToList()))
            .ToList();
        var columns = tables.SelectMany(t => t.Columns).ToList();

        var keyed = tables.Where(t => t.Key is not null).ToList();
        var idKeys = keyed.Count(t => Simplify(t.Key!.Name) == "id");
        var tableKeys = keyed.Count(t => RelationshipStems(t.Name).Any(s => Simplify(t.Key!.Name) == s + "id"));
        var keyNaming = keyed.Count < MinimumTables ? KeyNaming.Unknown
            : idKeys >= keyed.Count * 0.6 ? KeyNaming.Id
            : tableKeys >= keyed.Count * 0.6 ? KeyNaming.TableId
            : KeyNaming.Unknown;

        var plural = tables.Count(t => IsPlural(LastWord(t.Name)));
        var pluralTables = tables.Count < MinimumTables ? (bool?)null
            : plural >= tables.Count * Dominant ? true
            : tables.Count - plural >= tables.Count * Dominant ? false
            : null;

        var audit = tables.Count < MinimumTables ? [] : columns.Where(c => IsAuditName(c.Name))
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(c => (c.Schema, c.Table)).Distinct().Count() >= tables.Count * 0.5)
            .Select(g => g.First())
            .ToList();

        var unicode = columns.Count(c => c.BaseType.ToLowerInvariant() is "nvarchar" or "nchar" || c.DataType.StartsWith("nvarchar", StringComparison.OrdinalIgnoreCase));
        var ansi = columns.Count(c => c.BaseType.ToLowerInvariant() is "varchar" or "char" || c.DataType.StartsWith("varchar", StringComparison.OrdinalIgnoreCase));

        return new DesignContext
        {
            ProviderKey = providerKey,
            Tables = tables,
            Objects = snapshot.Objects,
            Schemas = snapshot.Objects.Select(o => o.Schema).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase).ToList(),
            TableStyle = DominantStyle(tables.Select(t => t.Name)),
            ColumnStyle = DominantStyle(columns.Select(c => c.Name)),
            PluralTables = pluralTables,
            KeyNaming = keyNaming,
            CommonAuditColumns = audit,
            PrefersUnicode = unicode > ansi,
            MainSchema = tables.GroupBy(t => t.Schema, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key
        };
    }

    // ----- Names -----

    public static NamingStyle StyleOf(string name)
    {
        if (name.Length == 0 || !name.All(c => char.IsLetterOrDigit(c) || c == '_')) return NamingStyle.Unknown;
        if (name.Contains('_')) return name.Any(char.IsUpper) ? NamingStyle.Unknown : NamingStyle.Snake;
        if (char.IsUpper(name[0])) return NamingStyle.Pascal;
        return name.All(c => !char.IsUpper(c)) ? NamingStyle.Snake : NamingStyle.Unknown;
    }

    /// <summary>Single lowercase words (orders) fit snake_case; single capitalised words (Orders) fit PascalCase.</summary>
    private static NamingStyle DominantStyle(IEnumerable<string> names)
    {
        var styles = names.Select(StyleOf).Where(s => s != NamingStyle.Unknown).ToList();
        if (styles.Count < MinimumTables) return NamingStyle.Unknown;
        var snake = styles.Count(s => s == NamingStyle.Snake);
        return snake >= styles.Count * Dominant ? NamingStyle.Snake
            : styles.Count - snake >= styles.Count * Dominant ? NamingStyle.Pascal
            : NamingStyle.Unknown;
    }

    /// <summary>The words of a name: CustomerId, customer_id and customerID → customer, id.</summary>
    public static IReadOnlyList<string> Words(string name)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (!char.IsLetterOrDigit(c))
            {
                Flush();
                continue;
            }
            var boundary = char.IsUpper(c) && current.Length > 0 &&
                           (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1])));
            if (boundary) Flush();
            current.Append(char.ToLowerInvariant(c));
        }
        Flush();
        return words;

        void Flush()
        {
            if (current.Length > 0) words.Add(current.ToString());
            current.Clear();
        }
    }

    public static string ToStyle(string name, NamingStyle style)
    {
        var words = Words(name);
        if (words.Count == 0) return name;
        return style switch
        {
            NamingStyle.Snake => string.Join("_", words),
            NamingStyle.Pascal => string.Concat(words.Select(w => w == "id" ? "Id" : char.ToUpperInvariant(w[0]) + w[1..])),
            _ => name
        };
    }

    public static string LastWord(string name) => Words(name) is { Count: > 0 } w ? w[^1] : "";

    public static bool IsPlural(string word) =>
        word.Length > 2 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal) &&
        !word.EndsWith("us", StringComparison.Ordinal) && !word.EndsWith("is", StringComparison.Ordinal) &&
        !word.EndsWith("status", StringComparison.Ordinal);

    /// <summary>Customer → Customers, Category → Categories, Box → Boxes, order_line → order_lines.</summary>
    public static string Pluralize(string name)
    {
        if (name.Length == 0) return name;
        var lower = name.ToLowerInvariant();
        var upper = char.IsUpper(name[^1]) && name.Length > 1 && char.IsUpper(name[^2]);
        string Suffix(string s) => upper ? s.ToUpperInvariant() : s;
        if (lower.EndsWith('y') && lower.Length > 1 && !"aeiou".Contains(lower[^2])) return name[..^1] + Suffix("ies");
        if (lower.EndsWith('s') || lower.EndsWith('x') || lower.EndsWith('z') || lower.EndsWith("ch", StringComparison.Ordinal) ||
            lower.EndsWith("sh", StringComparison.Ordinal))
            return name + Suffix("es");
        return name + Suffix("s");
    }

    /// <summary>Customers → Customer, Categories → Category, Boxes → Box.</summary>
    public static string Singularize(string name)
    {
        var lower = name.ToLowerInvariant();
        if (!IsPlural(LastWord(name))) return name;
        if (lower.EndsWith("ies", StringComparison.Ordinal)) return name[..^3] + (char.IsUpper(name[^1]) ? "Y" : "y");
        if (lower.EndsWith("ses", StringComparison.Ordinal) || lower.EndsWith("xes", StringComparison.Ordinal) ||
            lower.EndsWith("zes", StringComparison.Ordinal) || lower.EndsWith("ches", StringComparison.Ordinal) ||
            lower.EndsWith("shes", StringComparison.Ordinal))
            return name[..^2];
        return name[..^1];
    }

    private static IEnumerable<string> RelationshipStems(string table) => Lab.RelationshipInference.TableStems(table);

    private static bool IsAuditName(string name) => Simplify(name) is
        "createdat" or "createdon" or "createddate" or "createdtime" or "createdutc" or "created" or "datecreated" or "createdatutc" or
        "updatedat" or "updatedon" or "updateddate" or "modifiedat" or "modifiedon" or "modifieddate" or "lastmodified" or "datemodified";

    public static string Simplify(string name) => new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
