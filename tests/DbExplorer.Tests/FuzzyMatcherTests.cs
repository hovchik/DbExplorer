using DbExplorer.Application.Search;

namespace DbExplorer.Tests;

public class FuzzyMatcherTests
{
    [Theory]
    [InlineData("dbo.CustomerOrders", "custord")]
    [InlineData("dbo.CustomerOrders", "dbo.co")]
    [InlineData("sales.order_lines", "ol")]
    [InlineData("Refresh metadata", "refmeta")]
    public void Subsequences_match(string candidate, string query) =>
        Assert.NotNull(FuzzyMatcher.Score(candidate, query));

    [Theory]
    [InlineData("dbo.Customers", "xyz")]
    [InlineData("abc", "abcd")]
    [InlineData("dbo.Orders", "sredro")]
    public void Non_subsequences_do_not_match(string candidate, string query) =>
        Assert.Null(FuzzyMatcher.Score(candidate, query));

    [Fact]
    public void Exact_and_word_start_matches_rank_higher()
    {
        int S(string c, string q) => FuzzyMatcher.Score(c, q)!.Value;

        Assert.True(S("Orders", "orders") > S("OrderDetailsArchive", "orders"));
        Assert.True(S("dbo.OrderLines", "ol") > S("dbo.Tools", "ol"));
        Assert.True(S("dbo.Customers", "cust") > S("dbo.AccountCustodians", "cust"));
        Assert.True(S("sales.orders", "orders") > S("sales.orders_order_id_seq", "orders"));
    }

    [Fact]
    public void Empty_query_matches_everything() => Assert.Equal(0, FuzzyMatcher.Score("anything", ""));
}
