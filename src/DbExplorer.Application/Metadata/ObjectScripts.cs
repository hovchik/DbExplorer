using System.Text;
using System.Text.RegularExpressions;
using DbExplorer.Application.Copy;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Metadata;

/// <summary>
/// The scripts behind the Objects tab's right-click Modify, Delete and Drop: an editable ALTER / CREATE OR REPLACE /
/// CREATE OR ALTER of an object's definition, a DELETE template for a table, and the DROP TABLE statement.
/// </summary>
public static class ObjectScripts
{
    private static readonly Regex SqlServerSequenceType = new(@"\s+AS\s+\S+(?=\s+START\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SqlServerSequenceStart = new(@"\s+START\s+WITH\s+(?<n>-?\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex MariaDbSequenceEngine = new(@"\s+ENGINE\s*=\s*\w+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex CreateSequence = new(@"\GCREATE\s+SEQUENCE\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Whether Modify has a script for this object type (tables go to the Table designer instead).</summary>
    public static bool CanModify(DbObjectType type) => type is DbObjectType.View or DbObjectType.MaterializedView
        or DbObjectType.Procedure or DbObjectType.Function or DbObjectType.ScalarFunction or DbObjectType.TableFunction
        or DbObjectType.Trigger or DbObjectType.Sequence or DbObjectType.Synonym;

    /// <summary>
    /// An editable script that changes <paramref name="obj"/> in place when run: CREATE OR ALTER on SQL Server,
    /// CREATE OR REPLACE (or ALTER SEQUENCE) on PostgreSQL, and DROP … IF EXISTS then CREATE where the engine has no
    /// replace form (MySQL routines and triggers, SQL Server synonyms, PostgreSQL materialized views).
    /// <paramref name="definitions"/> holds one CREATE per overload, as the catalog stores them.
    /// </summary>
    public static string ModifyScript(string providerKey, DbObject obj, IReadOnlyList<string> definitions)
    {
        var dialect = SqlDialect.For(providerKey);
        var sb = new StringBuilder();
        sb.Append("-- Modify ").Append(Humanize(obj.Type)).Append(' ').Append(dialect.Table(obj.Schema, obj.Name));
        if (!string.IsNullOrEmpty(obj.Database)) sb.Append(" in ").Append(obj.Database);
        sb.Append(". Edit below, then run the script to apply it.\n");
        foreach (var note in Notes(providerKey, obj.Type, definitions.Count)) sb.Append("-- ").Append(note).Append('\n');

        foreach (var definition in definitions)
            sb.Append('\n').Append(dialect.AsStatement(Modify(dialect, obj, definition.Trim()))).Append(dialect.EndOfStep);
        return sb.ToString();
    }

    private static string Modify(SqlDialect dialect, DbObject obj, string definition)
    {
        if (obj.Type == DbObjectType.Sequence) return AlterSequence(dialect.ProviderKey, definition);
        var text = dialect.ToReplaceDefinition(definition, obj.Type);
        if (obj.Type == DbObjectType.Synonym && dialect.ProviderKey == SqlDialect.SqlServerKey)
            text = $"DROP SYNONYM IF EXISTS {dialect.Table(obj.Schema, obj.Name)};{dialect.EndOfStep}{text}";
        return text;
    }

    /// <summary>CREATE SEQUENCE as the catalog rebuilt it, turned into the engine's ALTER SEQUENCE with the same options.
    /// The start value is left out on SQL Server, where ALTER only knows RESTART (which would renumber).</summary>
    private static string AlterSequence(string providerKey, string definition)
    {
        var match = CreateSequence.Match(definition, 0);
        if (!match.Success) return definition;
        var rest = definition[match.Length..];
        switch (providerKey)
        {
            case SqlDialect.SqlServerKey:
                rest = SqlServerSequenceType.Replace(rest, "", 1);
                var start = SqlServerSequenceStart.Match(rest);
                if (start.Success)
                    rest = rest.Remove(start.Index, start.Length).TrimEnd().TrimEnd(';') +
                           $"\n    -- RESTART WITH {start.Groups["n"].Value}   -- uncomment to renumber from the start value";
                break;
            case SqlDialect.MySqlKey:
                rest = MariaDbSequenceEngine.Replace(rest, "");
                break;
        }
        return "ALTER SEQUENCE" + rest;
    }

    private static IEnumerable<string> Notes(string providerKey, DbObjectType type, int overloads)
    {
        if (overloads > 1) yield return $"{overloads} overloads share this name; each one is below.";
        switch (providerKey, type)
        {
            case (SqlDialect.PostgresKey, DbObjectType.MaterializedView):
                yield return "PostgreSQL cannot replace a materialized view: this drops and re-creates it, which re-reads its data and drops its indexes.";
                break;
            case (SqlDialect.PostgresKey, DbObjectType.Trigger):
                yield return "CREATE OR REPLACE TRIGGER needs PostgreSQL 14 or later.";
                break;
            case (SqlDialect.PostgresKey, DbObjectType.Function or DbObjectType.Procedure):
                yield return "Changing the parameter list creates a new overload; changing the return type needs a DROP first.";
                break;
            case (SqlDialect.MySqlKey, DbObjectType.Procedure or DbObjectType.Function or DbObjectType.Trigger):
                yield return "MySQL has no CREATE OR REPLACE here: the old one is dropped first, so grants on it go with it.";
                break;
            case (SqlDialect.SqlServerKey, DbObjectType.Synonym):
                yield return "Synonyms cannot be altered: this drops and re-creates it.";
                break;
            case (SqlDialect.SqlServerKey, _) when type != DbObjectType.Sequence:
                yield return "CREATE OR ALTER needs SQL Server 2016 SP1 or later; permissions on the object are kept.";
                break;
        }
    }

    /// <summary>A DELETE for the table with a WHERE to fill in: the primary key columns when there are any. The
    /// placeholders are not valid SQL, so running it untouched fails instead of emptying the table.</summary>
    public static string DeleteTemplate(string providerKey, DbObject table, IEnumerable<DbColumn> columns)
    {
        var dialect = SqlDialect.For(providerKey);
        var keys = columns.Where(c => c.IsPrimaryKey).OrderBy(c => c.Ordinal).ToList();
        var condition = keys.Count > 0
            ? string.Join("\n  AND ", keys.Select(k => $"{dialect.Quote(k.Name)} = <{k.Name}>"))
            : "<condition>";
        return $"-- Delete rows from {dialect.Table(table.Schema, table.Name)}" +
               (string.IsNullOrEmpty(table.Database) ? "" : $" in {table.Database}") +
               ". Replace the placeholders; without a WHERE every row goes.\n" +
               $"DELETE FROM {dialect.Table(table.Schema, table.Name)}\nWHERE {condition};\n";
    }

    /// <summary>DROP TABLE for the table, as the confirmation shows it and as it runs.</summary>
    public static string DropTable(string providerKey, DbObject table) =>
        SqlDialect.For(providerKey).DropTable(SqlDialect.For(providerKey).Table(table.Schema, table.Name));

    public static string Humanize(DbObjectType type) => type switch
    {
        DbObjectType.MaterializedView => "materialized view",
        DbObjectType.ForeignTable => "foreign table",
        DbObjectType.ScalarFunction or DbObjectType.TableFunction => "function",
        _ => type.ToString().ToLowerInvariant()
    };
}
