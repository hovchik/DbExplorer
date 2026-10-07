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
