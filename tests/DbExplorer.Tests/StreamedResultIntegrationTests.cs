using DbExplorer.Core.Abstractions;
using DbExplorer.Core.Connections;
using DbExplorer.Core.Models;
using DbExplorer.Providers.MySql;
using DbExplorer.Providers.Postgres;
using DbExplorer.Providers.SqlServer;

namespace DbExplorer.Tests;

/// <summary>
/// Streaming one result set of a script (full export) against real servers: only the wanted result set reaches the
/// stream, every row of it does, the rest of the script still runs, and an open transaction's own changes are seen.
/// Skipped unless DBEXPLORER_TEST_PG / DBEXPLORER_TEST_MSSQL / DBEXPLORER_TEST_MYSQL is set to "host;port;user;password".
/// </summary>
public class StreamedResultIntegrationTests
{
    private static ConnectionProfile? Profile(string variable, string providerKey, string database)
    {
        if (Environment.GetEnvironmentVariable(variable) is not { } settings) return null;
        var p = settings.Split(';');
        return new ConnectionProfile
        {
            ProviderKey = providerKey, Host = p[0], Port = int.Parse(p[1]), UserName = p[2], Password = p[3],
            Database = database, Name = "test", TrustServerCertificate = true
        };
    }

    private static async Task<(IReadOnlyList<string> Columns, List<object?[]> Rows)> StreamAsync(
        Func<ResultSetStream, Task<long>> run, int index)
    {
        IReadOnlyList<string> columns = [];
        var rows = new List<object?[]>();
        var stream = new ResultSetStream(index, c => columns = c, rows.Add);
        var count = await run(stream);
        stream.EnsureStarted();
        Assert.Equal(rows.Count, count);
        return (columns, rows);
    }

    [SkippableFact]
    public async Task Postgres_streams_one_result_set_of_a_script()
    {
        var profile = Profile("DBEXPLORER_TEST_PG", PostgresProviderFactory.ProviderKey, "postgres");
        Skip.If(profile is null);
        await using var provider = new PostgresProvider(profile);

        var (columns, rows) = await StreamAsync(s => provider.StreamScriptAsync(
            "SELECT 1 AS first; SELECT g AS n, 'row ' || g AS label, NULL::int AS empty FROM generate_series(1, 120000) g; SELECT 3",
            null, s), 1);
        Assert.Equal(["n", "label", "empty"], columns);
        Assert.Equal(120_000, rows.Count);
        Assert.Equal([120000, "row 120000", null], rows[^1]);

        // Inside an open transaction, the stream sees what the transaction changed and has not committed.
        await using var session = await provider.BeginScriptSessionAsync(null, transactional: true);
        await session.ExecuteAsync("CREATE TEMP TABLE pending (id int); INSERT INTO pending SELECT generate_series(1, 2500);", 30);
        var (_, pending) = await StreamAsync(s => session.StreamAsync("SELECT id FROM pending ORDER BY id", s), 0);
        Assert.Equal(2500, pending.Count);
        // The session is still usable afterwards.
        Assert.Equal(1, await session.ExecuteAsync("INSERT INTO pending VALUES (0)", 30));
    }

    [SkippableFact]
    public async Task Postgres_stream_stops_on_cancel()
    {
        var profile = Profile("DBEXPLORER_TEST_PG", PostgresProviderFactory.ProviderKey, "postgres");
        Skip.If(profile is null);
        await using var provider = new PostgresProvider(profile);
        using var cts = new CancellationTokenSource();
        var seen = 0;
        var stream = new ResultSetStream(0, _ => { }, _ => { if (++seen == 1000) cts.Cancel(); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.StreamScriptAsync("SELECT g FROM generate_series(1, 100000000) g", null, stream, cts.Token));
        Assert.True(seen < 100_000_000);
        // The provider still works.
        var result = await provider.ExecuteScriptAsync("SELECT 1", null, 30);
        Assert.Equal(1, result.ResultSets[0].Rows[0][0]);
    }

    [SkippableFact]
    public async Task SqlServer_streams_one_result_set_across_batches()
    {
        var profile = Profile("DBEXPLORER_TEST_MSSQL", SqlServerProviderFactory.ProviderKey, "master");
        Skip.If(profile is null);
        await using var provider = new SqlServerProvider(profile);

        var (columns, rows) = await StreamAsync(s => provider.StreamScriptAsync("""
            SELECT 1 AS first
            GO
            SELECT TOP (60000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n, CAST(NULL AS int) AS empty
            FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            SELECT 3 AS last
            """, null, s), 1);
        Assert.Equal(["n", "empty"], columns);
        Assert.Equal(60_000, rows.Count);
        Assert.Equal(60000L, rows[^1][0]);
        Assert.Null(rows[^1][1]);

        await using var session = await provider.BeginScriptSessionAsync(null, transactional: true);
        await session.ExecuteAsync("CREATE TABLE #pending (id int); INSERT INTO #pending SELECT TOP (2500) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) FROM sys.all_objects a CROSS JOIN sys.all_objects b;", 30);
        var (_, pending) = await StreamAsync(s => session.StreamAsync("SELECT id FROM #pending", s), 0);
        Assert.Equal(2500, pending.Count);
    }

    [SkippableFact]
    public async Task MySql_streams_one_result_set_of_a_script()
    {
        var profile = Profile("DBEXPLORER_TEST_MYSQL", MySqlProviderFactory.ProviderKey, "");
        Skip.If(profile is null);
        await using var provider = new MySqlProvider(profile);

        var (columns, rows) = await StreamAsync(s => provider.StreamScriptAsync("""
            SELECT 1 AS first;
            WITH RECURSIVE seq (n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM seq WHERE n < 900)
            SELECT n, CONCAT('row ', n) AS label FROM seq;
            SELECT 3;
            """, null, s), 1);
        Assert.Equal(["n", "label"], columns);
        Assert.Equal(900, rows.Count);
        Assert.Equal("row 900", rows[^1][1]);

        // DDL commits in MySQL, so the table is made first; the rows are inserted in the open transaction.
        await provider.ExecuteScriptAsync(
            "CREATE DATABASE IF NOT EXISTS dbx_stream_test; CREATE TABLE IF NOT EXISTS dbx_stream_test.pending (id int) ENGINE=InnoDB;", null, 30);
        try
        {
            await using var session = await provider.BeginScriptSessionAsync("dbx_stream_test", transactional: true);
            await session.ExecuteAsync("INSERT INTO pending VALUES (1), (2), (3);", 30);
            var (_, pending) = await StreamAsync(s => session.StreamAsync("SELECT id FROM pending", s), 0);
            Assert.Equal(3, pending.Count);
        }
        finally
        {
            await provider.ExecuteScriptAsync("DROP DATABASE dbx_stream_test", null, 30);
        }
    }
}
