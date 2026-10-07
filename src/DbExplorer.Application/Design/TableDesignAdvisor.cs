using DbExplorer.Application.Copy;
using DbExplorer.Application.Lab;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Design;

public enum DesignSeverity
{
    /// <summary>The script would fail or create something else than intended; Execute waits until it is fixed.</summary>
    Error,
    Warning,
    Tip
}

/// <summary>One finding about a table design, with the change that addresses it when there is a single obvious one.</summary>
/// <param name="Key">Stable per finding (rule and column), so a dismissed tip stays dismissed while the user edits.</param>
public sealed record DesignSuggestion(
    string Key, DesignSeverity Severity, string Title, string Detail,
    string? FixLabel = null, Func<TableDesign, TableDesign>? Fix = null)
{
    public bool CanFix => Fix is not null;
}

/// <summary>
/// Reviews a table while it is being designed: what would make the CREATE TABLE fail (no name, a name taken, a
/// foreign key to nothing), likely mistakes (no primary key, a reference without its foreign key, ids and dates kept as
/// text, varchar without a length, money in float), and consistency with the rest of the database (naming style,
/// plural names, audit columns, nvarchar). Rule-based and instant: nothing is read from the server.
/// </summary>
public static class TableDesignAdvisor
{
    /// <param name="original">The table as it is on the server when the design alters it; null for a new table. Then the
    /// rules about types and names look only at new and changed columns (existing data is not second-guessed), and
    /// <see cref="TableAlterAdvisor"/> adds what the ALTER script risks.</param>
    public static IReadOnlyList<DesignSuggestion> Review(TableDesign design, DesignContext context, TableDesign? original = null)
    {
        var provider = context.ProviderKey;
        var dialect = SqlDialect.For(provider);
        var schema = TableScriptBuilder.SchemaOf(design, provider);
        var name = design.Name.Trim();
        var columns = design.Columns.Where(c => c.Name.Trim().Length > 0).ToList();
        var result = new List<DesignSuggestion>();

        var altering = original is not null;
        // When altering, an existing column nobody touched keeps its type and name: no suggestion rewrites old data.
        bool Reviewable(ColumnDesign c) => original is null || c.OriginalName is null || original.Column(c.OriginalName) is not { } before ||
                                           !TableAlterAdvisor.SameColumn(before, c, provider);
        var reviewable = columns.Where(Reviewable).ToList();

        void Add(string key, DesignSeverity severity, string title, string detail, string? fixLabel = null, Func<TableDesign, TableDesign>? fix = null) =>
            result.Add(new DesignSuggestion(key, severity, title, detail, fixLabel, fix));

        // ----- Errors: the script would not run -----

        if (name.Length == 0)
            Add("name", DesignSeverity.Error, "Name the table", "The table needs a name before it can be created.");
        else if (context.Exists(schema, name) && !(altering && TableDesign.Same(schema, TableScriptBuilder.SchemaOf(original!, provider)) && TableDesign.Same(name, original!.Name)))
            Add("exists", DesignSeverity.Error, $"{schema}.{name} already exists",
                "Pick another name: the database already has an object with this name in this schema.");
        if (name.Length > dialect.MaxIdentifierLength)
            Add("name-length", DesignSeverity.Error, "The table name is too long",
                $"Names can be at most {dialect.MaxIdentifierLength} characters here.");

        if (columns.Count == 0)
        {
            var id = KeyColumnName(design, context);
            var (type, _) = ColumnTypes.Preferred(provider, TypeFamily.Integer);
            Add("no-columns", DesignSeverity.Error, "Add columns", "A table needs at least one column.",
                $"Add {id}", d => d.AddColumn(new ColumnDesign { Name = id, Type = type, IsNullable = false, IsPrimaryKey = true, IsIdentity = true }, first: true));
        }

        foreach (var (column, i) in design.Columns.Select((c, i) => (c, i)))
        {
            if (column.Name.Trim().Length == 0 && column.Type.Trim().Length > 0)
                Add($"column-name:{i}", DesignSeverity.Error, $"Column {i + 1} has no name", "Name it, or remove the row.");
            else if (column.Name.Trim().Length > 0 && column.Type.Trim().Length == 0)
                Add($"column-type:{column.Name}", DesignSeverity.Error, $"{column.Name} has no type", "Pick a type from the list or type one.");
            if (column.Name.Trim().Length > dialect.MaxIdentifierLength)
                Add($"column-length:{column.Name}", DesignSeverity.Error, $"{Short(column.Name)} is too long",
                    $"Column names can be at most {dialect.MaxIdentifierLength} characters here.");
        }

        foreach (var group in columns.GroupBy(c => c.Name.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            Add($"duplicate:{group.Key}", DesignSeverity.Error, $"{group.Key} appears {group.Count()} times", "Column names must be unique.");

        foreach (var column in columns.Where(c => c.IsIdentity && ColumnTypes.Family(c) != TypeFamily.Integer))
        {
            var (type, _) = ColumnTypes.Preferred(provider, TypeFamily.Integer);
            Add($"identity-type:{column.Name}", DesignSeverity.Error, $"{column.Name} is an identity but not an integer",
                "Identity values are generated numbers, so the column needs an integer type.",
                $"Use {type}", d => d.WithColumn(column.Name, c => c with { Type = type, Size = null }));
        }

        if (provider is SqlDialect.SqlServerKey or SqlDialect.MySqlKey && columns.Count(c => c.IsIdentity) > 1)
            Add("identity-count", DesignSeverity.Error, "Only one identity column is allowed",
                provider == SqlDialect.MySqlKey ? "MySQL allows one AUTO_INCREMENT column per table." : "SQL Server allows one IDENTITY column per table.");

        // MySQL needs an AUTO_INCREMENT column to lead a key, or the CREATE / ALTER fails.
        if (provider == SqlDialect.MySqlKey)
            foreach (var column in columns.Where(c => c.IsIdentity && !c.IsPrimaryKey &&
                                                      !design.Indexes.Any(i => i.Columns.Count > 0 && TableDesign.Same(i.Columns[0].Trim(), c.Name.Trim()))))
                Add($"identity-key:{column.Name}", DesignSeverity.Error, $"{column.Name} is AUTO_INCREMENT but not a key",
                    "MySQL only numbers a column that is the primary key or leads an index.",
                    "Make it the primary key", d => d.WithColumn(column.Name, c => c with { IsPrimaryKey = true, IsNullable = false }));

        foreach (var fk in design.ForeignKeys)
        {
            var label = fk.Column.Length > 0 ? fk.Column : "A foreign key";
            if (fk.Column.Trim().Length == 0 || design.Column(fk.Column) is null)
                Add($"fk-column:{fk.Column}:{fk.ReferencedTable}", DesignSeverity.Error, $"{label}: pick one of the table's columns",
                    "The foreign key's column is not in the table.");
            else if (context.FindTable(fk.ReferencedSchema, fk.ReferencedTable) is not { } parent)
                Add($"fk-table:{fk.Column}", DesignSeverity.Error, $"{label}: pick the table it references",
                    fk.ReferencedTable.Length == 0 ? "No table chosen yet." : $"{fk.ReferencedSchema}.{fk.ReferencedTable} is not in this database.");
            else if (!parent.Columns.Any(c => TableDesign.Same(c.Name, fk.ReferencedColumn)))
                Add($"fk-ref-column:{fk.Column}", DesignSeverity.Error, $"{label}: pick the column it references",
                    $"{parent.FullName} has no column {fk.ReferencedColumn}.");
        }

        foreach (var index in design.Indexes)
            foreach (var missing in index.Columns.Where(c => design.Column(c) is null))
                Add($"index-column:{missing}", DesignSeverity.Error, $"The index uses {missing}, which is not a column", "Fix the index's column list.");

        // ----- Warnings: likely mistakes -----

        if (columns.Count > 0 && !design.PrimaryKey.Any())
        {
            var candidate = columns.FirstOrDefault(c => DesignContext.Simplify(c.Name) == "id") ??
                            columns.FirstOrDefault(c => RelationshipInference.TableStems(name).Any(s => DesignContext.Simplify(c.Name) == s + "id"));
            if (candidate is not null)
            {
                var identity = ColumnTypes.Family(candidate) == TypeFamily.Integer && !columns.Any(c => c.IsIdentity);
                Add("no-pk", DesignSeverity.Warning, "The table has no primary key",
                    $"Without one, rows cannot be told apart reliably and other tables cannot reference this one. {candidate.Name} looks like the key.",
                    $"Make {candidate.Name} the key", d => d.WithColumn(candidate.Name, c => c with { IsPrimaryKey = true, IsNullable = false, IsIdentity = c.IsIdentity || identity }));
            }
            else
            {
                var id = KeyColumnName(design, context);
                var (type, _) = ColumnTypes.Preferred(provider, TypeFamily.Integer);
                Add("no-pk", DesignSeverity.Warning, "The table has no primary key",
                    "Without one, rows cannot be told apart reliably and other tables cannot reference this one.",
                    $"Add {id} as the key", d => d.AddColumn(new ColumnDesign { Name = id, Type = type, IsNullable = false, IsPrimaryKey = true, IsIdentity = true }, first: true));
            }
        }

        var referenced = design.ForeignKeys.Select(f => f.Column.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var singleKey = design.PrimaryKey.Count() == 1 ? design.PrimaryKey.First() : null;
        foreach (var column in columns)
        {
            if (referenced.Contains(column.Name.Trim()) || ReferenceEquals(column, singleKey) || column.Type.Trim().Length == 0) continue;
            if (BestParent(column, name, schema, context) is not { } match) continue;
            var (parent, key, compatible) = match;
            var fk = new ForeignKeyDesign { Column = column.Name.Trim(), ReferencedSchema = parent.Schema, ReferencedTable = parent.Name, ReferencedColumn = key.Name };
            if (compatible)
            {
                var sameType = SameType(column, key);
                Add($"fk-candidate:{column.Name}", DesignSeverity.Warning, $"{column.Name} looks like a reference to {parent.FullName}",
                    $"Add a foreign key to {parent.FullName}({key.Name}) so every {column.Name} must exist there." +
                    (sameType ? "" : $" Its type becomes {key.DataType} to match."),
                    "Add foreign key", d => (sameType ? d : d.WithColumn(column.Name, c => c with { Type = key.DataType, Size = null })).AddForeignKey(fk));
            }
            else
            {
                Add($"fk-candidate:{column.Name}", DesignSeverity.Warning, $"{column.Name} looks like a reference to {parent.FullName}, but its type differs",
                    $"{column.Name} is {column.FullType} while {parent.FullName}.{key.Name} is {key.DataType}, so it cannot reference it.",
                    $"Use {key.DataType} and add foreign key", d => d.WithColumn(column.Name, c => c with { Type = key.DataType, Size = null }).AddForeignKey(fk));
            }
        }

        foreach (var fk in design.ForeignKeys)
        {
            if (design.Column(fk.Column) is not { } column ||
                context.FindTable(fk.ReferencedSchema, fk.ReferencedTable)?.Columns.FirstOrDefault(c => TableDesign.Same(c.Name, fk.ReferencedColumn)) is not { } key)
                continue;
            if (!SameType(column, key))
                Add($"fk-type:{fk.Column}", DesignSeverity.Warning, $"{fk.Column} and {fk.ReferencedTable}.{key.Name} have different types",
                    $"{column.FullType} vs {key.DataType}: foreign key columns should have the same type as the key they reference.",
                    $"Use {key.DataType}", d => d.WithColumn(column.Name, c => c with { Type = key.DataType, Size = null }));
        }

        foreach (var column in reviewable)
        {
            var family = ColumnTypes.Family(column);
            var words = DesignContext.Words(column.Name);
            var bare = column.BaseType;

            if (ColumnTypes.NeedsLength(provider, bare) && string.IsNullOrEmpty(column.EffectiveSize) && bare is not ("char" or "nchar"))
            {
                var size = SuggestedLength(words) ?? 100;
                Add($"length:{column.Name}", provider == SqlDialect.MySqlKey ? DesignSeverity.Error : DesignSeverity.Warning, $"{column.Name} has no length",
                    provider == SqlDialect.MySqlKey
                        ? $"MySQL needs a length for {bare}; the CREATE TABLE fails without one."
                        : $"A {bare} without a length holds a single character in a CREATE TABLE.",
                    $"Use {bare}({size})", d => d.WithColumn(column.Name, c => c with { Type = bare, Size = size.ToString() }));
            }

            if (provider == SqlDialect.SqlServerKey && bare is "decimal" or "numeric" && string.IsNullOrEmpty(column.EffectiveSize))
                Add($"precision:{column.Name}", DesignSeverity.Warning, $"{column.Name} keeps no decimals",
                    $"A {bare} without precision is {bare}(18,0), so 9.99 is stored as 10.",
                    $"Use {bare}(18,2)", d => d.WithColumn(column.Name, c => c with { Type = bare, Size = "18,2" }));

            if (provider == SqlDialect.SqlServerKey && bare is "text" or "ntext" or "image")
            {
                var replacement = bare switch { "text" => "varchar", "ntext" => "nvarchar", _ => "varbinary" };
                Add($"deprecated:{column.Name}", DesignSeverity.Warning, $"{bare} is deprecated",
                    $"SQL Server keeps {bare} for old databases only; {replacement}(max) stores the same and works with every string function.",
                    $"Use {replacement}(max)", d => d.WithColumn(column.Name, c => c with { Type = replacement, Size = "max" }));
            }

            if (IsIdName(column.Name) && family is TypeFamily.Text or TypeFamily.LargeText && BestParent(column, name, schema, context) is null)
            {
                var (type, _) = ColumnTypes.Preferred(provider, TypeFamily.Integer);
                Add($"id-text:{column.Name}", DesignSeverity.Warning, $"{column.Name} is stored as text",
                    "Ids are usually numbers (or GUIDs): text keys are larger, slower to join and compare by collation.",
                    $"Use {type}", d => d.WithColumn(column.Name, c => c with { Type = type, Size = null }));
            }

            if (DateTarget(words) is { } dateFamily && family is TypeFamily.Text or TypeFamily.LargeText or TypeFamily.Integer or TypeFamily.Float)
            {
                var (type, _) = ColumnTypes.Preferred(provider, dateFamily);
                Add($"date-type:{column.Name}", DesignSeverity.Warning, $"{column.Name} looks like a date but is {column.FullType}",
                    "Dates kept as text or numbers cannot be compared, sorted or validated as dates.",
                    $"Use {type}", d => d.WithColumn(column.Name, c => c with { Type = type, Size = null }));
            }

            if (IsMoney(words) && family == TypeFamily.Float)
            {
                var (type, size) = ColumnTypes.Preferred(provider, TypeFamily.Decimal);
                Add($"money:{column.Name}", DesignSeverity.Warning, $"{column.Name} holds money in {column.FullType}",
                    "Floating point cannot store most cents exactly (0.1 + 0.2 ≠ 0.3); amounts belong in an exact decimal.",
                    $"Use {type}({size})", d => d.WithColumn(column.Name, c => c with { Type = type, Size = size }));
            }

            if (IsFlag(words) && family is TypeFamily.Integer or TypeFamily.Text)
            {
                var (type, _) = ColumnTypes.Preferred(provider, TypeFamily.Boolean);
                Add($"flag:{column.Name}", DesignSeverity.Tip, $"{column.Name} looks like a yes/no flag",
                    $"A {type} column says so and allows only true or false.",
                    $"Use {type}", d => d.WithColumn(column.Name, c => c with { Type = type, Size = null }));
            }

            if (family == TypeFamily.LargeText && SuggestedLength(words) is { } length)
            {
                var type = provider == SqlDialect.SqlServerKey ? (bare is "varchar" or "text" ? "varchar" : "nvarchar") : "varchar";
                Add($"unbounded:{column.Name}", DesignSeverity.Tip, $"{column.Name} does not need unlimited length",
                    $"A bounded {type}({length}) can be indexed and keeps bad data out; unlimited text is meant for documents and notes.",
                    $"Use {type}({length})", d => d.WithColumn(column.Name, c => c with { Type = type, Size = length.ToString() }));
            }

            if (provider == SqlDialect.SqlServerKey && bare == "datetime")
                Add($"datetime:{column.Name}", DesignSeverity.Tip, $"{column.Name}: datetime2 is more precise",
                    "datetime rounds to 3 ms and starts in 1753; datetime2 is exact to 100 ns, covers every date and takes no more space.",
                    "Use datetime2", d => d.WithColumn(column.Name, c => c with { Type = "datetime2", Size = null }));

            if (provider == SqlDialect.PostgresKey && bare is "timestamp" or "timestamp without time zone")
                Add($"timestamptz:{column.Name}", DesignSeverity.Tip, $"{column.Name}: timestamptz records the moment",
                    "timestamp drops the time zone, so values from clients in different zones cannot be compared; timestamptz stores the instant.",
                    "Use timestamptz", d => d.WithColumn(column.Name, c => c with { Type = "timestamptz", Size = null }));
        }

        foreach (var fk in design.ForeignKeys.Where(f => design.Column(f.Column) is not null))
        {
            var leadsIndex = design.Indexes.Any(i => i.Columns.Count > 0 && TableDesign.Same(i.Columns[0].Trim(), fk.Column.Trim())) ||
                             (design.PrimaryKey.FirstOrDefault() is { } first && TableDesign.Same(first.Name.Trim(), fk.Column.Trim()));
            if (leadsIndex) continue;
            var column = fk.Column.Trim();
            Add($"fk-index:{column}", DesignSeverity.Tip, $"Index {column}",
                $"Neither engine indexes foreign keys by itself. Without one, joins on {column} and deletes in {fk.ReferencedTable} scan this whole table.",
                "Add index", d => d.AddIndex(new IndexDesign { Columns = [column] }));
        }

        // ----- Tips: consistency with the rest of the database -----

        if (!context.Schemas.Contains(schema, StringComparer.OrdinalIgnoreCase) && context.Tables.Count > 0)
            Add("new-schema", DesignSeverity.Tip, $"Schema {schema} is new", "The script creates it first.");

        if (!altering && name.Length > 0 && context.TableStyle != NamingStyle.Unknown && DesignContext.StyleOf(name) != context.TableStyle && DesignContext.ToStyle(name, context.TableStyle) is var styled && styled != name)
            Add("table-style", DesignSeverity.Tip, $"Other tables use {StyleName(context.TableStyle)}",
                $"Most table names here look like {Example(context.TableStyle)}.",
                $"Rename to {styled}", d => d with { Name = styled });

        if (!altering && name.Length > 0 && context.PluralTables is { } plural && DesignContext.IsPlural(DesignContext.LastWord(name)) != plural)
        {
            var renamed = plural ? DesignContext.Pluralize(name) : DesignContext.Singularize(name);
            if (renamed != name && !context.Exists(schema, renamed))
                Add("table-plural", DesignSeverity.Tip, plural ? "Other table names are plural" : "Other table names are singular",
                    plural ? "Most tables here are named for their rows in the plural (Customers)." : "Most tables here are named in the singular (Customer).",
                    $"Rename to {renamed}", d => d with { Name = renamed });
        }

        if (context.ColumnStyle != NamingStyle.Unknown)
        {
            var off = reviewable.Where(c => DesignContext.StyleOf(c.Name.Trim()) != context.ColumnStyle &&
                                         DesignContext.ToStyle(c.Name.Trim(), context.ColumnStyle) != c.Name.Trim()).ToList();
            if (off.Count > 0)
            {
                var style = context.ColumnStyle;
                Add("column-style", DesignSeverity.Tip,
                    off.Count == 1 ? $"{off[0].Name} does not follow {StyleName(style)}" : $"{off.Count} columns do not follow {StyleName(style)}",
                    $"Most column names here look like {Example(style, column: true)}: {string.Join(", ", off.Take(4).Select(c => c.Name.Trim()))}{(off.Count > 4 ? "…" : "")}.",
                    off.Count == 1 ? $"Rename to {DesignContext.ToStyle(off[0].Name.Trim(), style)}" : "Rename them",
                    d => off.Aggregate(d, (acc, c) => acc.RenameColumn(c.Name.Trim(), DesignContext.ToStyle(c.Name.Trim(), style))));
            }
        }

        foreach (var audit in context.CommonAuditColumns)
        {
            var simplified = DesignContext.Simplify(audit.Name);
            var kind = simplified.Contains("creat") ? "creat" : "modif";
            if (columns.Any(c => DesignContext.Simplify(c.Name).Contains(kind) || (kind == "modif" && DesignContext.Simplify(c.Name).Contains("updat")))) continue;
            var type = audit.DataType.Length > 0 ? audit.DataType : ColumnTypes.Preferred(provider, TypeFamily.DateTime).Type;
            var now = ColumnTypes.NowExpression(provider, new ColumnDesign { Type = type }.BaseType);
            Add($"audit:{audit.Name}", DesignSeverity.Tip, $"Most tables have {audit.Name}",
                $"Add it here too ({type}, filled with the current time) so every table records when its rows were {(kind == "creat" ? "created" : "changed")}.",
                $"Add {audit.Name}", d => d.AddColumn(new ColumnDesign { Name = audit.Name, Type = type, IsNullable = false, Default = now }));
        }

        if (provider == SqlDialect.SqlServerKey && context.PrefersUnicode)
        {
            var ansi = reviewable.Where(c => c.BaseType is "varchar" or "char").ToList();
            if (ansi.Count > 0)
                Add("unicode", DesignSeverity.Tip, "Other tables store text as nvarchar",
                    $"{string.Join(", ", ansi.Select(c => c.Name.Trim()))} {(ansi.Count == 1 ? "is" : "are")} varchar, which cannot hold every character (Ä, 中, emoji) on most collations.",
                    "Use nvarchar", d => ansi.Aggregate(d, (acc, c) => acc.WithColumn(c.Name, x => x with { Type = "n" + x.BaseType })));
        }

        var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (original is not null) existingNames.UnionWith(original.Columns.Select(c => c.Name).Append(original.Name));
        foreach (var reserved in new[] { name }.Concat(columns.Select(c => c.Name.Trim())).Where(n => ReservedWords.Contains(n) && !existingNames.Contains(n)).Distinct(StringComparer.OrdinalIgnoreCase))
            Add($"reserved:{reserved}", DesignSeverity.Tip, $"{reserved} is a reserved word",
                "It works here because the script quotes every name, but every hand-written query will have to quote it too.");

        if (original is not null) result.AddRange(TableAlterAdvisor.Review(original, design, context));

        return result
            .GroupBy(s => s.Key).Select(g => g.First())
            .OrderBy(s => s.Severity)
            .ToList();
    }

    /// <summary>The key column name the database's convention calls for: Id / id, or CustomerId / customer_id.</summary>
    public static string KeyColumnName(TableDesign design, DesignContext context)
    {
        var snake = context.ColumnStyle == NamingStyle.Snake || (context.ColumnStyle == NamingStyle.Unknown && context.ProviderKey != SqlDialect.SqlServerKey);
        if (context.KeyNaming == KeyNaming.TableId && design.Name.Trim().Length > 0)
        {
            var singular = DesignContext.Singularize(design.Name.Trim());
            return DesignContext.ToStyle(singular + "_id", snake ? NamingStyle.Snake : NamingStyle.Pascal);
        }
        return snake ? "id" : "Id";
    }

    /// <summary>The existing table a column most likely refers to, by name (Orders.CustomerId → Customers.CustomerId).</summary>
    private static (ExistingTable Parent, DbColumn Key, bool Compatible)? BestParent(ColumnDesign column, string table, string schema, DesignContext context)
    {
        (ExistingTable, DbColumn, bool)? best = null;
        var bestScore = 0;
        var probe = new DbColumn { Name = column.Name.Trim(), BaseType = column.BaseType, DataType = column.FullType };
        foreach (var parent in context.Tables)
        {
            if (parent.Key is not { } key || (TableDesign.Same(parent.Name, table) && TableDesign.Same(parent.Schema, schema))) continue;
            var (score, _) = RelationshipInference.NameScore(column.Name.Trim(), parent.Name, key.Name, primaryKey: true);
            if (score < 70) continue;
            if (!TableDesign.Same(parent.Schema, schema)) score -= 5;
            var compatible = RelationshipInference.CompatibleTypes(probe, Normalized(key));
            if (compatible) score += 1;
            if (score > bestScore)
            {
                bestScore = score;
                best = (parent, key, compatible);
            }
        }
        return best;
    }

    /// <summary>Catalog base types can carry their size (nvarchar(100)); the type families compare the bare name.</summary>
    private static DbColumn Normalized(DbColumn column)
    {
        var type = string.IsNullOrEmpty(column.BaseType) ? column.DataType : column.BaseType;
        var open = type.IndexOf('(');
        return column with { BaseType = open < 0 ? type : type[..open] };
    }

    private static bool SameType(ColumnDesign column, DbColumn key) =>
        string.Equals(Compact(column.FullType), Compact(key.DataType), StringComparison.OrdinalIgnoreCase);

    private static string Compact(string type) => new(type.Where(c => !char.IsWhiteSpace(c)).ToArray());

    /// <summary>OrderId, order_id, ID, customerID; not Paid, Guid or UUID.</summary>
    public static bool IsIdName(string name)
    {
        name = name.Trim();
        if (string.Equals(name, "id", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.EndsWith("_id", StringComparison.OrdinalIgnoreCase)) return true;
        return name.Length > 2 && (name.EndsWith("Id", StringComparison.Ordinal) || name.EndsWith("ID", StringComparison.Ordinal)) &&
               (char.IsLower(name[^3]) || char.IsDigit(name[^3]));
    }

    private static TypeFamily? DateTarget(IReadOnlyList<string> words)
    {
        if (words.Count == 0 || IsFlag(words)) return null;
        var last = words[^1];
        if (last is "date" or "dob" or "birthday" or "birthdate") return TypeFamily.Date;
        if (last is "at" or "on" or "time" or "timestamp" or "datetime" && words.Count > 1) return TypeFamily.DateTime;
        if (words.Count == 1 && last is "created" or "updated" or "modified") return TypeFamily.DateTime;
        return null;
    }

    private static bool IsMoney(IReadOnlyList<string> words) =>
        words.Any(w => w is "price" or "amount" or "cost" or "total" or "subtotal" or "balance" or "salary" or "fee" or "tax" or "vat" or "discount");

    private static bool IsFlag(IReadOnlyList<string> words) =>
        (words.Count > 1 && words[0] is "is" or "has" or "can" or "should" or "allow" or "allows") ||
        (words.Count > 0 && words[^1] == "flag") ||
        (words.Count == 1 && words[0] is "active" or "enabled" or "disabled" or "deleted" or "archived" or "verified" or "visible");

    /// <summary>A sensible bound for short values recognised by name; null for anything else.</summary>
    private static int? SuggestedLength(IReadOnlyList<string> words) => words.Count == 0 ? null : words[^1] switch
    {
        "email" or "mail" => 320,
        "phone" or "mobile" or "fax" => 30,
        "code" or "sku" or "status" or "type" or "kind" => 50,
        "zip" or "postcode" or "postal" or "language" or "locale" or "culture" => 20,
        "currency" => 3,
        "name" or "username" or "login" or "city" or "country" or "region" or "state" => 100,
        "title" or "subject" or "slug" => 200,
        "url" or "uri" or "website" => 2000,
        _ => null
    };

    private static string StyleName(NamingStyle style) => style == NamingStyle.Snake ? "snake_case" : "PascalCase";

    private static string Example(NamingStyle style, bool column = false) => (style, column) switch
    {
        (NamingStyle.Snake, false) => "order_lines",
        (NamingStyle.Snake, true) => "customer_id",
        (_, false) => "OrderLines",
        _ => "CustomerId"
    };

    private static string Short(string name) => name.Length <= 30 ? name : name[..27] + "…";

    /// <summary>Words both engines reserve that people often pick as names.</summary>
    private static readonly HashSet<string> ReservedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "user", "order", "group", "table", "index", "key", "select", "from", "where", "desc", "asc", "check", "column",
        "default", "primary", "foreign", "references", "end", "transaction", "view", "procedure", "function", "case",
        "when", "then", "else", "grant", "constraint", "union", "join", "limit", "offset", "authorization", "current_user"
    };
}
