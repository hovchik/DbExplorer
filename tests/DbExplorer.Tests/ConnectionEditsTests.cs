using DbExplorer.Application.Connections;
using DbExplorer.Core.Connections;

namespace DbExplorer.Tests;

public class ConnectionEditsTests
{
    private static ConnectionProfile Shop() => new()
    {
        Name = "Shop", Folder = "Clients", ProviderKey = "postgres", Host = "db.example.com", Port = 5432, Database = "shop",
        UserName = "app", Password = "secret", SavePassword = true, Environment = ConnectionEnvironment.Development,
        Ssh = new SshTunnelSettings { Enabled = true, Host = "bastion", UserName = "deploy", Password = "ssh" }
    };

    [Fact]
    public void Duplicate_gets_a_new_id_a_free_name_and_keeps_everything_else()
    {
        var source = Shop();
        var copy = ConnectionEdits.Duplicate(source, [source]);

        Assert.NotEqual(source.Id, copy.Id);
        Assert.Equal("Shop (copy)", copy.Name);
        Assert.Equal(source.Host, copy.Host);
        Assert.Equal(source.Folder, copy.Folder);
        Assert.Equal("secret", copy.Password);
        Assert.Equal("ssh", copy.Ssh.Password);
        Assert.NotSame(source.Ssh, copy.Ssh);
        Assert.Equal("Shop", source.Name);
    }

    [Fact]
    public void Duplicate_counts_up_when_copies_exist()
    {
        var source = Shop();
        var existing = new[] { source, new ConnectionProfile { Name = "shop (COPY)" }, new ConnectionProfile { Name = "Shop (copy 2)" } };

        Assert.Equal("Shop (copy 3)", ConnectionEdits.Duplicate(source, existing).Name);
    }

    [Fact]
    public void Duplicate_of_an_unnamed_connection_uses_its_display_name()
    {
        var source = new ConnectionProfile { Host = "localhost", Database = "app" };
        Assert.Equal("localhost/app (copy)", ConnectionEdits.Duplicate(source, [source]).Name);
    }

    [Fact]
    public void Labels_environment_and_password_do_not_need_a_reconnect()
    {
        var current = Shop();
        var edited = current.Clone();
        edited.Name = "Shop live";
        edited.Folder = "Other";
        edited.Environment = ConnectionEnvironment.Production;
        edited.Password = "new";
        edited.SavePassword = false;
        edited.Host = " DB.example.com ";

        Assert.False(ConnectionEdits.NeedsReconnect(current, edited));
    }

    public static TheoryData<string> ServerSideChanges() =>
    [
        "provider", "host", "port", "database", "integrated", "user", "encrypt", "trust", "intent", "readonly",
        "ssh-on", "ssh-host", "ssh-port", "ssh-user", "ssh-auth", "ssh-key"
    ];

    [Theory]
    [MemberData(nameof(ServerSideChanges))]
    public void Server_sign_in_tls_ssh_and_read_only_changes_need_a_reconnect(string change)
    {
        var current = Shop();
        var edited = current.Clone();
        switch (change)
        {
            case "provider": edited.ProviderKey = "sqlserver"; break;
            case "host": edited.Host = "db2.example.com"; break;
            case "port": edited.Port = 5433; break;
            case "database": edited.Database = "Shop"; break;
            case "integrated": edited.IntegratedSecurity = true; break;
            case "user": edited.UserName = "admin"; break;
            case "encrypt": edited.Encrypt = !edited.Encrypt; break;
            case "trust": edited.TrustServerCertificate = !edited.TrustServerCertificate; break;
            case "intent": edited.ReadOnlyIntent = !edited.ReadOnlyIntent; break;
            case "readonly": edited.ReadOnly = true; break;
            case "ssh-on": edited.Ssh.Enabled = false; break;
            case "ssh-host": edited.Ssh.Host = "bastion2"; break;
            case "ssh-port": edited.Ssh.Port = 2222; break;
            case "ssh-user": edited.Ssh.UserName = "root"; break;
            case "ssh-auth": edited.Ssh.AuthMethod = SshAuthMethod.PrivateKey; break;
            case "ssh-key": edited.Ssh.PrivateKeyPath = "/keys/id"; break;
        }

        Assert.True(ConnectionEdits.NeedsReconnect(current, edited));
    }

    [Fact]
    public void Ssh_details_are_ignored_while_the_tunnel_stays_off()
    {
        var current = Shop();
        current.Ssh.Enabled = false;
        var edited = current.Clone();
        edited.Ssh.Host = "elsewhere";

        Assert.False(ConnectionEdits.NeedsReconnect(current, edited));
    }

    [Fact]
    public void Open_session_takes_name_folder_environment_and_read_only_at_once()
    {
        var open = Shop();
        var edited = open.Clone();
        edited.Name = "Shop live";
        edited.Folder = "";
        edited.Environment = ConnectionEnvironment.Production;
        edited.ReadOnly = true;
        edited.Host = "other";

        Assert.True(ConnectionEdits.ApplyToOpenSession(open, edited));
        Assert.Equal("Shop live", open.Name);
        Assert.Equal("", open.Folder);
        Assert.True(open.IsProduction);
        Assert.True(open.ReadOnly);
        Assert.Equal("db.example.com", open.Host); // the server stays the one the session is connected to
        Assert.False(ConnectionEdits.ApplyToOpenSession(open, edited));
    }
}
