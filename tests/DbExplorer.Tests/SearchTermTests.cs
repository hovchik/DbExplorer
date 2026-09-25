using DbExplorer.Core.Search;

namespace DbExplorer.Tests;

public class SearchTermTests
{
    [Fact]
    public void Numeric_text_is_parsed_with_invariant_culture()
    {
        var term = SearchTerm.Create(" 12.50 ", SearchMatchMode.Contains, includeNumeric: true, includeGuid: true);
        Assert.Equal(12.50m, term.Number);
        Assert.Null(term.Uuid);
    }

    [Fact]
    public void Guid_is_parsed_only_when_enabled()
    {
        var g = Guid.NewGuid();
        Assert.Equal(g, SearchTerm.Create(g.ToString(), SearchMatchMode.Exact, true, true).Uuid);
        Assert.Null(SearchTerm.Create(g.ToString(), SearchMatchMode.Exact, true, false).Uuid);
    }

    [Fact]
    public void Plain_text_has_no_number_or_guid()
    {
        var term = SearchTerm.Create("Yerevan", SearchMatchMode.Contains, true, true);
        Assert.Null(term.Number);
        Assert.Null(term.Uuid);
        Assert.True(term.HasText);
    }
}
