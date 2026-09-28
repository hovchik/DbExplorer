using System.Text.Json;
using DbExplorer.Core.Connections;

namespace DbExplorer.Tests;

public class ConnectionProfileTests
{
    [Fact]
    public void Environment_round_trips_as_a_readable_string()
    {
        var json = JsonSerializer.Serialize(new ConnectionProfile { Name = "p", Environment = ConnectionEnvironment.Production });
        Assert.Contains("\"Environment\":\"Production\"", json);
        Assert.DoesNotContain("IsProduction", json);
        Assert.DoesNotContain("DisplayName", json);
        Assert.Equal(ConnectionEnvironment.Production, JsonSerializer.Deserialize<ConnectionProfile>(json)!.Environment);
    }

    [Fact]
    public void Profiles_saved_before_environments_existed_load_as_none()
    {
        var profile = JsonSerializer.Deserialize<ConnectionProfile>("""{"Name":"old","Host":"h"}""")!;
        Assert.Equal(ConnectionEnvironment.None, profile.Environment);
        Assert.Equal("old", profile.ToString());
    }

    [Fact]
    public void Display_includes_environment_tag()
    {
        var p = new ConnectionProfile { Name = "Shop", Environment = ConnectionEnvironment.Staging };
        Assert.Equal("Shop  [STAGING]", p.ToString());
        Assert.Equal("Shop", p.DisplayName);
        Assert.False(p.IsProduction);
    }
}
