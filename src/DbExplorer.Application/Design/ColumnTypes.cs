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
    /// <summary>A type written the same way whatever spelling was used, to tell a real type change from a respelling:
    /// character varying(100) and varchar(100), int4 and integer, timestamptz and timestamp with time zone.</summary>
    public static string Canonical(string providerKey, string fullType)
    {
        var type = string.Join(' ', fullType.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        type = type.Replace(" (", "(").Replace(", ", ",");
        var open = type.IndexOf('(');
        var close = type.LastIndexOf(')');
        var name = open < 0 ? type : type[..open];
        var size = open >= 0 && close > open ? type[open..(close + 1)] : "";
        var rest = open >= 0 && close > open ? type[(close + 1)..].Trim() : "";
        if (providerKey != SqlDialect.SqlServerKey)
        {
            if (rest.Length > 0) name = $"{name} {rest}";
            name = name switch
            {
                "character varying" => "varchar",
                "character" or "bpchar" => "char",
                "int" or "int4" => "integer",
                "int8" => "bigint",
                "int2" => "smallint",
                "bool" => "boolean",
                "decimal" => "numeric",
                "float8" => "double precision",
                "float4" => "real",
                "timestamp with time zone" => "timestamptz",
                "timestamp without time zone" => "timestamp",
                "time without time zone" => "time",
                "time with time zone" => "timetz",
                _ => name
            };
            return name + size;
        }
        return name + size + (rest.Length > 0 ? " " + rest : "");
    }

    /// <summary>Why changing a column from <paramref name="before"/> to <paramref name="after"/> can fail or lose data on
    /// rows the table already has (a shorter length, a smaller integer, fewer digits, another kind of value), or null when
    /// every existing value fits the new type.</summary>
    public static string? RiskOfChange(string providerKey, ColumnDesign before, ColumnDesign after)
    {
        if (Canonical(providerKey, before.FullType) == Canonical(providerKey, after.FullType)) return null;
        var from = Family(before);
        var to = Family(after);
        var textual = new[] { TypeFamily.Text, TypeFamily.LargeText };
        if (textual.Contains(from) && textual.Contains(to) || from == to && from == TypeFamily.Binary)
            return Length(before) > Length(after)
                ? $"Values longer than {Length(after)} characters no longer fit: the change fails, or on some engines cuts them."
                : null;
        if (from == TypeFamily.Integer && to == TypeFamily.Integer)
            return IntegerRank(before.BaseType) > IntegerRank(after.BaseType)
                ? $"{after.BaseType} holds smaller numbers than {before.BaseType}; the change fails if a value does not fit."
                : null;
        if (from == TypeFamily.Decimal && to == TypeFamily.Decimal)
        {
            var (p1, s1) = Precision(before);
            var (p2, s2) = Precision(after);
            return p2 - s2 < p1 - s1 || s2 < s1
                ? $"{after.FullType} keeps fewer digits than {before.FullType}: large values fail and decimals are rounded."
                : null;
        }
        if (from == TypeFamily.Integer && to is TypeFamily.Decimal or TypeFamily.Float) return null;
        if (from == TypeFamily.Date && to == TypeFamily.DateTime) return null;
        if (to is TypeFamily.Text or TypeFamily.LargeText && from is not (TypeFamily.Binary or TypeFamily.Other) && Length(after) >= 40) return null;
        if (to == TypeFamily.LargeText) return null;
        return $"Every existing value must convert from {before.FullType} to {after.FullType}; one that does not makes the change fail.";
    }

    /// <summary>Characters a text or binary type holds; int.MaxValue for max, text and a varchar without a length.</summary>
    private static int Length(ColumnDesign column)
    {
        var size = column.EffectiveSize;
        if (Family(column) == TypeFamily.LargeText || string.IsNullOrEmpty(size) || size.Equals("max", StringComparison.OrdinalIgnoreCase))
            return int.MaxValue;
        return int.TryParse(size.Split(',')[0], out var n) ? n : int.MaxValue;
    }

    private static int IntegerRank(string baseType) => baseType switch
    {
        "tinyint" => 1,
        "smallint" or "int2" or "smallserial" => 2,
        "bigint" or "int8" or "bigserial" => 4,
        _ => 3
    };

    /// <summary>Precision and scale; a decimal without them counts as unlimited (PostgreSQL) or 18,0 (SQL Server).</summary>
    private static (int Precision, int Scale) Precision(ColumnDesign column)
    {
        var parts = (column.EffectiveSize ?? "").Split(',', StringSplitOptions.TrimEntries);
        if (parts[0].Length == 0 || !int.TryParse(parts[0], out var p)) return column.BaseType is "money" or "smallmoney" ? (19, 4) : (1000, 0);
        return (p, parts.Length > 1 && int.TryParse(parts[1], out var s) ? s : 0);
    }
}
