using DbExplorer.Core.Connections;
using DbExplorer.Core.Search;
using Microsoft.Data.SqlClient;

namespace DbExplorer.Providers.SqlServer;

/// <summary>Dialect helpers for SQL Server.</summary>
public static class SqlServerSql
{
    /// <summary>
    /// Application names split metadata and data-search traffic into separate connection pools.
    /// Every batch re-applies its session settings, so pooled connections never leak settings
    /// into code that did not expect them.
    /// </summary>
    public const string MetaAppName = "DbExplorer.Meta";
    public const string SearchAppName = "DbExplorer.Search";

    public static readonly HashSet<string> TextTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "char", "varchar", "nchar", "nvarchar", "text", "ntext", "sysname"
    };

    public static readonly HashSet<string> NumericTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "tinyint", "smallint", "int", "bigint", "decimal", "numeric", "money", "smallmoney", "float", "real"
    };

    public static string Quote(string identifier) => "[" + identifier.Replace("]", "]]") + "]";

    public static string QuoteFullName(string schema, string name) => Quote(schema) + "." + Quote(name);

    /// <summary>Escapes LIKE wildcards using bracket syntax.</summary>
    public static string EscapeLike(string value) =>
        value.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");

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
    /// Session settings that make every query non-blocking:
    /// dirty reads (no shared locks), fail fast on schema locks, and always lose a deadlock.
    /// </summary>
    public static string SessionPrefix(int lockTimeoutMs) =>
        "SET NOCOUNT ON; SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED; " +
        $"SET DEADLOCK_PRIORITY LOW; SET LOCK_TIMEOUT {Math.Max(0, lockTimeoutMs)};\n";

    public static string BuildConnectionString(ConnectionProfile p, string applicationName, string? databaseOverride = null)
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = p.Port is int port ? $"{p.Host},{port}" : p.Host,
            InitialCatalog = databaseOverride ?? p.Database,
            IntegratedSecurity = p.IntegratedSecurity,
            TrustServerCertificate = p.TrustServerCertificate,
            Encrypt = p.Encrypt ? SqlConnectionEncryptOption.Mandatory : SqlConnectionEncryptOption.Optional,
            ApplicationName = applicationName,
            ApplicationIntent = p.ReadOnlyIntent ? ApplicationIntent.ReadOnly : ApplicationIntent.ReadWrite,
            ConnectTimeout = 15,
            MultipleActiveResultSets = false
        };

        if (!p.IntegratedSecurity)
        {
            b.UserID = p.UserName ?? "";
            b.Password = p.Password ?? "";
        }

        return b.ConnectionString;
    }
}
