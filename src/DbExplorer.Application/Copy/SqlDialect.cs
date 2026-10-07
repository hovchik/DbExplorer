using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Copy;

/// <summary>
/// The SQL differences between the supported engines that copying needs: quoting, literals, batches,
/// transactions, identity handling and DDL. Keyed by provider key so the application layer does not
/// depend on the provider assemblies.
/// </summary>
public abstract class SqlDialect
{
    public const string SqlServerKey = "SqlServer";
    public const string PostgresKey = "Postgres";
    public const string MySqlKey = "MySql";

    public static readonly SqlDialect SqlServer = new SqlServerDialect();
    public static readonly SqlDialect Postgres = new PostgresDialect();
    public static readonly SqlDialect MySql = new MySqlDialect();

    public static SqlDialect For(string providerKey) => providerKey switch
    {
        SqlServerKey => SqlServer,
        MySqlKey => MySql,
        _ => Postgres
    };

    /// <summary>"SQL Server", "PostgreSQL" or "MySQL", for messages.</summary>
    public static string EngineName(string providerKey) => providerKey switch
    {
        SqlServerKey => "SQL Server",
        MySqlKey => "MySQL",
        _ => "PostgreSQL"
    };

    /// <summary>A MySQL schema is the database itself: objects always live in the schema named after their database.</summary>
    public static bool SchemaIsDatabase(string providerKey) => providerKey == MySqlKey;

    public abstract string ProviderKey { get; }

    /// <summary>Longest identifier the engine accepts.</summary>
    public abstract int MaxIdentifierLength { get; }

    public abstract string Quote(string identifier);

    public string Table(string schema, string name) => Quote(schema) + "." + Quote(name);

    /// <summary>Creates an empty database with the server's defaults (must run outside a transaction).</summary>
    public string CreateDatabase(string name) => $"CREATE DATABASE {Quote(name)};";

    /// <summary>Ends a step. SQL Server splits scripts on GO, so DDL that must start a batch (CREATE VIEW, ...) gets one.</summary>
    public abstract string EndOfStep { get; }

    public abstract string BeginTransaction { get; }
    public abstract string CommitTransaction { get; }

    public abstract string SelectTop(string columns, string from, string? where, string? orderBy, int limit);

    public abstract string CountRows(string from, string? where);

    public abstract string CreateSchemaIfMissing(string schema);

    public abstract string CopyTableAs(string sourceTable, string schema, string newName);

    public abstract string DropTable(string table);

    public abstract string Truncate(string table);

    public abstract string AddColumn(string table, string column, string type, string? defaultExpression = null);

    public abstract string IdentityClause { get; }

    /// <summary>What goes between the column list and VALUES when explicit identity values are inserted.</summary>
    public abstract string InsertIdentityOverride { get; }

    /// <summary>Statements around inserts that carry explicit values for an identity column.</summary>
    public abstract (string Before, string After) IdentityInsertScope(string table);

    /// <summary>Moves the identity/serial sequence past the copied values; empty when the engine does it by itself.</summary>
    public abstract string ResetSequence(string schema, string name, string column);

    /// <summary>Expression that reads a column in a form <see cref="Literal"/> can write back.</summary>
    public abstract string SelectExpression(DbColumn column);

    public abstract string Literal(object? value, string? targetBaseType = null);

    /// <summary>Turns a CREATE statement of a view/routine/trigger into one that replaces an existing object.</summary>
    public abstract string ToReplaceDefinition(string definition, DbObjectType type);

    /// <summary>Makes a stored definition runnable on its own as a step.</summary>
    public abstract string AsStatement(string definition);

    /// <summary>Is the target column filled by the server only (never inserted or updated)?</summary>
    public abstract bool IsReadOnlyColumn(DbColumn column);

    public string TruncateIdentifier(string name) => name.Length <= MaxIdentifierLength ? name : name[..MaxIdentifierLength];

    protected static string Invariant(IFormattable value, string? format = null) => value.ToString(format, CultureInfo.InvariantCulture);

    protected static string Hex(byte[] bytes) => Convert.ToHexString(bytes);

    /// <summary>Skips leading whitespace and comments, returning where the first real token starts.</summary>
    protected static int FirstTokenIndex(string sql)
    {
        var i = 0;
        while (i < sql.Length)
        {
            if (char.IsWhiteSpace(sql[i])) { i++; continue; }
            if (sql.AsSpan(i).StartsWith("--"))
            {
                var end = sql.IndexOf('\n', i);
                i = end < 0 ? sql.Length : end + 1;
                continue;
            }
            if (sql.AsSpan(i).StartsWith("/*"))
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + 2;
                continue;
            }
            break;
        }
        return i;
    }

    private sealed class SqlServerDialect : SqlDialect
    {
        private static readonly Regex CreateKeyword = new(
            @"\GCREATE(\s+OR\s+ALTER)?(?=\s+(VIEW|PROC|PROCEDURE|FUNCTION|TRIGGER)\b)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public override string ProviderKey => SqlServerKey;
        public override int MaxIdentifierLength => 128;
        public override string Quote(string identifier) => "[" + identifier.Replace("]", "]]") + "]";
        public override string EndOfStep => "\nGO\n";
        public override string BeginTransaction => "SET XACT_ABORT ON;\nBEGIN TRANSACTION;\nGO\n";
        public override string CommitTransaction => "COMMIT TRANSACTION;\nGO\n";

        public override string SelectTop(string columns, string from, string? where, string? orderBy, int limit) =>
            $"SELECT TOP ({limit}) {columns} FROM {from}" +
            (string.IsNullOrWhiteSpace(where) ? "" : $" WHERE {where}") +
            (string.IsNullOrWhiteSpace(orderBy) ? "" : $" ORDER BY {orderBy}") + ";";

        public override string CountRows(string from, string? where) =>
            $"SELECT COUNT_BIG(*) FROM {from}" + (string.IsNullOrWhiteSpace(where) ? "" : $" WHERE {where}") + ";";

        public override string CreateSchemaIfMissing(string schema) =>
            $"IF SCHEMA_ID({Literal(schema)}) IS NULL EXEC(N'CREATE SCHEMA {Quote(schema).Replace("'", "''")}');";

        public override string CopyTableAs(string sourceTable, string schema, string newName) =>
            $"SELECT * INTO {Table(schema, newName)} FROM {sourceTable};";

        public override string DropTable(string table) => $"DROP TABLE {table};";
        public override string Truncate(string table) => $"TRUNCATE TABLE {table};";
        public override string AddColumn(string table, string column, string type, string? defaultExpression = null) =>
            $"ALTER TABLE {table} ADD {Quote(column)} {type} NULL{(defaultExpression is null ? "" : " DEFAULT " + defaultExpression)};";
        public override string IdentityClause => " IDENTITY(1,1)";
        public override string InsertIdentityOverride => "";

        public override (string Before, string After) IdentityInsertScope(string table) =>
            ($"SET IDENTITY_INSERT {table} ON;\n", $"SET IDENTITY_INSERT {table} OFF;\n");

        // Explicit values above the current seed move it forward automatically.
        public override string ResetSequence(string schema, string name, string column) => "";

        public override string SelectExpression(DbColumn column) =>
            column.BaseType.ToLowerInvariant() is "geography" or "geometry" or "hierarchyid"
                ? $"{Quote(column.Name)}.ToString() AS {Quote(column.Name)}"
                : Quote(column.Name);

        public override bool IsReadOnlyColumn(DbColumn column) =>
            column.IsComputed || string.Equals(column.BaseType, "timestamp", StringComparison.OrdinalIgnoreCase);

        public override string Literal(object? value, string? targetBaseType = null)
        {
            var target = targetBaseType?.ToLowerInvariant();
            return value switch
            {
                null or DBNull => "NULL",
                bool b => b ? "1" : "0",
                string s => "N'" + s.Replace("'", "''") + "'",
                char c => "N'" + (c == '\'' ? "''" : c.ToString()) + "'",
                byte[] bytes => "0x" + Hex(bytes),
                Guid g => "'" + g.ToString("D") + "'",
                double d when double.IsNaN(d) || double.IsInfinity(d) =>
                    throw new InvalidOperationException($"SQL Server cannot store the floating point value {d}."),
                float f when float.IsNaN(f) || float.IsInfinity(f) =>
                    throw new InvalidOperationException($"SQL Server cannot store the floating point value {f}."),
                double d => Invariant(d, "R"),
                float f => Invariant(f, "R"),
                decimal m => Invariant(m),
                sbyte or byte or short or ushort or int or uint or long or ulong => Invariant((IFormattable)value),
                DateTime dt => "'" + FormatDateTime(dt, target) + "'",
                DateTimeOffset dto => target is "datetimeoffset" or null
                    ? "'" + Invariant(dto, "yyyy-MM-ddTHH:mm:ss.fffffffzzz") + "'"
                    : "'" + FormatDateTime(dto.UtcDateTime, target) + "'",
                DateOnly d => "'" + Invariant(d, "yyyy-MM-dd") + "'",
                TimeOnly t => "'" + Invariant(t, "HH:mm:ss.fffffff") + "'",
                TimeSpan ts => "'" + FormatTimeSpan(ts) + "'",
                IEnumerable items => "N'" + PostgresArrayText(items).Replace("'", "''") + "'",
                _ => "N'" + (Convert.ToString(value, CultureInfo.InvariantCulture) ?? "").Replace("'", "''") + "'"
            };
        }

        private static string FormatDateTime(DateTime dt, string? target) => target switch
        {
            "date" => Invariant(dt, "yyyy-MM-dd"),
            "datetime" or "smalldatetime" => Invariant(dt, "yyyy-MM-ddTHH:mm:ss.fff"),
            "datetimeoffset" when dt.Kind == DateTimeKind.Utc => Invariant(dt, "yyyy-MM-ddTHH:mm:ss.fffffff") + "+00:00",
            _ => Invariant(dt, "yyyy-MM-ddTHH:mm:ss.fffffff")
        };

        public override string ToReplaceDefinition(string definition, DbObjectType type)
        {
            if (type is DbObjectType.Synonym or DbObjectType.Sequence) return definition;
            var start = FirstTokenIndex(definition);
            var match = CreateKeyword.Match(definition, start);
            return match.Success
                ? definition[..start] + "CREATE OR ALTER" + definition[(start + match.Length)..]
                : definition;
        }

        public override string AsStatement(string definition) => definition.TrimEnd();
    }

    private sealed class PostgresDialect : SqlDialect
    {
        private static readonly HashSet<string> NativeTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "int2", "int4", "int8", "numeric", "float4", "float8", "bool", "text", "varchar", "bpchar", "char", "name",
            "date", "time", "timestamp", "timestamptz", "interval", "bytea", "uuid", "json", "jsonb", "xml", "money", "oid"
        };

        private static readonly Regex CreateTrigger = new(@"\GCREATE(\s+CONSTRAINT)?\s+TRIGGER\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex CreateMaterializedView = new(@"\GCREATE\s+MATERIALIZED\s+VIEW\s+(?<name>\S+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public override string ProviderKey => PostgresKey;
        public override int MaxIdentifierLength => 63;
        public override string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
        public override string EndOfStep => "\n";
        public override string BeginTransaction => "BEGIN;\n";
        public override string CommitTransaction => "COMMIT;\n";

        public override string SelectTop(string columns, string from, string? where, string? orderBy, int limit) =>
            $"SELECT {columns} FROM {from}" +
            (string.IsNullOrWhiteSpace(where) ? "" : $" WHERE {where}") +
            (string.IsNullOrWhiteSpace(orderBy) ? "" : $" ORDER BY {orderBy}") + $" LIMIT {limit};";

        public override string CountRows(string from, string? where) =>
            $"SELECT COUNT(*) FROM {from}" + (string.IsNullOrWhiteSpace(where) ? "" : $" WHERE {where}") + ";";

        public override string CreateSchemaIfMissing(string schema) => $"CREATE SCHEMA IF NOT EXISTS {Quote(schema)};";
        public override string CopyTableAs(string sourceTable, string schema, string newName) => $"CREATE TABLE {Table(schema, newName)} AS TABLE {sourceTable};";
        public override string DropTable(string table) => $"DROP TABLE {table};";
        public override string Truncate(string table) => $"TRUNCATE TABLE {table};";
        public override string AddColumn(string table, string column, string type, string? defaultExpression = null) =>
            $"ALTER TABLE {table} ADD COLUMN {Quote(column)} {type} NULL{(defaultExpression is null ? "" : " DEFAULT " + defaultExpression)};";
        public override string IdentityClause => " GENERATED BY DEFAULT AS IDENTITY";
        public override string InsertIdentityOverride => " OVERRIDING SYSTEM VALUE";
        public override (string Before, string After) IdentityInsertScope(string table) => ("", "");

        public override string ResetSequence(string schema, string name, string column) =>
            // pg_get_serial_sequence returns NULL for a column without a sequence, and setval(NULL, ...) is a no-op.
            $"SELECT setval(pg_get_serial_sequence({Literal(Table(schema, name))}, {Literal(column)}), " +
            $"COALESCE((SELECT MAX({Quote(column)}) FROM {Table(schema, name)}), 0) + 1, false);";

        public override string SelectExpression(DbColumn column)
        {
            var type = column.BaseType;
            var native = NativeTypes.Contains(type) || (type.StartsWith('_') && NativeTypes.Contains(type[1..]));
            // Enums, domains over unknown types and extension types (citext, geometry, ...) travel as text.
            return native ? Quote(column.Name) : $"{Quote(column.Name)}::text AS {Quote(column.Name)}";
        }

        public override bool IsReadOnlyColumn(DbColumn column) => column.IsComputed;

        public override string Literal(object? value, string? targetBaseType = null)
        {
            var target = targetBaseType?.ToLowerInvariant();
            return value switch
            {
                null or DBNull => "NULL",
                bool b => b ? "TRUE" : "FALSE",
                string s => Text(s),
                char c => Text(c.ToString()),
                byte[] bytes => "'\\x" + Hex(bytes) + "'",
                Guid g => "'" + g.ToString("D") + "'",
                double d when double.IsNaN(d) || double.IsInfinity(d) => Text(Invariant(d)),
                float f when float.IsNaN(f) || float.IsInfinity(f) => Text(Invariant(f)),
                double d => Invariant(d, "R"),
                float f => Invariant(f, "R"),
                decimal m => Invariant(m),
                sbyte or byte or short or ushort or int or uint or long or ulong => Invariant((IFormattable)value),
                DateTime dt => "'" + (target == "date"
                    ? Invariant(dt, "yyyy-MM-dd")
                    : Invariant(dt, "yyyy-MM-dd HH:mm:ss.ffffff") + (dt.Kind == DateTimeKind.Utc ? "+00" : "")) + "'",
                DateTimeOffset dto => "'" + Invariant(dto, "yyyy-MM-dd HH:mm:ss.ffffffzzz") + "'",
                DateOnly d => "'" + Invariant(d, "yyyy-MM-dd") + "'",
                TimeOnly t => "'" + Invariant(t, "HH:mm:ss.ffffff") + "'",
                TimeSpan ts => "'" + FormatTimeSpan(ts) + "'",
                IEnumerable items => Text(PostgresArrayText(items)),
                _ => Text(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "")
            };
        }

        private static string Text(string s) => "'" + s.Replace("'", "''") + "'";

        public override string ToReplaceDefinition(string definition, DbObjectType type)
        {
            var start = FirstTokenIndex(definition);
            if (type == DbObjectType.Trigger)
            {
                // CREATE OR REPLACE TRIGGER needs PostgreSQL 14 or later.
                var match = CreateTrigger.Match(definition, start);
                return match.Success && !match.Groups[1].Success
                    ? definition[..start] + "CREATE OR REPLACE TRIGGER" + definition[(start + match.Length)..]
                    : definition;
            }

            if (type == DbObjectType.MaterializedView)
            {
                var match = CreateMaterializedView.Match(definition, start);
                return match.Success
                    ? $"DROP MATERIALIZED VIEW IF EXISTS {match.Groups["name"].Value};\n{definition}"
                    : definition;
            }

            // Functions (pg_get_functiondef) and views are stored as CREATE OR REPLACE already.
            return definition;
        }

        public override string AsStatement(string definition)
        {
            var trimmed = definition.TrimEnd();
            return trimmed.EndsWith(';') ? trimmed : trimmed + ";";
        }
    }

    private sealed class MySqlDialect : SqlDialect
    {
        private static readonly Regex CreateRoutine = new(
            @"\GCREATE\s+(?:OR\s+REPLACE\s+)?(?:DEFINER\s*=\s*(?:`[^`]*`|'[^']*'|[^\s@]+)(?:@(?:`[^`]*`|'[^']*'|\S+))?\s+)?" +
            @"(?:SQL\s+SECURITY\s+\w+\s+)?(?<kind>PROCEDURE|FUNCTION|TRIGGER|EVENT)\s+(?:IF\s+NOT\s+EXISTS\s+)?" +
            @"(?<name>(?:`(?:[^`]|``)*`|[\w$]+)(?:\s*\.\s*(?:`(?:[^`]|``)*`|[\w$]+))?)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex CreateView = new(@"\GCREATE(?!\s+OR\s+REPLACE)(?=(\s+(ALGORITHM|DEFINER|SQL)\b[^\n]*?)?\s+VIEW\b)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly HashSet<string> SpatialTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "geometry", "point", "linestring", "polygon", "multipoint", "multilinestring", "multipolygon",
            "geometrycollection", "geomcollection"
        };

        public override string ProviderKey => MySqlKey;
        public override int MaxIdentifierLength => 64;
        public override string Quote(string identifier) => "`" + identifier.Replace("`", "``") + "`";
        public override string EndOfStep => "\n";
        public override string BeginTransaction => "START TRANSACTION;\n";
        public override string CommitTransaction => "COMMIT;\n";

        public override string SelectTop(string columns, string from, string? where, string? orderBy, int limit) =>
            $"SELECT {columns} FROM {from}" +
            (string.IsNullOrWhiteSpace(where) ? "" : $" WHERE {where}") +
            (string.IsNullOrWhiteSpace(orderBy) ? "" : $" ORDER BY {orderBy}") + $" LIMIT {limit};";

        public override string CountRows(string from, string? where) =>
            $"SELECT COUNT(*) FROM {from}" + (string.IsNullOrWhiteSpace(where) ? "" : $" WHERE {where}") + ";";

        public override string CreateSchemaIfMissing(string schema) => $"CREATE DATABASE IF NOT EXISTS {Quote(schema)};";
        public override string CopyTableAs(string sourceTable, string schema, string newName) =>
            $"CREATE TABLE {Table(schema, newName)} AS SELECT * FROM {sourceTable};";
        public override string DropTable(string table) => $"DROP TABLE {table};";
        public override string Truncate(string table) => $"TRUNCATE TABLE {table};";
        public override string AddColumn(string table, string column, string type, string? defaultExpression = null) =>
            $"ALTER TABLE {table} ADD COLUMN {Quote(column)} {type} NULL{(defaultExpression is null ? "" : " DEFAULT " + defaultExpression)};";
        public override string IdentityClause => " AUTO_INCREMENT";
        public override string InsertIdentityOverride => "";
        public override (string Before, string After) IdentityInsertScope(string table) => ("", "");

        // Explicit values above the counter move AUTO_INCREMENT forward by themselves.
        public override string ResetSequence(string schema, string name, string column) => "";

        public override string SelectExpression(DbColumn column) =>
            SpatialTypes.Contains(column.BaseType)
                ? $"ST_AsText({Quote(column.Name)}) AS {Quote(column.Name)}"
                : Quote(column.Name);

        public override bool IsReadOnlyColumn(DbColumn column) => column.IsComputed;

        public override string Literal(object? value, string? targetBaseType = null)
        {
            var target = targetBaseType?.ToLowerInvariant();
            return value switch
            {
                null or DBNull => "NULL",
                bool b => b ? "TRUE" : "FALSE",
                string s => Text(s),
                char c => Text(c.ToString()),
                byte[] bytes => "X'" + Hex(bytes) + "'",
                Guid g => "'" + g.ToString("D") + "'",
                double d when double.IsNaN(d) || double.IsInfinity(d) =>
                    throw new InvalidOperationException($"MySQL cannot store the floating point value {d}."),
                float f when float.IsNaN(f) || float.IsInfinity(f) =>
                    throw new InvalidOperationException($"MySQL cannot store the floating point value {f}."),
                double d => Invariant(d, "R"),
                float f => Invariant(f, "R"),
                decimal m => Invariant(m),
                sbyte or byte or short or ushort or int or uint or long or ulong => Invariant((IFormattable)value),
                DateTime dt => "'" + (target == "date" ? Invariant(dt, "yyyy-MM-dd") : Invariant(dt, "yyyy-MM-dd HH:mm:ss.ffffff")) + "'",
                // No time zone type: the instant is stored as UTC.
                DateTimeOffset dto => "'" + Invariant(dto.UtcDateTime, "yyyy-MM-dd HH:mm:ss.ffffff") + "'",
                DateOnly d => "'" + Invariant(d, "yyyy-MM-dd") + "'",
                TimeOnly t => "'" + Invariant(t, "HH:mm:ss.ffffff") + "'",
                TimeSpan ts => "'" + FormatTimeSpan(ts) + "'",
                IEnumerable items => Text(PostgresArrayText(items)),
                _ => Text(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "")
            };
        }

        // Backslashes are escapes unless NO_BACKSLASH_ESCAPES is set; doubled, they read the same either way.
        private static string Text(string s) => "'" + s.Replace("\\", "\\\\").Replace("'", "''") + "'";

        public override string ToReplaceDefinition(string definition, DbObjectType type)
        {
            var start = FirstTokenIndex(definition);
            if (type is DbObjectType.View)
            {
                var view = CreateView.Match(definition, start);
                return view.Success ? definition[..start] + "CREATE OR REPLACE" + definition[(start + view.Length)..] : definition;
            }

            // MySQL has no CREATE OR REPLACE for routines and triggers: drop the old one first.
            var match = CreateRoutine.Match(definition, start);
            return match.Success
                ? $"DROP {match.Groups["kind"].Value.ToUpperInvariant()} IF EXISTS {match.Groups["name"].Value};\n{definition}"
                : definition;
        }

        // The server parses BEGIN … END bodies itself, so a definition runs as one statement.
        public override string AsStatement(string definition)
        {
            var trimmed = definition.TrimEnd();
            return trimmed.EndsWith(';') ? trimmed : trimmed + ";";
        }
    }

    private static string FormatTimeSpan(TimeSpan ts)
    {
        var sign = ts < TimeSpan.Zero ? "-" : "";
        ts = ts.Duration();
        var fraction = ts.Ticks % TimeSpan.TicksPerSecond;
        return $"{sign}{(long)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}" +
               (fraction == 0 ? "" : "." + fraction.ToString("0000000", CultureInfo.InvariantCulture));
    }

    /// <summary>PostgreSQL array text form, e.g. {1,2,"a b"}; also used to store arrays as text elsewhere.</summary>
    private static string PostgresArrayText(IEnumerable items)
    {
        var sb = new StringBuilder("{");
        var first = true;
        foreach (var item in items)
        {
            if (!first) sb.Append(',');
            first = false;
            switch (item)
            {
                case null or DBNull: sb.Append("NULL"); break;
                case bool b: sb.Append(b ? "t" : "f"); break;
                case IFormattable f and not DateTime and not DateTimeOffset:
                    sb.Append(f.ToString(null, CultureInfo.InvariantCulture)); break;
                default:
                    var text = item switch
                    {
                        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture),
                        DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture),
                        _ => Convert.ToString(item, CultureInfo.InvariantCulture) ?? ""
                    };
                    sb.Append('"').Append(text.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
                    break;
            }
        }
        return sb.Append('}').ToString();
    }
}
