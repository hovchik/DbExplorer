using DbExplorer.Core.Search;
using DbExplorer.Providers.Postgres;
using DbExplorer.Providers.SqlServer;

namespace DbExplorer.Tests;

public class LikePatternTests
{
    [Fact]
    public void SqlServer_escapes_wildcards_with_brackets()
    {
        Assert.Equal("%50[%][_][[]x]%", SqlServerSql.BuildPattern("50%_[x]", SearchMatchMode.Contains));
        Assert.Equal("abc%", SqlServerSql.BuildPattern("abc", SearchMatchMode.StartsWith));
        Assert.Equal("%abc", SqlServerSql.BuildPattern("abc", SearchMatchMode.EndsWith));
        Assert.Equal("abc", SqlServerSql.BuildPattern("abc", SearchMatchMode.Exact));
    }

    [Fact]
    public void Postgres_escapes_wildcards_with_backslash()
    {
        Assert.Equal("a\\\\b\\%c\\_%", PostgresSql.BuildPattern("a\\b%c_", SearchMatchMode.StartsWith));
    }

    [Fact]
    public void Identifiers_are_quoted_safely()
    {
        Assert.Equal("[we]]ird]", SqlServerSql.Quote("we]ird"));
        Assert.Equal("\"we\"\"ird\"", PostgresSql.Quote("we\"ird"));
    }
}
