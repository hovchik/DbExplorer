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
        var lower = core.ToLowerInvariant();
        var toPostgres = targetProviderKey == SqlDialect.PostgresKey;

        if (Number.IsMatch(core))
        {
            if (target is "boolean" or "bool") return core == "0" ? "FALSE" : "TRUE";
            return core;
        }

        if (lower is "true" or "false")
            return toPostgres ? lower.ToUpperInvariant() : lower == "true" ? "1" : "0";

        if (Text.Match(core) is { Success: true } text)
        {
            var value = text.Groups[1].Value;
            return toPostgres ? $"'{value}'" : $"N'{value}'";
        }

        return lower switch
        {
            "getdate()" or "sysdatetime()" or "current_timestamp" or "now()" or "localtimestamp" or "transaction_timestamp()"
                => toPostgres ? "CURRENT_TIMESTAMP" : target == "datetime" ? "GETDATE()" : "SYSDATETIME()",
            "getutcdate()" or "sysutcdatetime()" => toPostgres ? "(now() AT TIME ZONE 'utc')" : null,
            "sysdatetimeoffset()" => toPostgres ? "CURRENT_TIMESTAMP" : null,
            "current_date" => toPostgres ? "CURRENT_DATE" : "CAST(GETDATE() AS date)",
            "newid()" or "newsequentialid()" => toPostgres ? "gen_random_uuid()" : null,
            "gen_random_uuid()" or "uuid_generate_v4()" => toPostgres ? null : "NEWID()",
            _ => null
        };
    }

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
