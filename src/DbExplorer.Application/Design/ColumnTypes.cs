using DbExplorer.Application.Copy;

namespace DbExplorer.Application.Design;

/// <summary>What kind of value a type holds, which the design suggestions reason about.</summary>
public enum TypeFamily
{
    Other,
    Integer,
    Decimal,
    Float,
    Text,
    LargeText,
    Boolean,
    Date,
    DateTime,
    Time,
    Uuid,
    Binary,
    Json
}

/// <summary>The column types the table designer offers per engine, and the family any type name belongs to.</summary>
public static class ColumnTypes
{
    private static readonly string[] SqlServer =
    [
        "int", "bigint", "smallint", "tinyint", "bit", "decimal", "numeric", "money", "float", "real",
        "nvarchar", "varchar", "nchar", "char", "date", "datetime2", "datetimeoffset", "time", "datetime",
        "uniqueidentifier", "varbinary", "xml"
    ];

    private static readonly string[] Postgres =
    [
        "integer", "bigint", "smallint", "boolean", "numeric", "real", "double precision", "varchar", "text", "char",
        "date", "timestamp", "timestamptz", "time", "interval", "uuid", "jsonb", "json", "bytea"
    ];

    /// <summary>The types offered in the designer's type list, most used first.</summary>
    public static IReadOnlyList<string> For(string providerKey) => providerKey == SqlDialect.SqlServerKey ? SqlServer : Postgres;

    /// <summary>SQL Server sizes a varchar/nvarchar/varbinary without a length as 1 in a CREATE TABLE.</summary>
    public static bool NeedsLength(string providerKey, string baseType) =>
        providerKey == SqlDialect.SqlServerKey && baseType is "varchar" or "nvarchar" or "varbinary" or "char" or "nchar" or "binary";

    public static TypeFamily Family(ColumnDesign column) => Family(column.BaseType, column.EffectiveSize);

    public static TypeFamily Family(string baseType, string? size = null)
    {
        var type = baseType.Trim().ToLowerInvariant();
        var open = type.IndexOf('(');
        if (open >= 0)
        {
            size ??= type[(open + 1)..].TrimEnd(')');
            type = type[..open].Trim();
        }
        var max = string.Equals(size?.Trim(), "max", StringComparison.OrdinalIgnoreCase);
        return type switch
        {
            "tinyint" or "smallint" or "int" or "bigint" or "int2" or "int4" or "int8" or "integer" or "serial" or "bigserial" or "smallserial" => TypeFamily.Integer,
            "decimal" or "numeric" or "money" or "smallmoney" => TypeFamily.Decimal,
            "float" or "real" or "double precision" or "float4" or "float8" => TypeFamily.Float,
            "varchar" or "nvarchar" or "character varying" when max => TypeFamily.LargeText,
            "char" or "nchar" or "varchar" or "nvarchar" or "character" or "character varying" or "bpchar" or "citext" or "sysname" => TypeFamily.Text,
            "text" or "ntext" => TypeFamily.LargeText,
            "bit" or "boolean" or "bool" => TypeFamily.Boolean,
            "date" => TypeFamily.Date,
            "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" or "timestamp" or "timestamptz"
                or "timestamp with time zone" or "timestamp without time zone" => TypeFamily.DateTime,
            "time" or "interval" => TypeFamily.Time,
            "uniqueidentifier" or "uuid" => TypeFamily.Uuid,
            "varbinary" or "binary" or "bytea" or "image" => TypeFamily.Binary,
            "json" or "jsonb" or "xml" => TypeFamily.Json,
            _ => TypeFamily.Other
        };
    }

    /// <summary>The engine's everyday type for a family: int / integer, datetime2 / timestamptz, bit / boolean …</summary>
    public static (string Type, string? Size) Preferred(string providerKey, TypeFamily family)
    {
        var sqlServer = providerKey == SqlDialect.SqlServerKey;
        return family switch
        {
            TypeFamily.Integer => (sqlServer ? "int" : "integer", null),
            TypeFamily.Decimal => (sqlServer ? "decimal" : "numeric", "18,2"),
            TypeFamily.Float => (sqlServer ? "float" : "double precision", null),
            TypeFamily.Boolean => (sqlServer ? "bit" : "boolean", null),
            TypeFamily.Date => ("date", null),
            TypeFamily.DateTime => (sqlServer ? "datetime2" : "timestamptz", null),
            TypeFamily.Time => ("time", null),
            TypeFamily.Uuid => (sqlServer ? "uniqueidentifier" : "uuid", null),
            TypeFamily.Binary => (sqlServer ? "varbinary" : "bytea", sqlServer ? "max" : null),
            TypeFamily.Json => (sqlServer ? "nvarchar" : "jsonb", sqlServer ? "max" : null),
            TypeFamily.LargeText => (sqlServer ? "nvarchar" : "text", sqlServer ? "max" : null),
            _ => (sqlServer ? "nvarchar" : "varchar", "100")
        };
    }

    /// <summary>The expression for "now" a timestamp column defaults to.</summary>
    public static string NowExpression(string providerKey, string baseType) =>
        providerKey == SqlDialect.SqlServerKey
            ? baseType switch { "datetime" or "smalldatetime" => "GETDATE()", "datetimeoffset" => "SYSDATETIMEOFFSET()", "date" => "CAST(GETDATE() AS date)", _ => "SYSUTCDATETIME()" }
            : baseType == "date" ? "CURRENT_DATE" : "now()";
}
