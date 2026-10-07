using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DbExplorer.Core.Connections;
using DbExplorer.Core.Search;
using MySqlConnector;

namespace DbExplorer.Providers.MySql;

public static class MySqlSql
{
    /// <summary>Schemas that belong to the server, never to an application.</summary>
    public static readonly string[] SystemSchemas = ["mysql", "information_schema", "performance_schema", "sys"];

    public static readonly HashSet<string> TextTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "char", "varchar", "tinytext", "text", "mediumtext", "longtext", "enum", "set"
    };

    public static readonly HashSet<string> NumericTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "tinyint", "smallint", "mediumint", "int", "integer", "bigint", "decimal", "numeric", "float", "double", "real"
    };

    public static string Quote(string identifier) => "`" + identifier.Replace("`", "``") + "`";

    public static string QuoteFullName(string schema, string name) =>
        string.IsNullOrEmpty(schema) ? Quote(name) : Quote(schema) + "." + Quote(name);

    /// <summary>A string literal that reads the same whether or not the server treats backslashes as escapes.</summary>
    public static string Literal(string value) => "'" + value.Replace("\\", "\\\\").Replace("'", "''") + "'";

    /// <summary>Escapes LIKE wildcards for <c>ESCAPE '!'</c> (a backslash would depend on NO_BACKSLASH_ESCAPES).</summary>
    public static string EscapeLike(string value) =>
        value.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_");

    public static string BuildPattern(string text, SearchMatchMode mode)
    {
        var e = EscapeLike(text);
        return mode switch
        {
            SearchMatchMode.Contains => $"%{e}%",
            SearchMatchMode.StartsWith => $"{e}%",
            SearchMatchMode.EndsWith => $"%{e}",
            _ => e
        };
    }

    /// <summary>
    /// Session settings for reads that must never queue behind other sessions: give up on a metadata lock or a row lock
    /// quickly and never run forever. InnoDB plain SELECTs read a snapshot and take no row locks, so no dirty reads are
    /// needed; the transaction that follows is started READ ONLY.
    /// </summary>
    public static string ReadOnlySettings(bool mariaDb, int statementTimeoutMs, int lockTimeoutMs)
    {
        var lockSeconds = Math.Max(1, (int)Math.Ceiling(Math.Max(0, lockTimeoutMs) / 1000d));
        var timeout = mariaDb
            ? $"SET SESSION max_statement_time = {(Math.Max(0, statementTimeoutMs) / 1000d).ToString("0.###", CultureInfo.InvariantCulture)}"
            : $"SET SESSION max_execution_time = {Math.Max(0, statementTimeoutMs)}";
        return $"SET SESSION lock_wait_timeout = {lockSeconds}; SET SESSION innodb_lock_wait_timeout = {lockSeconds}; {timeout};";
    }

    public static string BuildConnectionString(ConnectionProfile p, string? databaseOverride = null)
    {
        var b = new MySqlConnectionStringBuilder
        {
            Server = p.Host,
            Port = (uint)(p.Port ?? 3306),
            Database = databaseOverride ?? p.Database,
            UserID = p.UserName ?? "",
            Password = p.Password ?? "",
            ApplicationName = "DbExplorer",
            ConnectionTimeout = 15,
            // Same rules as the other engines: "Encrypt" alone checks the certificate and host name, "Trust certificate"
            // encrypts without checking. Through an SSH tunnel the certificate never names 127.0.0.1, so only the chain is checked.
            SslMode = !p.Encrypt ? MySqlSslMode.Preferred
                : p.TrustServerCertificate ? MySqlSslMode.Required
                : p.CertificateHostName is null ? MySqlSslMode.VerifyFull : MySqlSslMode.VerifyCA,
            // MySQL 8's default login (caching_sha2_password) needs the server's RSA key on an unencrypted connection.
            // Only allowed when the user did not ask for encryption, where a man in the middle is already possible.
            AllowPublicKeyRetrieval = !p.Encrypt,
            // Scripts may use @variables; without this the driver takes them for missing parameters.
            AllowUserVariables = true,
            // '0000-00-00' dates read as DateTime.MinValue instead of failing the whole result set.
            ConvertZeroDateTime = true,
            // char(36) stays text, binary(16) stays bytes: the grid shows what is stored.
            GuidFormat = MySqlGuidFormat.None,
            DefaultCommandTimeout = 30,
            Pooling = true
        };
        return b.ConnectionString;
    }

    private static readonly Regex DelimiterLine = new(@"^[ \t]*DELIMITER[ \t]+(?<d>\S+)[ \t]*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>
    /// Turns a script written for the mysql command-line client (DELIMITER $$ … $$) into one the server accepts: the
    /// DELIMITER lines are blanked and each custom delimiter becomes a semicolon. Lengths are kept, so error positions
    /// still point into the text the user wrote. The server parses BEGIN … END bodies itself.
    /// </summary>
    public static string StripDelimiters(string sql)
    {
        if (!DelimiterLine.IsMatch(sql)) return sql;

        var sb = new StringBuilder(sql);
        var delimiter = ";";
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            var atLineStart = i == 0 || sql[i - 1] == '\n';
            if (atLineStart)
            {
                var m = DelimiterLine.Match(sql, i);
                if (m.Success && m.Index == i)
                {
                    delimiter = m.Groups["d"].Value;
                    for (var k = m.Index; k < m.Index + m.Length; k++) sb[k] = ' ';
                    i = m.Index + m.Length;
                    continue;
                }
            }

            if (c is '\'' or '"' or '`')
            {
                i = SkipQuoted(sql, i);
                continue;
            }
            if (c == '#' || (c == '-' && i + 2 < sql.Length && sql[i + 1] == '-' && char.IsWhiteSpace(sql[i + 2])))
            {
                var end = sql.IndexOf('\n', i);
                i = end < 0 ? sql.Length : end;
                continue;
            }
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + 2;
                continue;
            }

            if (delimiter != ";" && string.CompareOrdinal(sql, i, delimiter, 0, delimiter.Length) == 0)
            {
                sb[i] = ';';
                for (var k = i + 1; k < i + delimiter.Length; k++) sb[k] = ' ';
                i += delimiter.Length;
                continue;
            }
            i++;
        }
        return sb.ToString();
    }

    private static int SkipQuoted(string sql, int start)
    {
        var quote = sql[start];
        var i = start + 1;
        while (i < sql.Length)
        {
            if (sql[i] == '\\' && quote != '`') { i += 2; continue; }
            if (sql[i] == quote)
            {
                if (i + 1 < sql.Length && sql[i + 1] == quote) { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        return sql.Length;
    }

    /// <summary>Where each top-level statement of the script starts (split on semicolons outside quotes and comments).</summary>
    public static IReadOnlyList<int> StatementStarts(string sql)
    {
        var starts = new List<int>();
        var i = 0;
        var pending = true;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '#' || (c == '-' && i + 2 < sql.Length && sql[i + 1] == '-' && char.IsWhiteSpace(sql[i + 2])))
            {
                var end = sql.IndexOf('\n', i);
                i = end < 0 ? sql.Length : end;
                continue;
            }
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + 2;
                continue;
            }
            if (pending) { starts.Add(i); pending = false; }
            if (c is '\'' or '"' or '`') { i = SkipQuoted(sql, i); continue; }
            if (c == ';') pending = true;
            i++;
        }
        return starts;
    }

    private static readonly Regex NearAtLine = new(@"near '(?<near>.*)' at line (?<line>\d+)\s*$",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>
    /// The script offset a syntax error points at. The server reports the text that follows the error ("near '…'") and
    /// its line counted from the start of the failing statement, which is not known in a multi-statement script: the
    /// first statement whose line holds that text wins. Null when the message carries no position.
    /// </summary>
    public static int? ErrorOffset(string sql, string message)
    {
        var m = NearAtLine.Match(message);
        if (!m.Success || !int.TryParse(m.Groups["line"].Value, out var line) || line < 1) return null;
        var near = m.Groups["near"].Value;
        // The server cuts the snippet at 80 characters; the first line of it is enough to find it.
        var probe = near.Split('\n')[0];

        foreach (var start in StatementStarts(sql))
        {
            var lineStart = start;
            for (var l = 1; l < line && lineStart >= 0; l++)
            {
                var nl = sql.IndexOf('\n', lineStart);
                lineStart = nl < 0 ? -1 : nl + 1;
            }
            if (lineStart < 0) continue;
            var lineEnd = sql.IndexOf('\n', lineStart);
            if (lineEnd < 0) lineEnd = sql.Length;

            // Line 1 of a statement starts where the statement does, not at the start of the text line.
            if (probe.Length == 0)
            {
                // "near ''": the statement ended too early; point at the end of its last line.
                continue;
            }
            var at = sql.IndexOf(probe, lineStart, lineEnd - lineStart, StringComparison.Ordinal);
            if (at >= 0) return at;
        }

        // Not found by line (e.g. a statement inside a BEGIN … END body): the first occurrence of the text.
        if (probe.Length > 0)
        {
            var any = sql.IndexOf(probe, StringComparison.Ordinal);
            if (any >= 0) return any;
        }
        return null;
    }
}
