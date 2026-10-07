using System.Text;
using System.Text.RegularExpressions;
using DbExplorer.Application.Metadata;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Assistant;

/// <summary>
/// Describes the catalog to the AI assistant as compact text: table, view and routine names, column names and types,
/// primary and foreign keys. Only metadata goes in, never row data or routine bodies. Objects named in the question
/// or query come first, so they survive when a large catalog has to be cut to the size limit.
/// </summary>
public static partial class SchemaContextBuilder
{
    /// <summary>About 30k tokens: enough for a few hundred tables, and cheap to resend (the schema is cached).</summary>
    public const int DefaultMaxChars = 120_000;

    public static string Build(MetadataSnapshot snapshot, string? database, string? focusText, int maxChars = DefaultMaxChars)
    {
        var scope = database is { Length: > 0 } && snapshot.ContainsDatabase(database) ? snapshot.ForDatabase(database) : snapshot;
        var mentioned = Words(focusText);
        var objects = scope.Objects
            .Where(o => o.Type is not (DbObjectType.Trigger or DbObjectType.Other))
            .OrderBy(o => mentioned.Contains(o.Name) ? 0 : 1)
            .ThenBy(o => o.IsTableLike ? 0 : 1)
            .ThenBy(o => o.Database, StringComparer.OrdinalIgnoreCase)
            .ThenBy(o => o.Schema, StringComparer.OrdinalIgnoreCase)
            .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var multipleDatabases = scope.Databases.Count > 1;

        var sb = new StringBuilder();
        var listed = 0;
        foreach (var o in objects)
        {
            var entry = Describe(scope, o, multipleDatabases);
            if (sb.Length + entry.Length > maxChars && listed > 0) break;
            sb.Append(entry);
            listed++;
        }
        if (listed < objects.Count)
            sb.AppendLine($"-- ({objects.Count - listed:N0} more objects not listed to keep this short)");
        return sb.Length == 0 ? "-- (no tables or views are loaded for this connection)\n" : sb.ToString();
    }

    private static string Describe(MetadataSnapshot scope, DbObject o, bool withDatabase)
    {
        var name = (withDatabase ? o.Database + "." : "") + o.Schema + "." + o.Name;
        var sb = new StringBuilder();
        if (o.IsTableLike)
        {
            var columns = scope.ColumnsOf(o.Database, o.Schema, o.Name).OrderBy(c => c.Ordinal)
                .Select(c => c.Name + " " + c.DataType + (c.IsPrimaryKey ? " pk" : "") + (c.IsNullable ? " null" : ""));
            sb.Append(Kind(o.Type)).Append(' ').Append(name).Append(" (").AppendJoin(", ", columns).AppendLine(")");
            foreach (var fk in scope.ForeignKeysOf(o.Database, o.Schema, o.Name))
                sb.Append("  fk (").Append(fk.Columns).Append(") -> ").Append(fk.ReferencedSchema).Append('.')
                  .Append(fk.ReferencedTable).Append(" (").Append(fk.ReferencedColumns).AppendLine(")");
        }
        else
        {
            sb.Append(Kind(o.Type)).Append(' ').AppendLine(name);
        }
        return sb.ToString();
    }

    private static string Kind(DbObjectType type) => type switch
    {
        DbObjectType.Table => "table",
        DbObjectType.View => "view",
        DbObjectType.MaterializedView => "materialized view",
        DbObjectType.ForeignTable => "foreign table",
        DbObjectType.Procedure => "procedure",
        DbObjectType.Function or DbObjectType.ScalarFunction => "function",
        DbObjectType.TableFunction => "table function",
        DbObjectType.Sequence => "sequence",
        DbObjectType.Synonym => "synonym",
        _ => "object"
    };

    private static HashSet<string> Words(string? text) =>
        string.IsNullOrEmpty(text)
            ? []
            : WordRegex().Matches(text).Select(m => m.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"[\p{L}_][\p{L}\p{N}_$#@]*")]
    private static partial Regex WordRegex();
}
