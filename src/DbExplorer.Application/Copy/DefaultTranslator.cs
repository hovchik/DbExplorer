using System.Text.RegularExpressions;

namespace DbExplorer.Application.Copy;

/// <summary>
/// Carries a column default over to the target engine. Within one engine the expression is reused as is;
/// across engines only literals and the common "now" / "new uuid" functions are translated, anything else is
/// dropped (and reported) rather than guessed. Sequence defaults (PostgreSQL serial) become identity columns.
/// </summary>
public static class DefaultTranslator
{
    private static readonly Regex Number = new(@"^-?\d+(\.\d+)?$", RegexOptions.CultureInvariant);
    private static readonly Regex Text = new(@"^N?'((?:[^']|'')*)'$", RegexOptions.CultureInvariant);
    private static readonly Regex PostgresCast = new(@"^(?<value>.+?)::[\w\s\.""\[\]\(\),]+$", RegexOptions.CultureInvariant);
    private static readonly Regex NextVal = new(@"^nextval\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>True for a PostgreSQL serial default (nextval('...')): the column should become an identity column.</summary>
    public static bool IsSequenceDefault(string expression) => NextVal.IsMatch(expression.Trim());

    /// <summary>The default for the target column, or null when it cannot be carried over.</summary>
    public static string? Translate(string expression, string sourceProviderKey, string targetProviderKey, string targetBaseType)
    {
        var trimmed = expression.Trim();
        if (IsSequenceDefault(trimmed)) return null;
        if (sourceProviderKey == targetProviderKey) return trimmed;

        var core = Unwrap(trimmed);
        if (sourceProviderKey == SqlDialect.PostgresKey && PostgresCast.Match(core) is { Success: true } cast)
            core = Unwrap(cast.Groups["value"].Value.Trim());

        var target = targetBaseType.ToLowerInvariant();
        // MySQL writes the precision into its clock functions: CURRENT_TIMESTAMP(6), now(3).
        var lower = PrecisionSuffix.Replace(core.ToLowerInvariant(), "()");
        if (lower == "current_timestamp()") lower = "current_timestamp";
        var toPostgres = targetProviderKey == SqlDialect.PostgresKey;
        var toMySql = targetProviderKey == SqlDialect.MySqlKey;

        if (Number.IsMatch(core))
        {
            if (target is "boolean" or "bool") return core == "0" ? "FALSE" : "TRUE";
            return core;
        }

        if (lower is "true" or "false")
            return toPostgres || toMySql ? lower.ToUpperInvariant() : lower == "true" ? "1" : "0";

        if (Text.Match(core) is { Success: true } text)
        {
            var value = text.Groups[1].Value;
            return toPostgres ? $"'{value}'" : toMySql ? $"'{value.Replace("\\", "\\\\")}'" : $"N'{value}'";
        }

        var clock = target is "datetime" or "timestamp" or "timestamptz" or "datetime2" or "smalldatetime" or "datetimeoffset";
        return lower switch
        {
            "getdate()" or "sysdatetime()" or "current_timestamp" or "now()" or "localtimestamp" or "localtimestamp()"
                or "transaction_timestamp()" or "localtime" or "localtime()"
                => toPostgres ? "CURRENT_TIMESTAMP"
                   : toMySql ? (clock ? "CURRENT_TIMESTAMP" : null)
                   : target == "datetime" ? "GETDATE()" : "SYSDATETIME()",
            "getutcdate()" or "sysutcdatetime()" or "utc_timestamp()" =>
                toPostgres ? "(now() AT TIME ZONE 'utc')" : toMySql ? "(UTC_TIMESTAMP())" : "SYSUTCDATETIME()",
            "sysdatetimeoffset()" => toPostgres ? "CURRENT_TIMESTAMP" : toMySql && clock ? "CURRENT_TIMESTAMP" : null,
            "current_date" or "curdate()" => toPostgres ? "CURRENT_DATE" : toMySql ? "(CURRENT_DATE)" : "CAST(GETDATE() AS date)",
            "newid()" or "newsequentialid()" => toPostgres ? "gen_random_uuid()" : toMySql ? "(UUID())" : null,
            "gen_random_uuid()" or "uuid_generate_v4()" => toPostgres ? null : toMySql ? "(UUID())" : "NEWID()",
            "uuid()" => toPostgres ? "gen_random_uuid()" : toMySql ? null : "NEWID()",
            _ => null
        };
    }

    private static readonly Regex PrecisionSuffix = new(@"\(\d+\)$", RegexOptions.CultureInvariant);

    /// <summary>SQL Server stores defaults wrapped in parentheses, e.g. "((0))" or "(getdate())".</summary>
    private static string Unwrap(string expression)
    {
        var s = expression;
        while (s.Length >= 2 && s[0] == '(' && s[^1] == ')' && Balanced(s[1..^1])) s = s[1..^1].Trim();
        return s;
    }

    private static bool Balanced(string s)
    {
        var depth = 0;
        var inText = false;
        foreach (var ch in s)
        {
            if (ch == '\'') inText = !inText;
            if (inText) continue;
            if (ch == '(') depth++;
            else if (ch == ')' && --depth < 0) return false;
        }
        return depth == 0;
    }
}
