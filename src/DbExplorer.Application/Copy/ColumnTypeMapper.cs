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

        return sourceProviderKey == SqlDialect.SqlServerKey
            ? SqlServerToPostgres(column)
            : PostgresToSqlServer(column);
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
