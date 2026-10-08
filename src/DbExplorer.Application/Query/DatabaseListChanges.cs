namespace DbExplorer.Application.Query;

/// <summary>
/// Whether a script can change the server's list of databases, so the database pickers re-read it after it runs:
/// CREATE / DROP / ALTER DATABASE (ALTER renames, or takes one offline), SQL Server's RESTORE DATABASE and
/// sp_renamedb / sp_attach_db / sp_detach_db, and on MySQL CREATE / DROP SCHEMA (a schema is a database there).
/// Comments and strings never count.
/// </summary>
public static class DatabaseListChanges
{
    private static readonly HashSet<string> Procedures = new(StringComparer.OrdinalIgnoreCase)
    {
        "sp_renamedb", "sp_attach_db", "sp_attach_single_file_db", "sp_detach_db"
    };

    public static bool Affects(string sql, string providerKey)
    {
        var words = SqlLexer.Tokenize(sql).Where(t => !t.IsTrivia).ToList();
        var mySql = providerKey == Copy.SqlDialect.MySqlKey;
        for (var i = 0; i < words.Count; i++)
        {
            var t = words[i];
            if (t.Kind == SqlTokenKind.Word && Procedures.Contains(t.Text)) return true;
            if (i + 1 >= words.Count) break;
            var next = words[i + 1];
            if ((t.Is("CREATE") || t.Is("DROP") || t.Is("ALTER") || t.Is("RESTORE")) &&
                (next.Is("DATABASE") || (mySql && next.Is("SCHEMA") && !t.Is("RESTORE"))))
                return true;
        }
        return false;
    }
}
