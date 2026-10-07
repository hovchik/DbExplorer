using System.Text;
using DbExplorer.Application;
using DbExplorer.Application.Connections;
using DbExplorer.Application.Connections.Ssh;
using DbExplorer.Core.Connections;

namespace DbExplorer.Tests;

public class SshTunnelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbx-ssh-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static ConnectionProfile Tunnelled(string name = "via ssh") => new()
    {
        Name = name, ProviderKey = "postgres", Host = "localhost", Port = 5432, Database = "app", UserName = "admin",
        Password = "db pass", SavePassword = true,
        Ssh = new SshTunnelSettings
        {
            Enabled = true, Host = "bastion.example.com", Port = 2200, UserName = "deploy",
            AuthMethod = SshAuthMethod.PrivateKey, PrivateKeyPath = @"C:\keys\id_ed25519",
            Password = "ssh pass", Passphrase = "key phrase"
        }
    };

    /// <summary>Reversible stand-in for DPAPI, so saved secrets can be checked.</summary>
    private sealed class Base64Protector : ISecretProtector
    {
        public bool IsSupported => true;
        public string Protect(string plainText) => "p:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plainText));
        public string? Unprotect(string protectedText) => Encoding.UTF8.GetString(Convert.FromBase64String(protectedText[2..]));
    }

    [Fact]
    public void Clone_copies_the_ssh_settings_instead_of_sharing_them()
    {
        var original = Tunnelled();
        var copy = original.Clone();
        copy.Ssh.Host = "other";
        copy.Ssh.Enabled = false;

        Assert.Equal("bastion.example.com", original.Ssh.Host);
        Assert.True(original.Ssh.Enabled);
    }

    [Fact]
    public async Task Store_keeps_ssh_settings_and_encrypts_its_secrets()
    {
        var paths = new AppPaths(_root);
        var store = new ConnectionStore(paths, new Base64Protector());
        await store.SaveAsync([Tunnelled()]);

        var json = await File.ReadAllTextAsync(paths.ConnectionsFile);
        Assert.DoesNotContain("ssh pass", json);
        Assert.DoesNotContain("key phrase", json);

        var loaded = Assert.Single(await store.LoadAsync());
        Assert.True(loaded.Ssh.Enabled);
        Assert.Equal("bastion.example.com", loaded.Ssh.Host);
        Assert.Equal(2200, loaded.Ssh.Port);
        Assert.Equal("deploy", loaded.Ssh.UserName);
        Assert.Equal(SshAuthMethod.PrivateKey, loaded.Ssh.AuthMethod);
        Assert.Equal(@"C:\keys\id_ed25519", loaded.Ssh.PrivateKeyPath);
        Assert.Equal("ssh pass", loaded.Ssh.Password);
        Assert.Equal("key phrase", loaded.Ssh.Passphrase);
        Assert.Equal("db pass", loaded.Password);
    }

    [Fact]
    public async Task Store_does_not_keep_ssh_secrets_when_passwords_are_not_saved()
    {
        var paths = new AppPaths(_root);
        var store = new ConnectionStore(paths, new Base64Protector());
        var profile = Tunnelled();
        profile.SavePassword = false;
        await store.SaveAsync([profile]);

        var loaded = Assert.Single(await store.LoadAsync());
        Assert.True(loaded.Ssh.Enabled);
        Assert.Null(loaded.Ssh.Password);
        Assert.Null(loaded.Ssh.Passphrase);
    }

    [Fact]
    public async Task Connections_saved_before_ssh_load_without_a_tunnel()
    {
        var paths = new AppPaths(_root);
        await File.WriteAllTextAsync(paths.ConnectionsFile, """[{"Profile": {"Name": "old", "Host": "h", "ProviderKey": "postgres"}, "ProtectedPassword": null}]""");

        var loaded = Assert.Single(await new ConnectionStore(paths, new Base64Protector()).LoadAsync());
        Assert.False(loaded.Ssh.Enabled);
        Assert.False(loaded.Ssh.NeedsPassword);
    }

    [Fact]
    public void Export_without_passwords_keeps_the_tunnel_but_not_its_secrets()
    {
        var json = ConnectionTransfer.Export([Tunnelled()], exportPassword: null);

        Assert.DoesNotContain("ssh pass", json);
        Assert.DoesNotContain("key phrase", json);
        var loaded = Assert.Single(ConnectionTransfer.Profiles(ConnectionTransfer.Read(json)));
        Assert.True(loaded.Ssh.Enabled);
        Assert.Equal("bastion.example.com", loaded.Ssh.Host);
        Assert.Equal(2200, loaded.Ssh.Port);
        Assert.Equal(SshAuthMethod.PrivateKey, loaded.Ssh.AuthMethod);
        Assert.Equal(@"C:\keys\id_ed25519", loaded.Ssh.PrivateKeyPath);
        Assert.Null(loaded.Ssh.Password);
        Assert.Null(loaded.Ssh.Passphrase);
    }

    [Fact]
    public void Export_with_a_password_encrypts_ssh_secrets_and_restores_them()
    {
        var json = ConnectionTransfer.Export([Tunnelled()], "export pass");

        Assert.DoesNotContain("ssh pass", json);
        Assert.DoesNotContain("key phrase", json);
        var file = ConnectionTransfer.Read(json);
        var loaded = Assert.Single(ConnectionTransfer.Profiles(file, "export pass"));
        Assert.Equal("ssh pass", loaded.Ssh.Password);
        Assert.Equal("key phrase", loaded.Ssh.Passphrase);
        Assert.Equal("db pass", loaded.Password);
        Assert.True(loaded.SavePassword);

        // Without the export password nothing secret comes back.
        var bare = Assert.Single(ConnectionTransfer.Profiles(file));
        Assert.Null(bare.Ssh.Password);
        Assert.Null(bare.Ssh.Passphrase);
    }

    [Fact]
    public void Ssh_secrets_of_a_connection_without_a_tunnel_are_not_exported()
    {
        var profile = Tunnelled();
        profile.Ssh.Enabled = false;
        var json = ConnectionTransfer.Export([profile], "export pass");

        var loaded = Assert.Single(ConnectionTransfer.Profiles(ConnectionTransfer.Read(json), "export pass"));
        Assert.Null(loaded.Ssh.Password);
        Assert.Null(loaded.Ssh.Passphrase);
    }

    [Fact]
    public void Files_from_before_ssh_still_import()
    {
        const string v1 = """
            {"format":"DbExplorer.Connections","version":1,"exportedAt":"2026-10-07T10:00:00+00:00",
             "connections":[{"profile":{"name":"old","host":"h","providerKey":"postgres"},"password":null}]}
            """;
        var loaded = Assert.Single(ConnectionTransfer.Profiles(ConnectionTransfer.Read(v1)));
        Assert.Equal("old", loaded.Name);
        Assert.False(loaded.Ssh.Enabled);
    }

    [Theory]
    [InlineData("localhost", 5432, 1433, "localhost", 5432)]
    [InlineData("db.internal", null, 1433, "db.internal", 1433)]
    [InlineData(@"sql01\SALES", 1500, 1433, "sql01", 1500)]
    [InlineData("tcp:sql01,1501", null, 1433, "sql01", 1501)]
    [InlineData(".", null, 1433, "localhost", 1433)]
    [InlineData("(local)", 1433, 1433, "localhost", 1433)]
    public void Destination_is_the_database_as_the_ssh_server_sees_it(string host, int? port, int defaultPort, string expectedHost, int expectedPort)
    {
        var (h, p) = SshTunnelService.Destination(new ConnectionProfile { Host = host, Port = port }, defaultPort);
        Assert.Equal(expectedHost, h);
        Assert.Equal(expectedPort, p);
    }

    [Fact]
    public void A_named_instance_without_a_port_cannot_be_tunnelled()
    {
        var ex = Assert.Throws<SshTunnelException>(() => SshTunnelService.Destination(new ConnectionProfile { Host = @"sql01\SALES" }, 1433));
        Assert.Contains("port", ex.Message);
    }

    [Fact]
    public void Known_hosts_remember_accepted_keys_per_server_and_port()
    {
        var store = new KnownHostsStore(new AppPaths(_root));
        Assert.Null(store.Find("bastion", 22));

        store.Trust(new SshHostKey("Bastion", 22, "ssh-ed25519", "SHA256:abc", null));

        var reopened = new KnownHostsStore(new AppPaths(_root));
        Assert.Equal("SHA256:abc", reopened.Find("bastion", 22));
        Assert.Null(reopened.Find("bastion", 2222));
        Assert.True(new SshHostKey("bastion", 22, "ssh-ed25519", "SHA256:new", "SHA256:abc").Changed);
        Assert.False(new SshHostKey("bastion", 22, "ssh-ed25519", "SHA256:new", null).Changed);
    }

    [Theory]
    [InlineData("", "deploy", "Enter the SSH server")]
    [InlineData("bastion", "", "Enter the SSH user")]
    public async Task Missing_settings_are_reported_before_connecting(string host, string user, string expected)
    {
        var tunnels = new SshTunnelService(new KnownHostsStore(new AppPaths(_root)), new RejectUnknownHostKeys());
        var profile = Tunnelled();
        profile.Ssh.Host = host;
        profile.Ssh.UserName = user;

        var ex = await Assert.ThrowsAsync<SshTunnelException>(() => tunnels.OpenAsync(profile, 5432));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public async Task A_missing_key_file_is_named()
    {
        var tunnels = new SshTunnelService(new KnownHostsStore(new AppPaths(_root)), new RejectUnknownHostKeys());
        var profile = Tunnelled();
        profile.Ssh.Host = "127.0.0.1";
        profile.Ssh.PrivateKeyPath = Path.Combine(_root, "nope_ed25519");

        var ex = await Assert.ThrowsAsync<SshTunnelException>(() => tunnels.OpenAsync(profile, 5432));
        Assert.Contains("nope_ed25519", ex.Message);
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task An_unreachable_ssh_server_says_so()
    {
        var tunnels = new SshTunnelService(new KnownHostsStore(new AppPaths(_root)), new RejectUnknownHostKeys());
        var profile = Tunnelled();
        profile.Ssh.Host = "127.0.0.1";
        profile.Ssh.Port = 1; // nothing listens there
        profile.Ssh.AuthMethod = SshAuthMethod.Password;

        var ex = await Assert.ThrowsAsync<SshTunnelException>(() => tunnels.OpenAsync(profile, 5432));
        Assert.Contains("Could not reach the SSH server 127.0.0.1:1", ex.Message);
    }

    [Fact]
    public void Tunnelled_databases_on_localhost_get_their_own_metadata_cache_per_ssh_server()
    {
        var direct = new ConnectionProfile { ProviderKey = "postgres", Host = "localhost", Port = 5432, Database = "app" };
        var viaA = Tunnelled();
        viaA.Database = "app";
        var viaB = Tunnelled();
        viaB.Database = "app";
        viaB.Ssh.Host = "other-bastion";

        var keys = new[] { direct, viaA, viaB }.Select(DbExplorer.Application.Metadata.MetadataCache.CacheKey).ToList();
        Assert.Equal(3, keys.Distinct().Count());

        var off = viaA.Clone();
        off.Ssh.Enabled = false;
        Assert.Equal(keys[0], DbExplorer.Application.Metadata.MetadataCache.CacheKey(off));
    }
}
