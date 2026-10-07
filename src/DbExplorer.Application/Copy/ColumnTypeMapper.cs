using System.Globalization;
using System.Text.RegularExpressions;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Copy;

/// <summary>
/// Translates a column's type between engines for CREATE TABLE / ADD COLUMN. Within one engine the
/// catalog type is reused verbatim. Across engines the common types map exactly; anything exotic
/// falls back to text and is reported so the user can adjust the script.
/// </summary>
public static class ColumnTypeMapper
{
    private static readonly Regex Arguments = new(@"\((?<args>[^)]*)\)", RegexOptions.CultureInvariant);

    public sealed record Mapping(string Type, string? Warning = null);

    public static Mapping Map(DbColumn column, string sourceProviderKey, string targetProviderKey)
    {
        if (sourceProviderKey == targetProviderKey) return new Mapping(column.DataType);

        return (sourceProviderKey, targetProviderKey) switch
        {
            (SqlDialect.SqlServerKey, SqlDialect.MySqlKey) => SqlServerToMySql(column),
            (SqlDialect.MySqlKey, SqlDialect.SqlServerKey) => MySqlToSqlServer(column),
            (SqlDialect.MySqlKey, _) => MySqlToPostgres(column),
            (_, SqlDialect.MySqlKey) => PostgresToMySql(column),
            (SqlDialect.SqlServerKey, _) => SqlServerToPostgres(column),
            _ => PostgresToSqlServer(column)
        };
    }

    /// <summary>MySQL varchar lengths above this go to TEXT: a row holds at most 64 KB, and utf8mb4 takes 4 bytes a character.</summary>
    private const int MySqlMaxVarchar = 2048;

    private static string MySqlVarchar(int? length) =>
        length is null ? "longtext" : length > MySqlMaxVarchar ? "text" : $"varchar({length})";

    private static string MySqlChar(int? length) =>
        length is > 0 and <= 255 ? $"char({length})" : MySqlVarchar(length);

    private static Mapping SqlServerToMySql(DbColumn c)
    {
        var args = ArgsOf(c.DataType);
        var length = args.Count == 1 ? args[0] : null;
        var n = length is null or "max" ? null : ParseInt(length);
        var precision = args.Count >= 1 ? ParseInt(args[0]) : null;

        return c.BaseType.ToLowerInvariant() switch
        {
            "bit" => new("tinyint(1)"),
            "tinyint" => new("tinyint unsigned"),
            "smallint" => new("smallint"),
            "int" => new("int"),
            "bigint" => new("bigint"),
            "decimal" or "numeric" => new(args.Count == 2 ? $"decimal({args[0]},{args[1]})" : "decimal(18,0)"),
            "money" => new("decimal(19,4)"),
            "smallmoney" => new("decimal(10,4)"),
            "float" => new("double"),
            "real" => new("float"),
            "date" => new("date"),
            "time" => new($"time({Math.Min(precision ?? 6, 6)})"),
            "datetime" => new("datetime(3)"),
            "smalldatetime" => new("datetime"),
            "datetime2" => new($"datetime({Math.Min(precision ?? 6, 6)})"),
            "datetimeoffset" => new($"datetime({Math.Min(precision ?? 6, 6)})", $"{c.Name}: datetimeoffset created as datetime; values are stored in UTC."),
            "char" or "nchar" => new(MySqlChar(n)),
            "varchar" or "nvarchar" => new(MySqlVarchar(n)),
            "text" or "ntext" or "xml" => new("longtext"),
            "sysname" => new("varchar(128)"),
            "binary" => new(n is > 0 and <= 255 ? $"binary({n})" : "longblob"),
            "varbinary" => new(n is > 0 and <= 8000 ? $"varbinary({n})" : "longblob"),
            "image" => new("longblob"),
            "timestamp" or "rowversion" => new("binary(8)"),
            "uniqueidentifier" => new("char(36)"),
            var other => new("longtext", $"{c.Name}: {other} has no MySQL equivalent; created as longtext.")
        };
    }

    private static Mapping PostgresToMySql(DbColumn c)
    {
        var args = ArgsOf(c.DataType);
        var baseType = c.BaseType.ToLowerInvariant();
        if (baseType.StartsWith('_'))
            return new("longtext", $"{c.Name}: arrays have no MySQL equivalent; created as longtext holding the array text.");

        var precision = args.Count >= 1 ? ParseInt(args[0]) : null;
        return baseType switch
        {
            "bool" => new("tinyint(1)"),
            "int2" => new("smallint"),
            "int4" => new("int"),
            "oid" => new("int unsigned"),
            "int8" => new("bigint"),
            "numeric" => new(args.Count == 2 ? $"decimal({args[0]},{args[1]})" : "decimal(65,10)",
                args.Count == 2 ? null : $"{c.Name}: unconstrained numeric created as decimal(65,10)."),
            "money" => new("decimal(19,4)"),
            "float4" => new("float"),
            "float8" => new("double"),
            "varchar" => new(MySqlVarchar(precision)),
            "bpchar" or "char" => new(MySqlChar(precision ?? 1)),
            "name" => new("varchar(64)"),
            "text" or "citext" or "xml" => new("longtext"),
            "json" or "jsonb" => new("json"),
            "date" => new("date"),
            "time" or "timetz" => new($"time({Math.Min(precision ?? 6, 6)})"),
            "timestamp" => new($"datetime({Math.Min(precision ?? 6, 6)})"),
            "timestamptz" => new($"datetime({Math.Min(precision ?? 6, 6)})", $"{c.Name}: timestamptz created as datetime; values are stored in UTC."),
            "interval" => new("varchar(100)", $"{c.Name}: interval created as varchar(100)."),
            "bytea" => new("longblob"),
            "uuid" => new("char(36)"),
            var other => new("longtext", $"{c.Name}: {other} has no MySQL equivalent; created as longtext.")
        };
    }

    private static bool IsUnsigned(DbColumn c) => c.DataType.Contains("unsigned", StringComparison.OrdinalIgnoreCase);

    /// <summary>MySQL's tinyint(1) is its boolean (BOOL is an alias for it).</summary>
    private static bool IsMySqlBoolean(DbColumn c) =>
        c.DataType.StartsWith("tinyint(1)", StringComparison.OrdinalIgnoreCase) ||
        (Same(c.BaseType, "bit") && ArgsOf(c.DataType) is ["1"] or []);

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static Mapping MySqlToPostgres(DbColumn c)
    {
        var args = ArgsOf(c.DataType);
        var n = args.Count >= 1 ? ParseInt(args[0]) : null;
        var unsigned = IsUnsigned(c);
        if (IsMySqlBoolean(c)) return new("boolean");

        return c.BaseType.ToLowerInvariant() switch
        {
            "tinyint" => new("smallint"),
            "smallint" => new(unsigned ? "integer" : "smallint"),
            "mediumint" => new("integer"),
            "int" or "integer" => new(unsigned ? "bigint" : "integer"),
            "bigint" => new(unsigned ? "numeric(20,0)" : "bigint"),
            "decimal" or "numeric" => new(args.Count == 2 ? $"numeric({args[0]},{args[1]})" : "numeric"),
            "float" => new("real"),
            "double" or "real" => new("double precision"),
            "bit" => new("bigint", $"{c.Name}: bit({n}) created as bigint holding the bits as a number."),
            "year" => new("smallint"),
            "date" => new("date"),
            "time" => new($"time({Math.Min(n ?? 0, 6)})"),
            "datetime" or "timestamp" => new($"timestamp({Math.Min(n ?? 0, 6)})"),
            "char" => new(n is > 0 ? $"char({n})" : "text"),
            "varchar" => new(n is > 0 ? $"varchar({n})" : "text"),
            "tinytext" or "text" or "mediumtext" or "longtext" or "enum" or "set" => new("text"),
            "binary" or "varbinary" or "tinyblob" or "blob" or "mediumblob" or "longblob" => new("bytea"),
            "json" => new("jsonb"),
            var other => new("text", $"{c.Name}: {other} has no PostgreSQL equivalent; created as text.")
        };
    }

    private static Mapping MySqlToSqlServer(DbColumn c)
    {
        var args = ArgsOf(c.DataType);
        var n = args.Count >= 1 ? ParseInt(args[0]) : null;
        var unsigned = IsUnsigned(c);
        if (IsMySqlBoolean(c)) return new("bit");

        return c.BaseType.ToLowerInvariant() switch
        {
            "tinyint" => new(unsigned ? "tinyint" : "smallint"),
            "smallint" => new(unsigned ? "int" : "smallint"),
            "mediumint" => new("int"),
            "int" or "integer" => new(unsigned ? "bigint" : "int"),
            "bigint" => new(unsigned ? "decimal(20,0)" : "bigint"),
            "decimal" or "numeric" when args.Count == 2 && ParseInt(args[0]) > 38 =>
                new($"decimal(38,{Math.Min(ParseInt(args[1]) ?? 0, 38)})", $"{c.Name}: decimal({args[0]},{args[1]}) narrowed to SQL Server's 38 digits."),
            "decimal" or "numeric" => new(args.Count == 2 ? $"decimal({args[0]},{args[1]})" : "decimal(10,0)"),
            "float" => new("real"),
            "double" or "real" => new("float"),
            "bit" => new("bigint", $"{c.Name}: bit({n}) created as bigint holding the bits as a number."),
            "year" => new("smallint"),
            "date" => new("date"),
            "time" => new($"time({Math.Min(n ?? 0, 7)})"),
            "datetime" or "timestamp" => new($"datetime2({Math.Min(n ?? 0, 7)})"),
            "char" => new(n is > 0 and <= 4000 ? $"nchar({n})" : "nvarchar(max)"),
            "varchar" => new(n is > 0 and <= 4000 ? $"nvarchar({n})" : "nvarchar(max)"),
            "enum" => new("nvarchar(255)"),
            "tinytext" or "text" or "mediumtext" or "longtext" or "set" or "json" => new("nvarchar(max)"),
            "binary" => new(n is > 0 and <= 8000 ? $"binary({n})" : "varbinary(max)"),
            "varbinary" => new(n is > 0 and <= 8000 ? $"varbinary({n})" : "varbinary(max)"),
            "tinyblob" or "blob" or "mediumblob" or "longblob" => new("varbinary(max)"),
            var other => new("nvarchar(max)", $"{c.Name}: {other} has no SQL Server equivalent; created as nvarchar(max).")
        };
    }

    private static Mapping SqlServerToPostgres(DbColumn c)
    {
        var args = ArgsOf(c.DataType);
        var length = args.Count == 1 ? args[0] : null;
        var precision = args.Count >= 1 ? ParseInt(args[0]) : null;

        return c.BaseType.ToLowerInvariant() switch
        {
            "bit" => new("boolean"),
            "tinyint" or "smallint" => new("smallint"),
            "int" => new("integer"),
            "bigint" => new("bigint"),
            "decimal" or "numeric" => new(args.Count == 2 ? $"numeric({args[0]},{args[1]})" : "numeric"),
            "money" => new("numeric(19,4)"),
            "smallmoney" => new("numeric(10,4)"),
            "float" => new("double precision"),
            "real" => new("real"),
            "date" => new("date"),
            "time" => new($"time({Math.Min(precision ?? 6, 6)})"),
            "datetime" => new("timestamp(3)"),
            "smalldatetime" => new("timestamp(0)"),
            "datetime2" => new($"timestamp({Math.Min(precision ?? 6, 6)})"),
            "datetimeoffset" => new($"timestamptz({Math.Min(precision ?? 6, 6)})"),
            "char" or "nchar" => new(length is null or "max" ? "text" : $"char({length})"),
            "varchar" or "nvarchar" => new(length is null or "max" ? "text" : $"varchar({length})"),
            "text" or "ntext" => new("text"),
            "sysname" => new("varchar(128)"),
            "binary" or "varbinary" or "image" or "timestamp" => new("bytea"),
            "uniqueidentifier" => new("uuid"),
            "xml" => new("xml"),
            var other => new("text", $"{c.Name}: {other} has no PostgreSQL equivalent; created as text.")
        };
    }

    private static Mapping PostgresToSqlServer(DbColumn c)
    {
        var args = ArgsOf(c.DataType);
        var baseType = c.BaseType.ToLowerInvariant();
        if (baseType.StartsWith('_'))
            return new("nvarchar(max)", $"{c.Name}: arrays have no SQL Server equivalent; created as nvarchar(max) holding the array text.");

        var precision = args.Count >= 1 ? ParseInt(args[0]) : null;
        var length = precision;

        return baseType switch
        {
            "bool" => new("bit"),
            "int2" => new("smallint"),
            "int4" or "oid" => new("int"),
            "int8" => new("bigint"),
            "numeric" => new(args.Count == 2 ? $"decimal({args[0]},{args[1]})" : "decimal(38,10)",
                args.Count == 2 ? null : $"{c.Name}: unconstrained numeric created as decimal(38,10)."),
            "money" => new("money"),
            "float4" => new("real"),
            "float8" => new("float"),
            "varchar" => new(length is > 0 and <= 4000 ? $"nvarchar({length})" : "nvarchar(max)"),
            "bpchar" or "char" => new(length is > 0 and <= 4000 ? $"nchar({length})" : "nchar(1)"),
            "name" => new("nvarchar(128)"),
            "text" or "citext" or "json" or "jsonb" => new("nvarchar(max)"),
            "date" => new("date"),
            "time" or "timetz" => new(precision is not null ? $"time({precision})" : "time"),
            "timestamp" => new(precision is not null ? $"datetime2({precision})" : "datetime2"),
            "timestamptz" => new(precision is not null ? $"datetimeoffset({precision})" : "datetimeoffset"),
            "interval" => new("nvarchar(100)", $"{c.Name}: interval created as nvarchar(100)."),
            "bytea" => new("varbinary(max)"),
            "uuid" => new("uniqueidentifier"),
            "xml" => new("xml"),
            var other => new("nvarchar(max)", $"{c.Name}: {other} has no SQL Server equivalent; created as nvarchar(max).")
        };
    }

    private static IReadOnlyList<string> ArgsOf(string dataType)
    {
        var match = Arguments.Match(dataType);
        return match.Success
            ? match.Groups["args"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            : [];
    }

    private static int? ParseInt(string text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
}
