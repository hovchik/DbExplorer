using DbExplorer.Application.Query;

namespace DbExplorer.Tests;

public class ReadOnlyScriptAnalyzerTests
{
    [Theory]
    [InlineData("SELECT * FROM dbo.Orders")]
    [InlineData("select * from sales.orders order by id;")]
    [InlineData("WITH x AS (SELECT 1 AS n) SELECT n FROM x;")]
    [InlineData("SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;\nDECLARE @d date = '2024-01-01';\nSELECT * FROM t WHERE d > @d")]
    [InlineData("SELECT * FROM a;\nSELECT * FROM b;")]
    [InlineData("SELECT [update], \"into\" FROM t -- insert into x\n/* delete */")]
    [InlineData("SELECT * FROM t ORDER BY id OFFSET 0 ROWS FETCH NEXT 10 ROWS ONLY")]
    [InlineData("SET search_path TO app;\nSHOW search_path;\nVALUES (1), (2);\nTABLE app.users;")]
    [InlineData("SELECT 'UPDATE t SET x = 1' AS txt")]
    [InlineData("SELECT 1\nGO\nSELECT 2")]
    public void Reads_are_read_only(string sql) => Assert.NotNull(ReadOnlyScriptAnalyzer.Analyze(sql));

    [Theory]
    [InlineData("")]
    [InlineData("   -- nothing\n")]
    [InlineData("SET NOCOUNT ON;")]
    [InlineData("DECLARE @x int = 1;")]
    [InlineData("UPDATE t SET x = 1")]
    [InlineData("SELECT 1\nUPDATE t SET x = 1")]
    [InlineData("SELECT * INTO #tmp FROM t")]
    [InlineData("SELECT * FROM t;\nDELETE FROM t WHERE id = 1;")]
    [InlineData("WITH d AS (DELETE FROM t RETURNING *) SELECT * FROM d")]
    [InlineData("EXEC dbo.GetOrders")]
    [InlineData("SELECT 1 EXEC dbo.Cleanup")]
    [InlineData("BEGIN TRAN; SELECT 1; COMMIT;")]
    [InlineData("IF 1 = 1 SELECT 1")]
    [InlineData("SELECT * FROM t FOR UPDATE")]
    [InlineData("DECLARE c CURSOR FOR SELECT 1")]
    [InlineData("SET ROWCOUNT 5; SELECT * FROM t")]
    [InlineData("CREATE TABLE t (id int); SELECT * FROM t;")]
    [InlineData("(SELECT 1) UNION (SELECT 2)")]
    [InlineData("VACUUM t; SELECT 1;")]
    public void Anything_else_is_not(string sql) => Assert.Null(ReadOnlyScriptAnalyzer.Analyze(sql));

    [Fact]
    public void Statements_keep_their_text_offset_and_whether_they_return_rows()
    {
        const string sql = "SET search_path TO app;\n\n  SELECT * FROM users;\nSHOW search_path;";
        var script = ReadOnlyScriptAnalyzer.Analyze(sql)!;

        Assert.Equal(
            [("SET search_path TO app;", 0, false), ("SELECT * FROM users;", 27, true), ("SHOW search_path;", 48, false)],
            script.Statements.Select(s => (s.Text, s.Offset, s.ReturnsRows)));
        Assert.All(script.Statements, s => Assert.Equal(s.Text, sql.Substring(s.Offset, s.Text.Length)));
    }

    [Fact]
    public void Only_a_row_limit_hands_the_statements_to_the_provider()
    {
        Assert.NotNull(QueryExecutionService.ReadOnlyFor("SELECT 1", 10_000));
        Assert.Null(QueryExecutionService.ReadOnlyFor("SELECT 1", int.MaxValue));
        Assert.Null(QueryExecutionService.ReadOnlyFor("SELECT 1", 0));
        Assert.Null(QueryExecutionService.ReadOnlyFor("DELETE FROM t", 10_000));
    }
}
