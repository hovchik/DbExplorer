using DbExplorer.Core.Connections;
using DbExplorer.Core.Search;
using Microsoft.Data.Sqlite;

namespace DbExplorer.Providers.Sqlite;

/// <summary>SQL text, names and connection strings for SQLite files.</summary>
public static class SqliteSql
{
    /// <summary>The name SQLite gives the opened file. It plays both the database and the schema in the rest of the app.</summary>
    public const string Main = "main";

    /// <summary>File extensions offered by the file pickers and recognised on the command line.</summary>
    public static readonly string[] FileExtensions = ["db", "sqlite", "sqlite3", "db3", "s3db", "sl3"];

    public static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    public static string QuoteFullName(string schema, string name) =>
        string.IsNullOrEmpty(schema) ? Quote(name) : Quote(schema) + "." + Quote(name);

    public static string Literal(string value) => "'" + value.Replace("'", "''") + "'";

    /// <summary>The schema an object lives in: "main" unless another file is attached under its own name.</summary>
    public static string SchemaOf(string? schema, string? database) =>
        !string.IsNullOrEmpty(schema) ? schema : !string.IsNullOrEmpty(database) ? database : Main;

    /// <summary>SQLite's type affinity of a declared column type (section 3.1 of the datatype docs).</summary>
    public static SqliteAffinity Affinity(string? declaredType)
    {
        var t = (declaredType ?? "").ToUpperInvariant();
        if (t.Contains("INT", StringComparison.Ordinal)) return SqliteAffinity.Integer;
        if (t.Contains("CHAR", StringComparison.Ordinal) || t.Contains("CLOB", StringComparison.Ordinal) ||
            t.Contains("TEXT", StringComparison.Ordinal)) return SqliteAffinity.Text;
        if (t.Length == 0 || t.Contains("BLOB", StringComparison.Ordinal)) return SqliteAffinity.Blob;
        if (t.Contains("REAL", StringComparison.Ordinal) || t.Contains("FLOA", StringComparison.Ordinal) ||
            t.Contains("DOUB", StringComparison.Ordinal)) return SqliteAffinity.Real;
        return SqliteAffinity.Numeric;
    }

    /// <summary>The declared type without its size, lower case: "varchar(50)" → "varchar", "" for an untyped column.</summary>
    public static string BaseType(string? declaredType)
    {
        var t = (declaredType ?? "").Trim();
        var open = t.IndexOf('(');
        return (open >= 0 ? t[..open] : t).Trim().ToLowerInvariant();
    }

    /// <summary>
    /// The path a connection's Host holds, made absolute: ~ and environment variables expanded, quotes around a pasted
    /// path removed, and a "file:" URI or a "Data Source=" string reduced to its path.
    /// </summary>
    public static string FilePath(string? host)
    {
        var path = (host ?? "").Trim().Trim('"', '\'');
        if (path.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("DataSource=", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("Filename=", StringComparison.OrdinalIgnoreCase))
        {
            try { path = new SqliteConnectionStringBuilder(path).DataSource; }
            catch (ArgumentException) { /* not a connection string after all */ }
        }
        if (path.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile)
            path = uri.LocalPath;
        if (path == ":memory:") return path;
        if (path.StartsWith('~'))
            path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];
        path = Environment.ExpandEnvironmentVariables(path);
        return path.Length == 0 ? "" : Path.GetFullPath(path);
    }

    /// <summary>
    /// Opens the file without creating it (a mistyped path fails instead of leaving an empty file behind); a read-only
    /// connection opens it read-only, so SQLite itself refuses writes. Foreign keys are enforced, as they are on the
    /// other engines; SQLite leaves them off unless asked. Encrypted files (SQLCipher) are not supported: the bundled
    /// SQLite has no encryption, so the profile's password is not used.
    /// </summary>
    public static string BuildConnectionString(ConnectionProfile profile, bool readOnly = false, bool create = false)
    {
        var path = FilePath(profile.Host);
        if (path.Length == 0) throw new InvalidOperationException("Choose the SQLite database file.");
        return new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = profile.ReadOnly || readOnly ? SqliteOpenMode.ReadOnly
                : create ? SqliteOpenMode.ReadWriteCreate
                : SqliteOpenMode.ReadWrite,
            ForeignKeys = true,
            DefaultTimeout = 30,
            Pooling = true
        }.ToString();
    }

    /// <summary>Escapes LIKE wildcards for <c>ESCAPE '!'</c>.</summary>
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
}

public enum SqliteAffinity { Text, Numeric, Integer, Real, Blob }
