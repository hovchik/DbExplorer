using System.Text.RegularExpressions;
using DbExplorer.Core.Connections;

namespace DbExplorer.Application.Connections;

/// <summary>
/// The app side of a read-only connection: what counts as a write, and the message shown when one is refused.
/// PostgreSQL connections are also opened read-only on the server; SQL Server has no such session setting.
/// </summary>
public static class ReadOnlyGuard
{
    private static readonly Regex WriteKeyword = new(
        @"\b(INSERT|UPDATE|DELETE|DROP|ALTER|TRUNCATE|CREATE|MERGE|EXEC|EXECUTE|CALL|GRANT|REVOKE|DENY|INTO|BACKUP|RESTORE|DBCC|KILL|VACUUM|REINDEX|CLUSTER|COPY|LOCK|REFRESH|COMMENT|SECURITY|LABEL|IMPORT|BULK)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(2));

    /// <summary>
    /// Whether <paramref name="sql"/> may change anything. Comments and string literals are ignored, so
    /// <c>WHERE note = 'delete me'</c> still reads.
    /// </summary>
    public static bool MayWrite(string sql) => WriteKeyword.IsMatch(StripCommentsAndStrings(sql));

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
