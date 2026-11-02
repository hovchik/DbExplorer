using System.Security.Authentication;
using DbExplorer.Application.Providers;
using DbExplorer.Core.Connections;
using DbExplorer.Desktop.ViewModels;
using DbExplorer.Providers.MySql;
using DbExplorer.Providers.Postgres;
using DbExplorer.Providers.SqlServer;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;

namespace DbExplorer.Tests;

public class ConnectionSecurityTests
{
    [Theory]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.1.2.3", true)]
    [InlineData("::1", true)]
    [InlineData("[::1]", true)]
    [InlineData(".", true)]
    [InlineData(@".\SQLEXPRESS", true)]
    [InlineData(@"(local)\SQL2022", true)]
    [InlineData(@"(localdb)\MSSQLLocalDB", true)]
    [InlineData(@"localhost\SQLEXPRESS", true)]
    [InlineData("tcp:localhost,1433", true)]
    [InlineData("app.localhost", true)]
    [InlineData("", true)]
    [InlineData("db.example.com", false)]
    [InlineData("10.0.0.5", false)]
    [InlineData("192.168.1.20", false)]
    [InlineData(@"sqlprod01\SALES", false)]
    [InlineData("tcp:myserver.database.windows.net,1433", false)]
    [InlineData("localhost.evil.com", false)]
    public void Recognizes_servers_on_this_computer(string host, bool local) =>
        Assert.Equal(local, ConnectionSecurity.IsLocalHost(host));

    [Fact]
    public void This_machines_own_name_is_local() =>
        Assert.True(ConnectionSecurity.IsLocalHost(System.Environment.MachineName));

    [Fact]
    public void New_connections_to_another_machine_encrypt_and_verify()
    {
        Assert.Equal((true, false), ConnectionSecurity.DefaultsFor("db.example.com"));
        Assert.Equal((false, true), ConnectionSecurity.DefaultsFor("localhost"));
    }

    [Fact]
    public void Saved_connections_keep_their_settings()
    {
        // Saved files always carry both values; the property defaults (what an old file means) are unchanged.
        var p = new ConnectionProfile();
        Assert.False(p.Encrypt);
        Assert.True(p.TrustServerCertificate);
    }

    [Fact]
    public void Remote_defaults_give_full_verification_on_every_engine()
    {
        var p = new ConnectionProfile { Host = "db.example.com", UserName = "u", Password = "p" };
        ConnectionSecurity.ApplyDefaults(p);

        Assert.Equal(SslMode.VerifyFull, new NpgsqlConnectionStringBuilder(PostgresSql.BuildConnectionString(p)).SslMode);

        var mysql = new MySqlConnectionStringBuilder(MySqlSql.BuildConnectionString(p));
        Assert.Equal(MySqlSslMode.VerifyFull, mysql.SslMode);
        Assert.False(mysql.AllowPublicKeyRetrieval);

        var mssql = new SqlConnectionStringBuilder(SqlServerSql.BuildConnectionString(p, "test"));
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, mssql.Encrypt);
        Assert.False(mssql.TrustServerCertificate);
    }

    [Fact]
    public void Local_defaults_keep_working_without_a_certificate()
    {
        var p = new ConnectionProfile { Host = "localhost", UserName = "u", Password = "p" };
        ConnectionSecurity.ApplyDefaults(p);

        Assert.Equal(SslMode.Prefer, new NpgsqlConnectionStringBuilder(PostgresSql.BuildConnectionString(p)).SslMode);
        var mysql = new MySqlConnectionStringBuilder(MySqlSql.BuildConnectionString(p));
        Assert.Equal(MySqlSslMode.Preferred, mysql.SslMode);
        Assert.True(mysql.AllowPublicKeyRetrieval);
        Assert.True(new SqlConnectionStringBuilder(SqlServerSql.BuildConnectionString(p, "test")).TrustServerCertificate);
    }

    [Theory]
    [InlineData("localhost", null, false, true)]
    [InlineData("db.example.com", null, false, false)] // a man in the middle could hand over its own RSA key
    [InlineData("db.example.com", null, true, false)]
    [InlineData("127.0.0.1", "localhost", false, true)] // SSH tunnel to a server on the SSH host
    [InlineData("127.0.0.1", "db.internal", false, false)] // SSH tunnel on to another machine
    public void MySql_fetches_the_RSA_key_only_for_a_local_unencrypted_server(string host, string? certificateHost, bool encrypt, bool allowed)
    {
        var p = new ConnectionProfile { Host = host, CertificateHostName = certificateHost, Encrypt = encrypt };
        Assert.Equal(allowed, new MySqlConnectionStringBuilder(MySqlSql.BuildConnectionString(p)).AllowPublicKeyRetrieval);
    }

    [Theory]
    [InlineData(false, true, TlsProtection.Opportunistic)]
    [InlineData(true, true, TlsProtection.EncryptedUnverified)]
    [InlineData(true, false, TlsProtection.Verified)]
    public void Protection_levels(bool encrypt, bool trust, TlsProtection expected) =>
        Assert.Equal(expected, ConnectionSecurity.Protection(encrypt, trust));

    [Theory]
    [InlineData(true, false, ConnectionEnvironment.Production, false)]
    [InlineData(true, true, ConnectionEnvironment.Production, true)]
    [InlineData(false, true, ConnectionEnvironment.Production, true)]
    [InlineData(false, true, ConnectionEnvironment.Development, false)]
    [InlineData(false, true, ConnectionEnvironment.None, false)]
    public void Production_connections_warn_when_the_certificate_is_not_checked(
        bool encrypt, bool trust, ConnectionEnvironment environment, bool warns) =>
        Assert.Equal(warns, ConnectionSecurity.Warning(encrypt, trust, environment) is not null);

    [Fact]
    public void Summary_says_unencrypted_remote_traffic_can_be_read()
    {
        Assert.Contains("could read", ConnectionSecurity.Describe(false, true, "db.example.com"));
        Assert.Contains("this computer", ConnectionSecurity.Describe(false, true, "localhost"));
        Assert.Contains("checked", ConnectionSecurity.Describe(true, false, "db.example.com"));
    }

    [Fact]
    public void Certificate_failures_point_at_Trust_certificate()
    {
        // What Npgsql reports for a self-signed certificate with VerifyFull
        var error = new InvalidOperationException("Exception while performing SSL handshake",
            new AuthenticationException("The remote certificate was rejected by the provided RemoteCertificateValidationCallback."));
        Assert.Contains("Trust certificate", ConnectionSecurity.ExplainFailure(error));

        var sqlServer = new Exception("A connection was successfully established with the server, but then an error occurred during the login process. (provider: SSL Provider, error: 0 - The certificate chain was issued by an authority that is not trusted.)");
        Assert.Contains("Trust certificate", ConnectionSecurity.Describe(sqlServer));
    }

    [Fact]
    public void Servers_without_TLS_point_at_Encrypt()
    {
        // MySqlConnector, then Npgsql (the text a PostgreSQL 16 server without ssl=on gives)
        Assert.Contains("untick Encrypt", ConnectionSecurity.ExplainFailure(new Exception("Server does not support SSL connections")));
        Assert.Contains("untick Encrypt", ConnectionSecurity.ExplainFailure(
            new Exception("SSL connection requested. No SSL enabled connection from this host is configured.")));
    }

    [Fact]
    public void Refused_RSA_key_points_at_Encrypt()
    {
        var error = new Exception("Authentication method 'caching_sha2_password' failed. Either use a secure connection, specify the server's RSA public key with ServerRSAPublicKeyFile, or set AllowPublicKeyRetrieval=True.");
        Assert.Contains("Tick Encrypt", ConnectionSecurity.ExplainFailure(error));
    }

    [Fact]
    public void Other_errors_are_left_alone()
    {
        var error = new Exception("password authentication failed for user \"app\"");
        Assert.Null(ConnectionSecurity.ExplainFailure(error));
        Assert.Equal(error.Message, ConnectionSecurity.Describe(error));
    }

    [Fact]
    public void New_connection_dialog_follows_the_host_until_a_box_is_ticked()
    {
        var registry = new ProviderRegistry([new SqlServerProviderFactory(), new PostgresProviderFactory(), new MySqlProviderFactory()]);
        var vm = new ConnectionDialogViewModel(registry, new ConnectionProfile(), isNew: true);
        Assert.False(vm.Encrypt); // "localhost"

        vm.Host = "db.example.com";
        Assert.True(vm.Encrypt);
        Assert.False(vm.TrustServerCertificate);

        vm.TrustServerCertificate = true; // the user's choice sticks
        vm.Host = "db2.example.com";
        Assert.True(vm.TrustServerCertificate);

        vm.Environment = ConnectionEnvironment.Production;
        Assert.True(vm.HasTlsWarning);
    }

    [Fact]
    public void Editing_a_saved_connection_never_changes_its_TLS_settings()
    {
        var registry = new ProviderRegistry([new SqlServerProviderFactory(), new PostgresProviderFactory(), new MySqlProviderFactory()]);
        var saved = new ConnectionProfile { Host = "localhost", Encrypt = false, TrustServerCertificate = true };
        var vm = new ConnectionDialogViewModel(registry, saved);

        vm.Host = "db.example.com";
        var profile = vm.ToProfile();
        Assert.False(profile.Encrypt);
        Assert.True(profile.TrustServerCertificate);
    }
}
