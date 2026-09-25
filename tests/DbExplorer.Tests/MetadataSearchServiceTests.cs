using DbExplorer.Application.Metadata;
using DbExplorer.Application.Search;
using DbExplorer.Core.Models;

namespace DbExplorer.Tests;

public class MetadataSearchServiceTests
{
    private static readonly MetadataSnapshot Snapshot = new()
    {
        Objects =
        [
            new DbObject { Schema = "dbo", Name = "Customers", Type = DbObjectType.Table },
            new DbObject { Schema = "dbo", Name = "usp_GetCustomer", Type = DbObjectType.Procedure },
            new DbObject { Schema = "sales", Name = "Orders", Type = DbObjectType.Table }
        ],
        Columns =
        [
            new DbColumn { Schema = "dbo", Table = "Customers", Name = "CustomerId", DataType = "int" },
            new DbColumn { Schema = "sales", Table = "Orders", Name = "CustomerId", DataType = "int" },
            new DbColumn { Schema = "sales", Table = "Orders", Name = "Total", DataType = "money" }
        ],
        Modules =
        [
            new DbModule
            {
                Schema = "dbo", Name = "usp_GetCustomer", Type = DbObjectType.Procedure,
                Definition = "CREATE PROCEDURE dbo.usp_GetCustomer @id int\nAS\nSELECT * FROM sales.Orders WHERE CustomerId = @id"
            }
        ],
        RefreshedAt = DateTimeOffset.Now
    };

    private readonly MetadataSearchService _service = new();

    [Fact]
    public void Finds_objects_columns_and_code_lines()
    {
        var results = _service.Search(Snapshot, new MetadataSearchQuery("customer", MetadataSearchScope.All));

        Assert.Contains(results, r => r.Kind == MetadataMatchKind.Object && r.ObjectName == "Customers");
        Assert.Equal(2, results.Count(r => r.Kind == MetadataMatchKind.Column));
        Assert.Contains(results, r => r.Kind == MetadataMatchKind.Definition && r.Line == 3);
    }

    [Fact]
    public void Whole_word_and_case_are_respected()
    {
        var results = _service.Search(Snapshot,
            new MetadataSearchQuery("Orders", MetadataSearchScope.ObjectNames, MatchCase: true, WholeWord: true));
        Assert.Single(results);

        var none = _service.Search(Snapshot,
            new MetadataSearchQuery("orders", MetadataSearchScope.ObjectNames, MatchCase: true));
        Assert.Empty(none);
    }

    [Fact]
    public void Invalid_regex_throws_argument_exception()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            _service.Search(Snapshot, new MetadataSearchQuery("([", MetadataSearchScope.All, UseRegex: true)));
    }
}
