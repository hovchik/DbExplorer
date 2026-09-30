using DbExplorer.Core.Models;

namespace DbExplorer.Application.Query;

/// <summary>
/// Recognises scripts that only read, so a row limit can stop each result set on the server (see
/// <see cref="ReadOnlyScript"/>). Deliberately conservative: anything it does not know is safe to cut short — a write,
/// DDL, a procedure call, SELECT … INTO, a cursor, a control-flow block, transaction control — makes the whole script
/// "not read-only", and it then runs as before (every row read, the extra ones discarded).
/// </summary>
public static class ReadOnlyScriptAnalyzer
{
    /// <summary>Statements that return rows; the only ones a provider limits.</summary>
    private static readonly HashSet<string> QueryStarts = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "WITH", "VALUES", "TABLE"
    };

    /// <summary>Other statements allowed in a read-only script: declarations, session settings, messages.</summary>
    private static readonly HashSet<string> OtherStarts = new(StringComparer.OrdinalIgnoreCase)
    {
        "DECLARE", "SET", "PRINT", "USE", "SHOW"
    };

    /// <summary>
    /// Words that make a statement more than a read wherever they appear as bare words ([quoted] names do not count).
    /// T-SQL statements need no separator, so "SELECT 1 UPDATE t SET …" is one range here: every statement verb that
    /// writes or changes the session's flow is listed. PostgreSQL statements are ';'-separated, so its own commands
    /// (VACUUM, COPY, LOCK, …) are already caught by the allowed first words.
    /// </summary>
    private static readonly HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "UPDATE", "DELETE", "MERGE", "INTO", "UPDATETEXT", "WRITETEXT", "BULK",
        "EXEC", "EXECUTE",
        "CREATE", "ALTER", "DROP", "TRUNCATE", "DISABLE", "ENABLE",
        "GRANT", "REVOKE", "DENY", "SETUSER",
        "BEGIN", "COMMIT", "ROLLBACK", "SAVE",
        "CURSOR", "IF", "WHILE", "GOTO",
        "BACKUP", "RESTORE", "DBCC", "KILL", "SHUTDOWN", "RECONFIGURE", "CHECKPOINT",
        "ROWCOUNT"
    };

    /// <summary>The script's statements when every one of them only reads and at least one returns rows; otherwise null.</summary>
    public static ReadOnlyScript? Analyze(string script)
    {
        if (string.IsNullOrWhiteSpace(script)) return null;

        var statements = new List<ReadOnlyStatement>();
        foreach (var range in SqlScriptTools.SplitStatements(script, splitOnBlankLines: false))
        {
            var text = range.Of(script);
            var tokens = SqlLexer.Tokenize(text).Where(t => !t.IsTrivia).ToList();
            if (tokens.Count == 0 || (tokens.Count == 1 && tokens[0].Kind == SqlTokenKind.Semicolon)) continue;

            var first = tokens[0];
            var returnsRows = first.Kind == SqlTokenKind.Word && QueryStarts.Contains(first.Text);
            if (!returnsRows && !(first.Kind == SqlTokenKind.Word && OtherStarts.Contains(first.Text))) return null;
            if (tokens.Any(t => t.Kind == SqlTokenKind.Word && Blocked.Contains(t.Text))) return null;

            statements.Add(new ReadOnlyStatement(text, range.Start, returnsRows));
        }

        return statements.Any(s => s.ReturnsRows) ? new ReadOnlyScript(statements) : null;
    }
}
