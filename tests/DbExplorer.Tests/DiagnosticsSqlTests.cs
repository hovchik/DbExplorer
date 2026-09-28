using DbExplorer.Core.Models;
using DbExplorer.Providers.Postgres;
using DbExplorer.Providers.SqlServer;

namespace DbExplorer.Tests;

public class DiagnosticsSqlTests
{
    private static DbColumn Col(string name, string baseType) => new() { Name = name, BaseType = baseType, DataType = baseType };

    [Theory]
    [InlineData("int", ColumnProfileLevel.Full)]
    [InlineData("bit", ColumnProfileLevel.Full)]
    [InlineData("uniqueidentifier", ColumnProfileLevel.Distinct)]
    [InlineData("varbinary", ColumnProfileLevel.Distinct)]
    [InlineData("xml", ColumnProfileLevel.NullsOnly)]
    [InlineData("ntext", ColumnProfileLevel.NullsOnly)]
    public void SqlServer_profile_levels(string type, ColumnProfileLevel expected) =>
        Assert.Equal(expected, SqlServerDiagnostics.ProfileLevel(Col("c", type)));

    [Fact]
    public void SqlServer_profile_sql_handles_bit_dates_xml_and_quoting()
    {
        var sql = SqlServerDiagnostics.ProfileTable("dbo", "My]Table",
            [Col("Flag", "bit"), Col("When", "datetime2"), Col("Doc", "xml"), Col("Odd]Name", "int")]);

        Assert.Contains("FROM [dbo].[My]]Table]", sql);
        Assert.Contains("[Odd]]Name] AS c3", sql);
        Assert.Contains("MIN(CAST(c0 AS tinyint))", sql);
        Assert.Contains("CONVERT(nvarchar(400), MIN(c1), 121)", sql);
        Assert.Contains("SUM(CASE WHEN c2 IS NULL THEN 0 ELSE 1 END)", sql);
        Assert.DoesNotContain("DISTINCT c2", sql);
        Assert.Contains("TOP (@n)", sql);
    }

    [Fact]
    public void SqlServer_top_values_rejects_spatial_types() =>
        Assert.Throws<NotSupportedException>(() => SqlServerDiagnostics.TopValues("dbo", "T", Col("Shape", "geography")));

    [Theory]
    [InlineData(QueryStatOrder.TotalCpu, "ORDER BY qs.total_worker_time DESC")]
    [InlineData(QueryStatOrder.AverageDuration, "ORDER BY qs.total_elapsed_time / execution_count DESC")]
    public void SqlServer_top_queries_order(QueryStatOrder order, string expected) =>
        Assert.Contains(expected, SqlServerDiagnostics.TopQueries(order));

    [Fact]
    public void Postgres_top_queries_use_version_specific_columns()
    {
        Assert.Contains("s.total_exec_time", PostgresDiagnostics.TopQueries("public", 160002, QueryStatOrder.TotalDuration));
        var v12 = PostgresDiagnostics.TopQueries("ext schema", 120010, QueryStatOrder.AverageDuration);
        Assert.Contains("ORDER BY s.mean_time DESC", v12);
        Assert.Contains("FROM \"ext schema\".pg_stat_statements", v12);
    }

    [Fact]
    public void Postgres_profile_counts_distinct_by_text_and_skips_min_max_for_unordered_types()
    {
        var sql = PostgresDiagnostics.ProfileTable("public", "t", [Col("id", "int4"), Col("doc", "json")]);
        Assert.Contains("left(min(c0)::text, 400)", sql);
        Assert.Contains("count(DISTINCT c1::text)", sql);
        Assert.DoesNotContain("min(c1)", sql);
        Assert.Contains("LIMIT @n", sql);
    }
}
