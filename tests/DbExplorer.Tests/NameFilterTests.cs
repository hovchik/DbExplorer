using DbExplorer.Application.Search;

namespace DbExplorer.Tests;

public class NameFilterTests
{
    private static readonly string[] Names = ["master", "Sales", "SalesArchive", "hr_sales", "Inventory"];

    [Fact]
    public void BlankTextKeepsEveryName()
    {
        Assert.Equal(Names, NameFilter.Apply(Names, ""));
        Assert.Equal(Names, NameFilter.Apply(Names, "   "));
        Assert.Equal(Names, NameFilter.Apply(Names, null));
    }

    [Fact]
    public void KeepsNamesContainingTheTextIgnoringCase()
    {
        Assert.Equal(["Sales", "SalesArchive", "hr_sales"], NameFilter.Apply(Names, "SALES"));
        Assert.Equal(["Inventory"], NameFilter.Apply(Names, " vent "));
    }

    [Fact]
    public void NoMatchGivesAnEmptyList() => Assert.Empty(NameFilter.Apply(Names, "zzz"));
}
