using DbExplorer.Core.Connections;
using DbExplorer.Core.Search;
using Npgsql;

namespace DbExplorer.Providers.Postgres;

public static class PostgresSql
{
    public static readonly HashSet<string> TextTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text", "varchar", "bpchar", "name", "citext", "char"
    };

    public static readonly HashSet<string> NumericTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "int2", "int4", "int8", "numeric", "float4", "float8"
    };

    public static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    public static string QuoteFullName(string schema, string name) => Quote(schema) + "." + Quote(name);

    /// <summary>Escapes LIKE wildcards using the default backslash escape character.</summary>
    public static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

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
    /// Transaction-local settings: read-only, never wait long for a lock, never run forever.
    /// Postgres MVCC readers never block writers, so no dirty reads are needed.
    /// </summary>
    public static string TransactionSettings(int statementTimeoutMs, int lockTimeoutMs) =>
        "SET TRANSACTION READ ONLY; " +
        $"SET LOCAL statement_timeout = {Math.Max(0, statementTimeoutMs)}; " +
        $"SET LOCAL lock_timeout = {Math.Max(0, lockTimeoutMs)};";

    public static string BuildConnectionString(ConnectionProfile p, string? databaseOverride = null)
    {
        var b = new NpgsqlConnectionStringBuilder
        {
            Host = p.Host,
            Port = p.Port ?? 5432,
            Database = databaseOverride ?? p.Database,
            ApplicationName = "DbExplorer",
            Timeout = 15,
            SslMode = p.Encrypt ? SslMode.Require : SslMode.Prefer
        };

        if (!p.IntegratedSecurity)
        {
            b.Username = p.UserName;
            b.Password = p.Password;
        }

        return b.ConnectionString;
    }
}
