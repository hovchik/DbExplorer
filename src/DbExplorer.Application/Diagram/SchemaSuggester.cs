using System.Text;
using DbExplorer.Application.Copy;
using DbExplorer.Application.Lab;
using DbExplorer.Application.Metadata;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Diagram;

/// <summary>A foreign key the schema lacks: inferred from names and types, or a relationship the user accepted in the
/// Lab (which DbExplorer knows but the database does not enforce).</summary>
public sealed record SuggestedForeignKey(DbForeignKey ForeignKey, string Confidence, string Reason, string? TypeWarning, bool Accepted)
{
    public string Child => $"{ForeignKey.Schema}.{ForeignKey.Table}({ForeignKey.Columns})";
    public string Parent => $"{ForeignKey.ReferencedSchema}.{ForeignKey.ReferencedTable}({ForeignKey.ReferencedColumns})";
}

/// <summary>The whole-schema diagram with the suggested foreign keys drawn in, and the script that would add them.</summary>
public sealed record SchemaSuggestions(ErDiagram Diagram, IReadOnlyList<SuggestedForeignKey> ForeignKeys, string Script);

/// <summary>
/// "Suggest improvements" for a whole-schema diagram: missing foreign keys found by <see cref="RelationshipInference"/>
/// (Orders.CustomerId → Customers.CustomerId with compatible types) plus the Lab's accepted relationships, as a diagram
/// and an ALTER TABLE ... ADD CONSTRAINT script. Nothing is run against the server.
/// </summary>
public static class SchemaSuggester
{
    public static SchemaSuggestions Suggest(
        MetadataSnapshot snapshot, string providerKey, string? database, string? schema, ErColumnMode mode, int maxTables = 80)
    {
        bool InDb(string db) => string.IsNullOrEmpty(database) || Same(db, database);
        bool InSchema(string s) => schema is null || Same(s, schema);
        bool InScope(DbForeignKey f) => InDb(f.Database) && InSchema(f.Schema) && InSchema(f.ReferencedSchema);

        var usedNames = snapshot.ForeignKeys.Where(f => !f.IsVirtual).Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dialect = SqlDialect.For(providerKey);
        var suggestions = new List<SuggestedForeignKey>();

        foreach (var accepted in snapshot.ForeignKeys.Where(f => f.IsVirtual && InScope(f)))
            suggestions.Add(new SuggestedForeignKey(accepted with { Name = ConstraintName(accepted, dialect, usedNames) },
                "accepted", "relationship you accepted in the Lab", TypeWarning(snapshot, accepted), Accepted: true));

        foreach (var inferred in RelationshipInference.Infer(snapshot))
        {
            var fk = inferred.ToForeignKey();
            if (!InScope(fk)) continue;
            fk = fk with { Name = ConstraintName(fk, dialect, usedNames) };
            suggestions.Add(new SuggestedForeignKey(fk, inferred.Confidence, inferred.Reason, TypeWarning(snapshot, fk), Accepted: false));
        }

        // Draw them like real foreign keys (so they also pull related tables together), then flag their edges.
        var augmented = new MetadataSnapshot
        {
            Objects = snapshot.Objects,
            Columns = snapshot.Columns,
            Modules = snapshot.Modules,
            ForeignKeys = snapshot.ForeignKeys.Where(f => !(f.IsVirtual && InScope(f))).Concat(suggestions.Select(s => s.ForeignKey)).ToList(),
            Indexes = snapshot.Indexes,
            RefreshedAt = snapshot.RefreshedAt,
            IsStale = snapshot.IsStale
        };
        var suggested = suggestions.Select(s => s.ForeignKey).ToHashSet(ReferenceEqualityComparer.Instance);
        var diagram = ErDiagramBuilder.WholeSchema(augmented, database, schema, mode, maxTables);
        diagram = diagram with { Edges = diagram.Edges.Select(e => suggested.Contains(e.ForeignKey) ? e with { IsSuggested = true } : e).ToList() };

        return new SchemaSuggestions(diagram, suggestions, Script(dialect, database, schema, suggestions));
    }

    public static string Script(SqlDialect dialect, string? database, string? schema, IReadOnlyList<SuggestedForeignKey> suggestions)
    {
        var scope = (string.IsNullOrEmpty(database) ? "" : database + ", ") + (schema is null ? "all schemas" : "schema " + schema);
        var sb = new StringBuilder();
        sb.Append("-- Suggested foreign keys (").Append(scope).Append("), inferred from column names and types.\n");
        if (suggestions.Count == 0)
        {
            sb.Append("-- No missing foreign keys found: every column that looks like a reference already has one.\n");
            return sb.ToString();
        }

        sb.Append("-- Nothing here has been run. Review each one first: adding a constraint fails while existing rows\n");
        sb.Append("-- point at missing parents (the Lab's Verify checks a sample).\n\n");
        if (!string.IsNullOrEmpty(database))
            sb.Append(dialect.ProviderKey == SqlDialect.SqlServerKey ? $"USE {dialect.Quote(database)};\n\n" : $"-- Run in database {database}.\n\n");

        foreach (var s in suggestions)
        {
            var fk = s.ForeignKey;
            sb.Append("-- ").Append(s.Child).Append(" -> ").Append(s.Parent).Append(": ")
              .Append(s.Accepted ? s.Reason : $"{s.Confidence} confidence, {s.Reason}").Append('\n');
            if (s.TypeWarning is not null) sb.Append("-- ").Append(s.TypeWarning).Append('\n');
            sb.Append("ALTER TABLE ").Append(dialect.Table(fk.Schema, fk.Table))
              .Append(" ADD CONSTRAINT ").Append(dialect.Quote(fk.Name))
              .Append(" FOREIGN KEY (").Append(QuoteList(dialect, fk.Columns))
              .Append(") REFERENCES ").Append(dialect.Table(fk.ReferencedSchema, fk.ReferencedTable))
              .Append(" (").Append(QuoteList(dialect, fk.ReferencedColumns)).Append(");\n\n");
        }
        return sb.ToString().TrimEnd('\n') + "\n";
    }

    /// <summary>FK_Child_Parent, or FK_Child_Column when that is taken; within the engine's identifier length.</summary>
    private static string ConstraintName(DbForeignKey fk, SqlDialect dialect, HashSet<string> used)
    {
        var columns = string.Join("_", ErDiagramBuilder.SplitColumns(fk.Columns));
        string[] candidates = [$"FK_{fk.Table}_{fk.ReferencedTable}", $"FK_{fk.Table}_{columns}", $"FK_{fk.Table}_{columns}_{fk.ReferencedTable}"];
        foreach (var candidate in candidates.Select(c => Fit(c, dialect)))
            if (used.Add(candidate)) return candidate;
        for (var i = 2; ; i++)
        {
            var candidate = Fit(candidates[0], dialect, $"_{i}");
            if (used.Add(candidate)) return candidate;
        }
    }

    private static string Fit(string name, SqlDialect dialect, string suffix = "") =>
        (name.Length + suffix.Length > dialect.MaxIdentifierLength ? name[..(dialect.MaxIdentifierLength - suffix.Length)] : name) + suffix;

    /// <summary>Engines want both sides of a foreign key to have the same type; the inference only checks the family.</summary>
    private static string? TypeWarning(MetadataSnapshot snapshot, DbForeignKey fk)
    {
        var child = ErDiagramBuilder.SplitColumns(fk.Columns).ToList();
        var parent = ErDiagramBuilder.SplitColumns(fk.ReferencedColumns).ToList();
        var childColumns = snapshot.ColumnsOf(fk.Database, fk.Schema, fk.Table).ToList();
        var parentColumns = snapshot.ColumnsOf(fk.Database, fk.ReferencedSchema, fk.ReferencedTable).ToList();
        for (var i = 0; i < Math.Min(child.Count, parent.Count); i++)
        {
            var c = childColumns.FirstOrDefault(x => Same(x.Name, child[i]));
            var p = parentColumns.FirstOrDefault(x => Same(x.Name, parent[i]));
            if (c is not null && p is not null && !Same(c.DataType, p.DataType))
                return $"Types differ ({c.Name} {c.DataType}, {p.Name} {p.DataType}): align them first.";
        }
        return null;
    }

    private static string QuoteList(SqlDialect dialect, string? columns) =>
        string.Join(", ", ErDiagramBuilder.SplitColumns(columns).Select(dialect.Quote));

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
