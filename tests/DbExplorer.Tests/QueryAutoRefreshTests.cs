using DbExplorer.Application.Query;

namespace DbExplorer.Tests;

public class QueryAutoRefreshTests
{
    [Theory]
    [InlineData("SELECT * FROM dbo.Orders")]
    [InlineData("SELECT COUNT(*) FROM Orders WHERE Status = 1; SELECT TOP 10 * FROM Logs ORDER BY Id DESC")]
    [InlineData("WITH recent AS (SELECT * FROM Orders) SELECT * FROM recent")]
    public void Reads_can_be_repeated(string sql) => Assert.Null(QueryAutoRefresh.WhyNotRepeatable(sql));

    [Theory]
    [InlineData("UPDATE Orders SET Status = 2 WHERE Id = 1")]
    [InlineData("DELETE FROM Logs")]
    [InlineData("SELECT 1; INSERT INTO Audit (At) VALUES (GETDATE())")]
    [InlineData("EXEC dbo.usp_Report")]
    [InlineData("TRUNCATE TABLE Logs")]
    public void Writes_are_never_repeated(string sql) => Assert.NotNull(QueryAutoRefresh.WhyNotRepeatable(sql));

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void Nothing_run_yet_has_nothing_to_repeat(string? sql) => Assert.NotNull(QueryAutoRefresh.WhyNotRepeatable(sql));

    [Fact]
    public void Intervals_are_ascending_and_include_the_default()
    {
        var periods = QueryAutoRefresh.Intervals.Select(i => i.Period).ToList();
        Assert.Equal(periods.Order(), periods);
        Assert.Contains(QueryAutoRefresh.DefaultInterval, QueryAutoRefresh.Intervals);
        Assert.Equal(TimeSpan.FromSeconds(5), periods[0]);
        Assert.Equal(TimeSpan.FromMinutes(5), periods[^1]);
    }
}
