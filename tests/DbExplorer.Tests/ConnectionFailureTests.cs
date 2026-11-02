using System.ComponentModel;
using System.Data.Common;
using System.Net.Sockets;
using System.Security.Authentication;
using DbExplorer.Application.Connections;
using DbExplorer.Application.Connections.Ssh;
using DbExplorer.Core.Connections;
using DbExplorer.Desktop.ViewModels;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;

namespace DbExplorer.Tests;

public class ConnectionFailureTests
{
    private static readonly ConnectionProfile Profile = new()
    {
        Name = "Orders", Host = "db.example.com", Port = 5432, Database = "orders", UserName = "app"
    };

    /// <summary>A driver error with an SQLSTATE, like Npgsql's and MySqlConnector's.</summary>
    private sealed class StateException(string message, string state) : DbException(message)
    {
        public override string SqlState => state;
    }

    private static ConnectionFailureKind Kind(Exception e) => ConnectionFailure.Classify(e, Profile).Kind;

    [Fact]
    public void Postgres_codes_sort_login_and_missing_database()
    {
        Assert.Equal(ConnectionFailureKind.Login,
            Kind(new PostgresException("password authentication failed for user \"app\"", "FATAL", "FATAL", "28P01")));
        Assert.Equal(ConnectionFailureKind.DatabaseMissing,
            Kind(new PostgresException("database \"orders\" does not exist", "FATAL", "FATAL", "3D000")));
        Assert.Equal(ConnectionFailureKind.NotAllowed,
            Kind(new PostgresException("no pg_hba.conf entry for host \"10.0.0.5\", user \"app\", database \"orders\", no encryption",
                "FATAL", "FATAL", "28000")));
        Assert.Equal(ConnectionFailureKind.Login,
            Kind(new PostgresException("role \"app\" does not exist", "FATAL", "FATAL", "28000")));
    }

    [Fact]
    public void Codes_win_over_texts_in_another_language()
    {
        // A German server: no English text to match, but the SQLSTATE still says what happened.
        Assert.Equal(ConnectionFailureKind.Login,
            Kind(new StateException("Passwort-Authentifizierung für Benutzer »app« fehlgeschlagen", "28P01")));
        Assert.Equal(ConnectionFailureKind.DatabaseMissing,
            Kind(new StateException("Datenbank »orders« existiert nicht", "3D000")));
    }

    [Theory]
    [InlineData("Access denied for user 'app'@'10.0.0.5' (using password: YES)", ConnectionFailureKind.Login)]
    [InlineData("Unknown database 'orders'", ConnectionFailureKind.DatabaseMissing)]
    [InlineData("Host '10.0.0.5' is not allowed to connect to this MySQL server", ConnectionFailureKind.NotAllowed)]
    [InlineData("Login failed for user 'app'.", ConnectionFailureKind.Login)]
    [InlineData("Cannot open database \"orders\" requested by the login. The login failed.", ConnectionFailureKind.DatabaseMissing)]
    [InlineData("Connection Timeout Expired. The timeout period elapsed while attempting to consume the pre-login handshake acknowledgement.", ConnectionFailureKind.Timeout)]
    [InlineData("A network-related or instance-specific error occurred while establishing a connection to SQL Server. The server was not found or was not accessible.", ConnectionFailureKind.Timeout)]
    [InlineData("Unable to connect to any of the specified MySQL hosts.", ConnectionFailureKind.Timeout)]
    [InlineData("Something unusual happened", ConnectionFailureKind.Other)]
    public void Driver_texts_without_codes(string message, ConnectionFailureKind expected) =>
        Assert.Equal(expected, Kind(new InvalidOperationException(message)));

    [Theory]
    [InlineData(SocketError.HostNotFound, ConnectionFailureKind.HostNotFound)]
    [InlineData(SocketError.TryAgain, ConnectionFailureKind.HostNotFound)]
    [InlineData(SocketError.ConnectionRefused, ConnectionFailureKind.Refused)]
    [InlineData(SocketError.TimedOut, ConnectionFailureKind.Timeout)]
    [InlineData(SocketError.HostUnreachable, ConnectionFailureKind.Timeout)]
    public void Socket_errors_inside_a_driver_error(SocketError code, ConnectionFailureKind expected) =>
        Assert.Equal(expected, Kind(new NpgsqlException("Failed to connect to 10.0.0.5:5432", new SocketException((int)code))));

    [Fact]
    public void Windows_socket_codes_from_SqlClient()
    {
        Assert.Equal(ConnectionFailureKind.HostNotFound, Kind(new InvalidOperationException("x", new Win32Exception(11001))));
        Assert.Equal(ConnectionFailureKind.Refused, Kind(new InvalidOperationException("x", new Win32Exception(10061))));
        Assert.Equal(ConnectionFailureKind.Timeout, Kind(new InvalidOperationException("x", new Win32Exception(10060))));
    }

    [Fact]
    public void Tls_failures_use_the_tls_hint_even_though_dotnet_says_authentication_failed()
    {
        var error = new NpgsqlException("Exception while connecting",
            new AuthenticationException("Authentication failed, see inner exception."));
        var failure = ConnectionFailure.Classify(error, Profile);
        Assert.Equal(ConnectionFailureKind.Tls, failure.Kind);
        Assert.Contains("Trust certificate", failure.Advice);
    }

    [Fact]
    public void Ssh_failures_keep_the_ssh_explanation()
    {
        var error = new SshTunnelException("Could not reach the SSH server bastion:22: No such host is known");
        var failure = ConnectionFailure.Classify(error, Profile);
        Assert.Equal(ConnectionFailureKind.Ssh, failure.Kind);
        Assert.Equal(error.Message, failure.Detail);
    }

    [Fact]
    public void Advice_names_the_host_port_user_and_database()
    {
        var refused = ConnectionFailure.Classify(new SocketException((int)SocketError.ConnectionRefused), Profile);
        Assert.Contains("db.example.com:5432", refused.Advice);
        var login = ConnectionFailure.Classify(new PostgresException("x", "FATAL", "FATAL", "28P01"), Profile);
        Assert.Contains("\"app\"", login.Advice);
        var missing = ConnectionFailure.Classify(new PostgresException("x", "FATAL", "FATAL", "3D000"), Profile);
        Assert.Contains("\"orders\"", missing.Advice);
        Assert.StartsWith("Database not found. ", missing.Summary);
        Assert.EndsWith("x)", missing.Summary);
    }

    [Fact]
    public void Windows_sign_in_gets_its_own_advice()
    {
        var profile = new ConnectionProfile { Host = "sql01", IntegratedSecurity = true };
        var failure = ConnectionFailure.Classify(new InvalidOperationException("Login failed for user 'CORP\\ann'."), profile);
        Assert.Contains("Windows account", failure.Advice);
    }

    [Fact]
    public void Npgsql_wrapper_text_is_followed_by_the_inner_message()
    {
        var error = new NpgsqlException("Exception while connecting", new SocketException((int)SocketError.ConnectionRefused));
        var failure = ConnectionFailure.Classify(error);
        Assert.StartsWith("Exception while connecting ", failure.Detail);
        Assert.True(failure.Detail.Length > "Exception while connecting ".Length);
    }

    // ----- Real drivers against a port where nothing listens -----

    private static int ClosedPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task Real_Npgsql_refused()
    {
        var port = ClosedPort();
        await using var c = new NpgsqlConnection($"Host=127.0.0.1;Port={port};Username=x;Password=y;Timeout=5");
        var error = await Assert.ThrowsAnyAsync<Exception>(() => c.OpenAsync());
        Assert.Equal(ConnectionFailureKind.Refused, ConnectionFailure.Classify(error).Kind);
    }

    [Fact]
    public async Task Real_MySqlConnector_refused()
    {
        var port = ClosedPort();
        await using var c = new MySqlConnection($"Server=127.0.0.1;Port={port};User ID=x;Password=y;Connection Timeout=5");
        var error = await Assert.ThrowsAnyAsync<Exception>(() => c.OpenAsync());
        Assert.Contains(ConnectionFailure.Classify(error).Kind, new[] { ConnectionFailureKind.Refused, ConnectionFailureKind.Timeout });
    }

    [Fact]
    public async Task Real_SqlClient_refused()
    {
        var port = ClosedPort();
        await using var c = new SqlConnection($"Server=tcp:127.0.0.1,{port};User ID=x;Password=y;Connect Timeout=5;Encrypt=false");
        var error = await Assert.ThrowsAnyAsync<Exception>(() => c.OpenAsync());
        Assert.Contains(ConnectionFailure.Classify(error).Kind, new[] { ConnectionFailureKind.Refused, ConnectionFailureKind.Timeout });
    }
}

public class StatusHistoryTests
{
    private static StatusEntry Entry(string text, bool error = false, int second = 0) =>
        new(new DateTime(2026, 11, 2, 14, 0, second), text, error);

    [Fact]
    public void Newest_first_and_capped()
    {
        var history = new StatusHistory(capacity: 3);
        for (var i = 0; i < 5; i++) history.Record(Entry($"m{i}"));
        Assert.Equal(new[] { "m4", "m3", "m2" }, history.Select(e => e.Text));
    }

    [Fact]
    public void Repeated_line_is_kept_once_with_the_newer_time()
    {
        var history = new StatusHistory();
        history.Record(Entry("Refresh failed: timeout", true, 1));
        history.Record(Entry("Refresh failed: timeout", true, 9));
        Assert.Single(history);
        Assert.Equal(9, history[0].Time.Second);

        history.Record(Entry("Refresh failed: timeout", false, 10));
        Assert.Equal(2, history.Count);
    }

    [Fact]
    public void Text_is_oldest_first_and_marks_errors()
    {
        var history = new StatusHistory();
        history.Record(Entry("Connected", second: 1));
        history.Record(Entry("Could not connect", true, 2));
        var lines = history.ToText().Split(Environment.NewLine);
        Assert.Equal("2026-11-02 14:00:01  Connected", lines[0]);
        Assert.Equal("2026-11-02 14:00:02  ERROR  Could not connect", lines[1]);
    }
}

/// <summary>Sign-in and missing-database failures from a real PostgreSQL; DBEXPLORER_TEST_PG = "host;port;user;password".</summary>
public class ConnectionFailurePostgresTests
{
    private static readonly string? Settings = Environment.GetEnvironmentVariable("DBEXPLORER_TEST_PG");

    private static async Task<ConnectionFailure> FailAsync(Func<string[], string> connectionString)
    {
        Skip.If(string.IsNullOrEmpty(Settings), "DBEXPLORER_TEST_PG is not set");
        var p = Settings!.Split(';');
        await using var c = new NpgsqlConnection(connectionString(p));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => c.OpenAsync());
        return ConnectionFailure.Classify(error);
    }

    [SkippableFact]
    public async Task Wrong_password_is_a_sign_in_failure()
    {
        var failure = await FailAsync(p => $"Host={p[0]};Port={p[1]};Username={p[2]};Password=wrong-{Guid.NewGuid():N}");
        Assert.Equal(ConnectionFailureKind.Login, failure.Kind);
    }

    [SkippableFact]
    public async Task Missing_database_is_reported_as_such()
    {
        var failure = await FailAsync(p => $"Host={p[0]};Port={p[1]};Username={p[2]};Password={p[3]};Database=no_such_db_{Guid.NewGuid():N}");
        Assert.Equal(ConnectionFailureKind.DatabaseMissing, failure.Kind);
    }
}
