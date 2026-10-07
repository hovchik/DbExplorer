using DbExplorer.Application;
using DbExplorer.Application.Connections.Ssh;
using DbExplorer.Application.Lab;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.Providers;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Connections;
using DbExplorer.Providers.Postgres;

namespace DbExplorer.Tests;

/// <summary>
/// Tunnels through a real SSH server to a real PostgreSQL. Skipped unless DBEXPLORER_TEST_SSH is set to
/// "host;port;user;password;keyFile;keyPassphrase" of an SSH server that allows TCP forwarding, and DBEXPLORER_TEST_PG to
/// "host;port;user;password" of a PostgreSQL server as that SSH server reaches it.
/// </summary>
public sealed class SshTunnelIntegrationTests : IDisposable
{
    private static readonly string? SshSettings = Environment.GetEnvironmentVariable("DBEXPLORER_TEST_SSH");
    private static readonly string? PgSettings = Environment.GetEnvironmentVariable("DBEXPLORER_TEST_PG");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbx-sshit-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class CountingPrompt(bool answer) : ISshHostKeyPrompt
    {
        public List<SshHostKey> Asked { get; } = [];

        public Task<bool> TrustAsync(SshHostKey key, CancellationToken ct)
        {
            Asked.Add(key);
            return Task.FromResult(answer);
        }
    }

    private static ConnectionProfile Profile(SshAuthMethod auth = SshAuthMethod.Password)
    {
        var s = SshSettings!.Split(';');
        var p = PgSettings!.Split(';');
        return new ConnectionProfile
        {
            Name = "tunnelled", ProviderKey = PostgresProviderFactory.ProviderKey,
            Host = p[0], Port = int.Parse(p[1]), UserName = p[2], Password = p[3], Database = "postgres",
            Ssh = new SshTunnelSettings
            {
                Enabled = true, Host = s[0], Port = int.Parse(s[1]), UserName = s[2], AuthMethod = auth,
                Password = auth == SshAuthMethod.Password ? s[3] : null,
                PrivateKeyPath = s[4], Passphrase = auth == SshAuthMethod.PrivateKey ? s[5] : null
            }
        };
    }

    private SshTunnelService Tunnels(ISshHostKeyPrompt prompt) => new(new KnownHostsStore(new AppPaths(_root)), prompt);

    private SessionService Sessions(SshTunnelService tunnels)
    {
        var paths = new AppPaths(_root);
        var metadata = new MetadataService(new MetadataCache(paths), new SchemaHistoryStore(paths), new VirtualForeignKeyStore(paths));
        return new SessionService(new ProviderRegistry([new PostgresProviderFactory()]), metadata, tunnels);
    }

    [SkippableFact]
    public async Task Connects_through_the_tunnel_asks_once_about_the_host_key_and_closes_the_port_on_disconnect()
    {
        Skip.If(SshSettings is null || PgSettings is null, "DBEXPLORER_TEST_SSH and DBEXPLORER_TEST_PG are not set");
        var prompt = new CountingPrompt(answer: true);
        var sessions = Sessions(Tunnels(prompt));

        var session = await sessions.ConnectAsync(Profile());
        var port = session.Tunnel!.LocalPort;
        Assert.Contains("PostgreSQL", session.ServerVersion);
        Assert.Contains("postgres", await session.Factory.ListDatabasesAsync(session.Profile));
        var key = Assert.Single(prompt.Asked);
        Assert.StartsWith("SHA256:", key.Fingerprint);
        Assert.False(key.Changed);
        await session.DisposeAsync();
        await AssertClosedAsync(port);

        // The accepted key is remembered: the next connect does not ask.
        await using var again = await sessions.ConnectAsync(Profile());
        Assert.Single(prompt.Asked);
    }

    [SkippableFact]
    public async Task A_rejected_host_key_stops_the_connection()
    {
        Skip.If(SshSettings is null || PgSettings is null, "DBEXPLORER_TEST_SSH and DBEXPLORER_TEST_PG are not set");
        var ex = await Assert.ThrowsAsync<SshTunnelException>(() => Tunnels(new CountingPrompt(answer: false)).OpenAsync(Profile(), 5432));
        Assert.Contains("host key", ex.Message);
    }

    [SkippableFact]
    public async Task A_changed_host_key_is_reported_as_changed()
    {
        Skip.If(SshSettings is null || PgSettings is null, "DBEXPLORER_TEST_SSH and DBEXPLORER_TEST_PG are not set");
        var profile = Profile();
        new KnownHostsStore(new AppPaths(_root)).Trust(new SshHostKey(profile.Ssh.Host, profile.Ssh.Port, "ssh-ed25519", "SHA256:not-the-real-one", null));
        var prompt = new CountingPrompt(answer: false);

        var ex = await Assert.ThrowsAsync<SshTunnelException>(() => Tunnels(prompt).OpenAsync(profile, 5432));
        Assert.True(Assert.Single(prompt.Asked).Changed);
        Assert.Contains("has changed", ex.Message);
    }

    [SkippableFact]
    public async Task Signs_in_with_a_key_and_its_passphrase()
    {
        Skip.If(SshSettings is null || PgSettings is null, "DBEXPLORER_TEST_SSH and DBEXPLORER_TEST_PG are not set");
        await using var session = await Sessions(Tunnels(new CountingPrompt(true))).ConnectAsync(Profile(SshAuthMethod.PrivateKey));
        Assert.Contains("PostgreSQL", session.ServerVersion);
    }

    [SkippableFact]
    public async Task A_key_without_its_passphrase_asks_for_it()
    {
        Skip.If(SshSettings is null || PgSettings is null, "DBEXPLORER_TEST_SSH and DBEXPLORER_TEST_PG are not set");
        var profile = Profile(SshAuthMethod.PrivateKey);
        profile.Ssh.Passphrase = null;
        var ex = await Assert.ThrowsAsync<SshTunnelException>(() => Tunnels(new CountingPrompt(true)).OpenAsync(profile, 5432));
        Assert.Contains("passphrase", ex.Message);
    }

    [SkippableFact]
    public async Task A_wrong_ssh_password_is_an_ssh_error()
    {
        Skip.If(SshSettings is null || PgSettings is null, "DBEXPLORER_TEST_SSH and DBEXPLORER_TEST_PG are not set");
        var profile = Profile();
        profile.Ssh.Password = "wrong";
        var ex = await Assert.ThrowsAsync<SshTunnelException>(() => Tunnels(new CountingPrompt(true)).OpenAsync(profile, 5432));
        Assert.Contains("refused the password", ex.Message);
    }

    [SkippableFact]
    public async Task A_database_the_ssh_server_cannot_reach_is_explained()
    {
        Skip.If(SshSettings is null || PgSettings is null, "DBEXPLORER_TEST_SSH and DBEXPLORER_TEST_PG are not set");
        var profile = Profile();
        profile.Port = 1; // nothing listens there on the SSH server
        var ex = await Assert.ThrowsAsync<SshTunnelException>(() => Sessions(Tunnels(new CountingPrompt(true))).ConnectAsync(profile));
        Assert.Contains("could not reach the database", ex.Message);
        Assert.Contains(":1", ex.Message);
    }

    private static async Task AssertClosedAsync(int port)
    {
        using var client = new System.Net.Sockets.TcpClient();
        await Assert.ThrowsAnyAsync<System.Net.Sockets.SocketException>(() => client.ConnectAsync("127.0.0.1", port));
    }
}
