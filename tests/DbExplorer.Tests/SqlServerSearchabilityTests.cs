using DbExplorer.Core.Connections;
using DbExplorer.Core.Models;
using DbExplorer.Core.Search;
using DbExplorer.Providers.SqlServer;

namespace DbExplorer.Tests;

public class SqlServerSearchabilityTests
{
    // The constructor only builds connection strings; it does not connect.
    private readonly SqlServerProvider _provider = new(new ConnectionProfile
    {
        ProviderKey = SqlServerProviderFactory.ProviderKey,
        Host = "localhost",
        Database = "test",
        UserName = "u",
        Password = "p"
    });

    private static DbColumn Col(string baseType) => new() { Name = "c", BaseType = baseType };

    [Fact]
    public void Text_columns_are_searchable_for_any_text()
    {
        var term = SearchTerm.Create("abc", SearchMatchMode.Contains, true, true);
        Assert.True(_provider.IsSearchable(Col("nvarchar"), term));
        Assert.False(_provider.IsSearchable(Col("int"), term));
        Assert.False(_provider.IsSearchable(Col("uniqueidentifier"), term));
        Assert.False(_provider.IsSearchable(Col("varbinary"), term));
    }

    [Fact]
    public void Numeric_columns_are_searchable_only_for_numbers()
    {
        var term = SearchTerm.Create("42", SearchMatchMode.Contains, true, true);
        Assert.True(_provider.IsSearchable(Col("int"), term));
        Assert.True(_provider.IsSearchable(Col("varchar"), term));

        var noNumeric = SearchTerm.Create("42", SearchMatchMode.Contains, false, true);
        Assert.False(_provider.IsSearchable(Col("int"), noNumeric));
    }
}
