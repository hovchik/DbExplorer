namespace DbExplorer.Application.Query;

/// <summary>
/// Whether a script can change the catalog (tables, views, routines, columns, indexes, keys…), so the metadata is
/// read again in the background after it runs: CREATE / ALTER / DROP of an object, MySQL's RENAME TABLE, PostgreSQL's
/// COMMENT ON, SQL Server's sp_rename, and SELECT … INTO a new table. Temporary tables (#t, CREATE TEMP TABLE) and
/// server-level principals (logins, users, roles) do not count, and neither do comments and strings.
/// </summary>
public static class SchemaChanges
{
    private static readonly HashSet<string> Verbs = new(StringComparer.OrdinalIgnoreCase) { "CREATE", "ALTER", "DROP" };

    /// <summary>What follows CREATE / ALTER / DROP when the statement is about the server or its principals, not
    /// about anything the catalog snapshot holds.</summary>
    private static readonly HashSet<string> NotCatalog = new(StringComparer.OrdinalIgnoreCase)
    {
        "LOGIN", "USER", "ROLE", "GROUP", "SESSION", "SYSTEM", "SERVER", "CREDENTIAL", "RESOURCE", "WORKLOAD",
        "TEMP", "TEMPORARY", "APPLICATION", "MASTER", "CERTIFICATE", "ASYMMETRIC", "SYMMETRIC", "AVAILABILITY", "ENDPOINT"
    };

    private static readonly HashSet<string> Procedures = new(StringComparer.OrdinalIgnoreCase) { "sp_rename" };

    private static readonly HashSet<string> InsertWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "MERGE", "REPLACE", "UPSERT"
    };

    public static bool Affects(string sql)
    {
        var words = SqlLexer.Tokenize(sql).Where(t => !t.IsTrivia).ToList();
        var statementHasSelect = false;
        var statementHasInsert = false;
        for (var i = 0; i < words.Count; i++)
        {
            var t = words[i];
            if (t.Kind == SqlTokenKind.Semicolon || t.Is("GO"))
            {
                statementHasSelect = statementHasInsert = false;
                continue;
            }
            if (t.Kind != SqlTokenKind.Word) continue;
            // a.drop or b.create is a column, not a statement.
            if (i > 0 && words[i - 1].Kind == SqlTokenKind.Dot) continue;
            if (Procedures.Contains(t.Text)) return true;

            var next = i + 1 < words.Count ? words[i + 1] : default;
            var after = i + 2 < words.Count ? words[i + 2] : default;

            if (Verbs.Contains(t.Text) && next.Kind == SqlTokenKind.Word && IsCatalogObject(t, next, after, words, i)) return true;
            if (t.Is("RENAME") && (next.Is("TABLE") || next.Is("TABLES"))) return true;
            if (t.Is("COMMENT") && next.Is("ON")) return true;

            if (t.Is("SELECT")) statementHasSelect = true;
            else if (InsertWords.Contains(t.Text)) statementHasInsert = true;
            else if (t.Is("INTO") && statementHasSelect && !statementHasInsert && IsNewTable(next, after)) return true;
        }
        return false;
    }

    private static bool IsCatalogObject(SqlToken verb, SqlToken next, SqlToken after, IReadOnlyList<SqlToken> words, int i)
    {
        if (NotCatalog.Contains(next.Text)) return false;
        // CREATE LOCAL / GLOBAL TEMPORARY TABLE
        if ((next.Is("LOCAL") || next.Is("GLOBAL")) && (after.Is("TEMP") || after.Is("TEMPORARY"))) return false;
        // DROP EVENT SESSION / ALTER EVENT SESSION (SQL Server tracing), not a MySQL event
        if (next.Is("EVENT") && after.Is("SESSION")) return false;
        // ALTER DEFAULT PRIVILEGES only changes future grants.
        if (verb.Is("ALTER") && next.Is("DEFAULT") && after.Is("PRIVILEGES")) return false;
        // CREATE / DROP TABLE #temp (also DROP TABLE IF EXISTS #temp)
        if (next.Is("TABLE"))
        {
            var j = i + 2;
            if (j + 1 < words.Count && words[j].Is("IF") && words[j + 1].Is("EXISTS")) j += 2;
            if (j + 2 < words.Count && words[j].Is("IF") && words[j + 1].Is("NOT") && words[j + 2].Is("EXISTS")) j += 3;
            if (j < words.Count && words[j].Text.StartsWith('#')) return false;
        }
        return true;
    }

    /// <summary>SELECT … INTO new_table (not a variable, a temp table, or MySQL's INTO OUTFILE).</summary>
    private static bool IsNewTable(SqlToken next, SqlToken after)
    {
        if (next.Kind == SqlTokenKind.QuotedIdentifier) return true;
        if (next.Kind != SqlTokenKind.Word || next.Text.StartsWith('#') || next.Text.StartsWith('@')) return false;
        if (next.Is("OUTFILE") || next.Is("DUMPFILE")) return false;
        if (next.Is("TEMP") || next.Is("TEMPORARY")) return false;
        if (next.Is("UNLOGGED")) return after.Kind is SqlTokenKind.Word or SqlTokenKind.QuotedIdentifier;
        return true;
    }
}
