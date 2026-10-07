using DbExplorer.Application.Lab;

namespace DbExplorer.Tests;

public class SqlAnatomyTests
{
    [Fact]
    public void ParsesClausesAndJoins()
    {
        var a = SqlAnatomy.ParseSelect("""
            SELECT TOP (10) o.Id, c.Name
            FROM dbo.Orders o
            INNER JOIN dbo.Customers AS c ON c.Id = o.CustomerId AND c.Active = 1
            LEFT JOIN dbo.Notes n ON n.OrderId = o.Id
            WHERE o.Status = 'open' AND o.Total BETWEEN 1 AND 10 AND (o.A = 1 OR o.B = 2)
            GROUP BY o.Id, c.Name
            HAVING COUNT(*) > 1
            ORDER BY o.Id;
            """)!;

        Assert.Equal("TOP (10)", a.Modifiers);
        Assert.Equal(3, a.From.Count);
        Assert.Equal(("", "dbo.Orders", "o"), (a.From[0].Join, a.From[0].Name, a.From[0].Alias));
        Assert.Equal(("INNER JOIN", "c"), (a.From[1].Join, a.From[1].Alias));
        Assert.Equal("c.Id = o.CustomerId AND c.Active = 1", a.From[1].Condition);
        Assert.Equal("LEFT JOIN", a.From[2].Join);
        Assert.False(a.From[2].CanEliminate);
        Assert.True(a.From[1].CanEliminate);
        Assert.Equal("o.Id, c.Name", a.GroupBy);
        Assert.Equal("COUNT(*) > 1", a.Having);
        Assert.Equal("o.Id", a.OrderBy);
        Assert.True(a.LimitsRows);

        var conjuncts = SqlAnatomy.SplitConjuncts(a.Where!);
        Assert.Equal(["o.Status = 'open'", "o.Total BETWEEN 1 AND 10", "(o.A = 1 OR o.B = 2)"], conjuncts);
        Assert.Equal("dbo.Orders o INNER JOIN dbo.Customers AS c ON c.Id = o.CustomerId AND c.Active = 1", a.FromClause(2));
    }

    [Fact]
    public void KeepsCteAndCommaJoinsAndLimit()
    {
        var a = SqlAnatomy.ParseSelect("WITH x AS (SELECT id FROM t WHERE a = 1) SELECT * FROM x, y AS z WHERE x.id = z.id LIMIT 5")!;
        Assert.StartsWith("WITH x AS", a.Prefix);
        Assert.Equal(2, a.From.Count);
        Assert.Equal((",", "z"), (a.From[1].Join, a.From[1].Alias));
        Assert.Equal("x.id = z.id", a.Where);
        Assert.Equal("LIMIT 5", a.Tail);
    }

    [Fact]
    public void RejectsWhatItCannotProbe()
    {
        Assert.True(SqlAnatomy.ParseSelect("SELECT a FROM t UNION SELECT a FROM u")!.HasSetOperation);
        Assert.Null(SqlAnatomy.ParseSelect("SELECT a INTO #x FROM t"));
        Assert.Null(SqlAnatomy.ParseSelect("UPDATE t SET a = 1"));
        Assert.Null(SqlAnatomy.ParseSelect("SELECT 1; SELECT 2"));
    }

    [Fact]
    public void AndInsideCaseAndParenthesesStays()
    {
        var parts = SqlAnatomy.SplitConjuncts("CASE WHEN a = 1 AND b = 2 THEN 1 END = 1 AND f(x AND y) AND c = 'x AND y'");
        Assert.Equal(["CASE WHEN a = 1 AND b = 2 THEN 1 END = 1", "f(x AND y)", "c = 'x AND y'"], parts);
    }

    [Fact]
    public void FindsQualifiedColumns()
    {
        var cols = SqlAnatomy.QualifiedColumns("o.[Customer Id] = c.Id AND dbo.f(o.X) > 1 AND db.s.t.col = 1");
        Assert.Contains(("o", "Customer Id"), cols);
        Assert.Contains(("c", "Id"), cols);
        Assert.Contains(("o", "X"), cols);
        Assert.Contains(("t", "col"), cols);
        Assert.DoesNotContain(("dbo", "f"), cols);
    }

    [Theory]
    [InlineData("UPDATE dbo.Orders SET Status = 'x' WHERE Id = 5", "dbo.Orders", null, null, "Id = 5")]
    [InlineData("UPDATE o SET Status = 'x' FROM dbo.Orders o JOIN dbo.C c ON c.Id = o.CId WHERE c.A = 1", "o", null, "dbo.Orders o JOIN dbo.C c ON c.Id = o.CId", "c.A = 1")]
    [InlineData("UPDATE orders AS o SET status = 'x' FROM customers c WHERE c.id = o.customer_id RETURNING *", "orders", "o", "customers c", "c.id = o.customer_id")]
    [InlineData("DELETE FROM dbo.Orders WHERE Id IN (1, 2)", "dbo.Orders", null, null, "Id IN (1, 2)")]
    [InlineData("DELETE o FROM dbo.Orders o JOIN x ON x.id = o.id", "o", null, "dbo.Orders o JOIN x ON x.id = o.id", null)]
    [InlineData("DELETE FROM orders o USING customers c WHERE c.id = o.cid", "orders", "o", "customers c", "c.id = o.cid")]
    [InlineData("WITH d AS (SELECT 1 AS id) DELETE FROM t WHERE id IN (SELECT id FROM d)", "t", null, null, "id IN (SELECT id FROM d)")]
    public void ParsesUpdatesAndDeletes(string sql, string target, string? alias, string? from, string? where)
    {
        var d = SqlAnatomy.ParseDml(sql)!;
        Assert.Equal(target, d.Target);
        Assert.Equal(alias, d.TargetAlias);
        Assert.Equal(from, d.From);
        Assert.Equal(where, d.Where);
    }

    [Fact]
    public void ParsesInsertTargetAndOutputPosition()
    {
        const string sql = "INSERT INTO dbo.T (a, b) VALUES (1, 2)";
        var d = SqlAnatomy.ParseDml(sql)!;
        Assert.Equal(DmlKind.Insert, d.Kind);
        Assert.Equal("dbo.T", d.Target);
        Assert.Equal("VALUES (1, 2)", sql[d.InsertOutputAt..]);

        var pg = SqlAnatomy.ParseDml("INSERT INTO t AS x SELECT * FROM u RETURNING id")!;
        Assert.Equal(("t", "x"), (pg.Target, pg.TargetAlias));
        Assert.True(pg.ReturningAt > 0);
    }

    [Fact]
    public void KindOfSeesThroughCtes()
    {
        Assert.Equal(DmlKind.Update, SqlAnatomy.KindOf("WITH a AS (SELECT 1) UPDATE t SET x = 1"));
        Assert.Equal(DmlKind.Select, SqlAnatomy.KindOf("  select 1"));
        Assert.Equal(DmlKind.Merge, SqlAnatomy.KindOf("MERGE INTO t USING s ON t.id = s.id WHEN MATCHED THEN DELETE;"));
        Assert.Equal(DmlKind.Other, SqlAnatomy.KindOf("ALTER TABLE t ADD c int"));
    }

    [Theory]
    [InlineData("COMMIT", "COMMIT")]
    [InlineData("commit transaction", "COMMIT TRANSACTION")]
    [InlineData("COMMIT PREPARED 'x'", "COMMIT PREPARED")]
    [InlineData("ROLLBACK TO SAVEPOINT a", "ROLLBACK")]
    [InlineData("ABORT", "ABORT")]
    [InlineData("END", "END")]
    [InlineData("END WORK", "END WORK")]
    [InlineData("BEGIN", "BEGIN")]
    [InlineData("BEGIN;", "BEGIN")]
    [InlineData("BEGIN TRAN", "BEGIN TRAN")]
    [InlineData("BEGIN ISOLATION LEVEL SERIALIZABLE", "BEGIN ISOLATION")]
    [InlineData("BEGIN DISTRIBUTED TRANSACTION", "BEGIN DISTRIBUTED")]
    [InlineData("/* go */ -- now\n START TRANSACTION", "START TRANSACTION")]
    [InlineData("PREPARE TRANSACTION 'x'", "PREPARE TRANSACTION")]
    [InlineData("BEGIN UPDATE t SET a = 1 END", null)]
    [InlineData("BEGIN TRY SELECT 1 END TRY BEGIN CATCH END CATCH", null)]
    [InlineData("UPDATE t SET note = 'COMMIT'", null)]
    [InlineData("-- COMMIT\nSELECT 1", null)]
    [InlineData("SAVE TRANSACTION a", null)]
    public void Recognises_transaction_control(string sql, string? keyword) =>
        Assert.Equal(keyword, SqlAnatomy.TransactionControl(sql));
}
