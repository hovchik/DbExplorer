using System.Text.RegularExpressions;

namespace DbExplorer.Providers.Sqlite;

/// <summary>Small readers of SQLite's stored CREATE statements and error messages.</summary>
public static class SqliteScript
{
    /// <summary>
    /// Where <paramref name="keyword"/> first appears as a whole word outside strings, quoted names and comments,
    /// or -1.
    /// </summary>
    public static int FindKeyword(string sql, string keyword, int start = 0)
    {
        var i = start;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c is '\'' or '"' or '`' or '[')
            {
                var close = c == '[' ? ']' : c;
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == close)
                    {
                        if (close != ']' && i + 1 < sql.Length && sql[i + 1] == close) { i += 2; continue; }
                        break;
                    }
                    i++;
                }
                i++;
                continue;
            }
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                var end = sql.IndexOf('\n', i);
                i = end < 0 ? sql.Length : end + 1;
                continue;
            }
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + 2;
                continue;
            }
            if (IsWord(c) && (i == 0 || !IsWord(sql[i - 1])))
            {
                var j = i;
                while (j < sql.Length && IsWord(sql[j])) j++;
                if (j - i == keyword.Length && string.Compare(sql, i, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) == 0)
                    return i;
                i = j;
                continue;
            }
            i++;
        }
        return -1;
    }

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';

    /// <summary>The text from <paramref name="open"/> (an opening parenthesis) to its closing one, exclusive, or null.</summary>
    private static string? Parenthesised(string sql, int open)
    {
        var depth = 0;
        for (var i = open; i < sql.Length; i++)
        {
            var c = sql[i];
            if (c is '\'' or '"' or '`')
            {
                var end = sql.IndexOf(c, i + 1);
                while (end > 0 && end + 1 < sql.Length && sql[end + 1] == c) end = sql.IndexOf(c, end + 2);
                if (end < 0) return null;
                i = end;
                continue;
            }
            if (c == '(') depth++;
            else if (c == ')' && --depth == 0) return sql[(open + 1)..i];
        }
        return null;
    }

    private static readonly Regex ConstraintName = new(
        @"CONSTRAINT\s+(?<name>""(?:[^""]|"""")+""|`[^`]+`|\[[^\]]+\]|[\w$]+)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The CHECK constraints in a CREATE TABLE, column and table ones alike, as (name, "(condition)"). An unnamed one
    /// is called ck_1, ck_2… in order; SQLite itself keeps no names for them.
    /// </summary>
    public static IReadOnlyList<(string Name, string Expression)> CheckConstraints(string? createTable)
    {
        var checks = new List<(string, string)>();
        if (string.IsNullOrEmpty(createTable)) return checks;
        var at = FindKeyword(createTable, "CHECK");
        while (at >= 0)
        {
            var open = createTable.IndexOf('(', at);
            if (open < 0) break;
            var body = Parenthesised(createTable, open);
            if (body is null) break;
            var before = createTable[..at].TrimEnd();
            var named = ConstraintName.Match(before);
            var name = named.Success ? Unquote(named.Groups["name"].Value) : $"ck_{checks.Count + 1}";
            checks.Add((name, "(" + body.Trim() + ")"));
            at = FindKeyword(createTable, "CHECK", open + body.Length + 2);
        }
        return checks;
    }

    private static string Unquote(string name) =>
        name.Length >= 2 && name[0] is '"' or '`' or '[' ? name[1..^1].Replace("\"\"", "\"") : name;

    private static readonly Regex Near = new(@"near ""(?<token>[^""]+)""", RegexOptions.CultureInvariant);

    /// <summary>
    /// Where a syntax error is in the script: SQLite reports the token it stopped at ("near "FORM": syntax error"), so
    /// this is the first place that token appears as a word. Null when the message names no token or the script lacks it.
    /// </summary>
    public static int? ErrorOffset(string sql, string message)
    {
        var m = Near.Match(message);
        if (!m.Success) return null;
        var token = m.Groups["token"].Value;
        if (token.Length > 0 && token.All(IsWord))
            return FindKeyword(sql, token) is var i and >= 0 ? i : null;
        var at = sql.IndexOf(token, StringComparison.Ordinal);
        return at >= 0 ? at : null;
    }
}
