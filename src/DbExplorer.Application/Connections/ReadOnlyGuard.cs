using System.Text.RegularExpressions;
using DbExplorer.Application.Query;
using DbExplorer.Core.Connections;

namespace DbExplorer.Application.Connections;

/// <summary>
/// The app side of a read-only connection: what counts as a write, and the message shown when one is refused.
/// PostgreSQL connections are also opened read-only on the server; SQL Server has no such session setting.
/// </summary>
public static class ReadOnlyGuard
{
    private static readonly Regex WriteKeyword = new(
        @"\b(INSERT|UPDATE|DELETE|DROP|ALTER|TRUNCATE|CREATE|MERGE|EXEC|EXECUTE|CALL|GRANT|REVOKE|DENY|INTO|BACKUP|RESTORE|DBCC|KILL|VACUUM|REINDEX|CLUSTER|COPY|LOCK|REFRESH|COMMENT|SECURITY|LABEL|IMPORT|BULK|" +
        @"UPDATETEXT|WRITETEXT|SHUTDOWN|RECONFIGURE|SETUSER|SP_EXECUTESQL)\b" +
        // Phrases whose words alone are ordinary names: ENABLE / DISABLE TRIGGER, MySQL's RENAME TABLE and SET GLOBAL.
        @"|\b(ENABLE|DISABLE)\s+TRIGGER\b|\bRENAME\s+(TABLE|USER)\b|\bSET\s+([^;]*?,\s*)?(@@)?(GLOBAL|PERSIST|PERSIST_ONLY|PASSWORD)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(2));

    /// <summary>
    /// Words a statement that does not write may start with. Anything else at the start of a statement — a procedure name
    /// (SQL Server runs <c>sp_rename 'a', 'b'</c> or <c>dbo.usp_Purge</c> as the first statement of a batch without EXEC),
    /// MySQL's REPLACE / RENAME / LOAD, a statement the guard does not know — counts as a write.
    /// </summary>
    private static readonly HashSet<string> ReadingStarts = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "WITH", "VALUES", "TABLE", "SHOW", "EXPLAIN", "DESCRIBE", "DESC", "HELP", "CHECKSUM",
        "DECLARE", "SET", "PRINT", "USE", "RAISERROR", "THROW", "IF", "ELSE", "BEGIN", "END", "WHILE", "BREAK", "CONTINUE",
        "RETURN", "WAITFOR", "GOTO", "OPEN", "FETCH", "CLOSE", "DEALLOCATE", "MOVE", "READTEXT", "REVERT",
        "COMMIT", "ROLLBACK", "SAVE", "SAVEPOINT", "RELEASE", "START", "ABORT", "DISCARD", "RESET", "LISTEN", "UNLISTEN", "NOTIFY"
    };

    /// <summary>
    /// Whether <paramref name="sql"/> may change anything. Comments and string literals are ignored, so
    /// <c>WHERE note = 'delete me'</c> still reads; dynamic SQL (EXEC, sp_executesql) always counts as a write.
    /// </summary>
    public static bool MayWrite(string sql) => WriteKeyword.IsMatch(StripCommentsAndStrings(sql)) || StartsUnknownStatement(sql);

    /// <summary>Whether a statement starts with a name or a word not in <see cref="ReadingStarts"/>.</summary>
    private static bool StartsUnknownStatement(string sql)
    {
        foreach (var range in SqlScriptTools.SplitStatements(sql, splitOnBlankLines: false))
        {
            var tokens = SqlLexer.Tokenize(range.Of(sql)).Where(t => !t.IsTrivia).Take(2).ToList();
            if (tokens.Count == 0) continue;
            var first = tokens[0];
            if (first.Kind == SqlTokenKind.QuotedIdentifier) return true;
            if (first.Kind != SqlTokenKind.Word) continue;
            if (!ReadingStarts.Contains(first.Text) || tokens.Count > 1 && tokens[1].Kind == SqlTokenKind.Dot) return true;
        }
        return false;
    }

    /// <summary>"Nothing was run: Shop / prod is read-only. …" for the status line or a message box.</summary>
    public static string Refusal(ConnectionProfile profile, string what) =>
        $"{what} was not run: {profile.QualifiedName} is a read-only connection. Turn off \"Read-only\" in the connection's settings to change data or schema.";

    private static string StripCommentsAndStrings(string sql)
    {
        var sb = new System.Text.StringBuilder(sql.Length);
        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                sb.Append(' ');
            }
            else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + 1;
                sb.Append(' ');
            }
            else if (c == '\'')
            {
                i++;
                while (i < sql.Length && !(sql[i] == '\'' && (i + 1 >= sql.Length || sql[i + 1] != '\''))) i += sql[i] == '\'' ? 2 : 1;
                sb.Append(" '' ");
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
