using DbExplorer.Application.Connections;
using DbExplorer.Core.Connections;

namespace DbExplorer.Tests;

public class ConnectionFoldersTests
{
    private static ConnectionProfile P(string name, string folder = "") => new() { Name = name, Folder = folder };

    [Fact]
    public void Folder_names_are_trimmed_and_use_forward_slashes()
    {
        Assert.Equal("Clients/Acme", ConnectionFolders.Normalize(" Clients \\ Acme / "));
        Assert.Equal("", ConnectionFolders.Normalize(null));
    }

    [Fact]
    public void Loose_connections_come_first_then_folders_then_names()
    {
        var ordered = ConnectionFolders.Order([P("zeta", "Shop"), P("beta"), P("alpha", "shop"), P("local"), P("x", "Acme")]);
        Assert.Equal(["beta", "local", "x", "alpha", "zeta"], ordered.Select(p => p.Name));
    }

    [Fact]
    public void Folder_list_has_each_folder_once()
    {
        Assert.Equal(["Acme", "Shop"], ConnectionFolders.All([P("a", "Shop"), P("b", "shop "), P("c"), P("d", "Acme")]));
    }

    [Fact]
    public void Folder_shows_before_the_name()
    {
        var profile = new ConnectionProfile { Name = "prod-sql", Folder = "Shop", Environment = ConnectionEnvironment.Production };
        Assert.StartsWith("Shop / prod-sql  [", profile.ToString());
        Assert.Equal("prod-sql", profile.DisplayName);
    }
}
