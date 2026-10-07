using DbExplorer.Application;
using DbExplorer.Application.Connections;
using DbExplorer.Core.Connections;
using DbExplorer.Providers.Postgres;
using Npgsql;

namespace DbExplorer.Tests;

public class ConnectionStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbx-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Profiles_round_trip_without_the_password()
    {
        var paths = new AppPaths(_root);
        var store = new ConnectionStore(paths, new NoSecretProtector());
        await store.SaveAsync([new ConnectionProfile { Name = "p", Host = "h", Password = "secret", SavePassword = true }]);

        Assert.DoesNotContain("secret", await File.ReadAllTextAsync(paths.ConnectionsFile));
        var loaded = Assert.Single(await store.LoadAsync());
        Assert.Equal("p", loaded.Name);
        Assert.Null(loaded.Password);
    }

    [Fact]
    public async Task Saves_at_the_same_time_leave_a_valid_file()
    {
        var paths = new AppPaths(_root);
        var store = new ConnectionStore(paths, new NoSecretProtector());

        await Task.WhenAll(Enumerable.Range(1, 20).Select(n => Task.Run(() =>
            store.SaveAsync(Enumerable.Range(0, n).Select(i => new ConnectionProfile { Name = $"p{i}", Host = "h" }).ToList()))));

        Assert.InRange((await store.LoadAsync()).Count, 1, 20);
        Assert.Equal([paths.ConnectionsFile], Directory.GetFiles(_root));
    }

    [Fact]
    public async Task A_damaged_file_is_kept_aside_instead_of_being_overwritten()
    {
        var paths = new AppPaths(_root);
        await File.WriteAllTextAsync(paths.ConnectionsFile, """[{"Profile": {"Name": "x" """);
        var store = new ConnectionStore(paths, new NoSecretProtector());

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
        Assert.False(File.Exists(paths.ConnectionsFile));
        var backup = Assert.Single(Directory.GetFiles(_root, "connections.json.corrupt-*"));
        Assert.Contains(backup, ex.Message);
        Assert.Empty(await store.LoadAsync());
    }

    [Theory]
    [InlineData(false, true, SslMode.Prefer)]
    [InlineData(false, false, SslMode.Prefer)]
    [InlineData(true, true, SslMode.Require)]
    [InlineData(true, false, SslMode.VerifyFull)]
    public void Postgres_validates_the_certificate_unless_told_to_trust_it(bool encrypt, bool trust, SslMode expected)
    {
        var cs = PostgresSql.BuildConnectionString(new ConnectionProfile { Host = "h", Encrypt = encrypt, TrustServerCertificate = trust });
        Assert.Equal(expected, new NpgsqlConnectionStringBuilder(cs).SslMode);
    }
}
