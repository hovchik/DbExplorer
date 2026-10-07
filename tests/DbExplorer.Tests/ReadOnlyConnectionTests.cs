using DbExplorer.Application.Connections;
using DbExplorer.Core.Connections;
using DbExplorer.Providers.Postgres;

namespace DbExplorer.Tests;

public class ReadOnlyConnectionTests
{
    [Theory]
    [InlineData("SELECT * FROM orders WHERE id = 1", false)]
    [InlineData("WITH x AS (SELECT 1 AS a) SELECT a FROM x", false)]
    [InlineData("SELECT * FROM t WHERE note = 'please delete me' -- update later", false)]
    [InlineData("SELECT updated_at, created_by FROM t /* DROP TABLE t */", false)]
    [InlineData("UPDATE t SET a = 1", true)]
    [InlineData("SELECT * INTO backup_t FROM t", true)]
    [InlineData("EXEC dbo.Cleanup", true)]
    [InlineData("truncate table t", true)]
    [InlineData("SELECT 'it''s' AS a; DELETE FROM t", true)]
    public void Writes_are_recognized_outside_comments_and_strings(string sql, bool writes) =>
        Assert.Equal(writes, ReadOnlyGuard.MayWrite(sql));

    [Theory]
    // SQL Server runs a bare procedure name as the first statement of a batch.
    [InlineData("sp_rename 'dbo.Orders', 'x'")]
    [InlineData("dbo.usp_Purge")]
    [InlineData("[dbo].[usp_Purge] 1")]
    [InlineData("SELECT 1\nGO\nusp_Purge")]
    [InlineData("-- cleanup\nsp_executesql N'DELETE FROM t'")]
    [InlineData("EXEC (@sql)")]
    [InlineData("EXECUTE('DELETE FROM t')")]
    [InlineData("EXEC sp_executesql N'DELETE FROM t'")]
    [InlineData("DISABLE TRIGGER trg ON dbo.Orders")]
    [InlineData("enable trigger all on database")]
    [InlineData("UPDATETEXT t.c @ptr 0 NULL 'x'")]
    [InlineData("WRITETEXT t.c @ptr 'x'")]
    [InlineData("SHUTDOWN WITH NOWAIT")]
    [InlineData("RECONFIGURE")]
    // MySQL
    [InlineData("REPLACE t SET a = 1")]
    [InlineData("RENAME TABLE a TO b")]
    [InlineData("SET GLOBAL max_connections = 10")]
    [InlineData("SET @@GLOBAL.max_connections = 10")]
    [InlineData("SET @a = 1, GLOBAL max_connections = 10")]
    [InlineData("LOAD DATA INFILE 'x' INTO TABLE t")]
    public void Procedure_calls_and_admin_statements_count_as_writes(string sql) =>
        Assert.True(ReadOnlyGuard.MayWrite(sql));

    [Theory]
    [InlineData("SET NOCOUNT ON; SELECT * FROM t")]
    [InlineData("DECLARE @n int = 1; SELECT @n; PRINT 'done'")]
    [InlineData("SHOW TABLES")]
    [InlineData("EXPLAIN SELECT * FROM t")]
    [InlineData("SELECT REPLACE(name, 'a', 'b'), @@GLOBAL.max_connections FROM t")]
    [InlineData("SET SESSION sql_mode = 'ANSI'; SELECT 1")]
    [InlineData("IF 1 = 1 BEGIN SELECT 1 END")]
    [InlineData("SELECT 1\nGO\nSELECT 2")]
    [InlineData("(SELECT 1) UNION (SELECT 2)")]
    [InlineData("USE shop; SELECT * FROM dbo.Orders")]
    public void Reads_and_session_settings_do_not(string sql) =>
        Assert.False(ReadOnlyGuard.MayWrite(sql));

    [Fact]
    public void Refusal_names_the_connection_and_how_to_allow_writes()
    {
        var profile = new ConnectionProfile { Name = "prod", Folder = "Shop", ReadOnly = true };
        var message = ReadOnlyGuard.Refusal(profile, "The script");
        Assert.StartsWith("The script was not run: Shop / prod is a read-only connection.", message);
        Assert.EndsWith("(read-only)", profile.ToString());
    }

    [Fact]
    public void Postgres_opens_read_only_connections_read_only_on_the_server()
    {
        var profile = new ConnectionProfile { Host = "db", Database = "shop", UserName = "u", ReadOnly = true };
        Assert.Contains("default_transaction_read_only=on", PostgresSql.BuildConnectionString(profile));
        profile.ReadOnly = false;
        Assert.DoesNotContain("default_transaction_read_only", PostgresSql.BuildConnectionString(profile));
    }
}
