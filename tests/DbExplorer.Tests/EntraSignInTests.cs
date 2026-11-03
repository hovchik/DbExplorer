using System.Text.Json;
using Azure.Core;
using DbExplorer.Application.Connections;
using DbExplorer.Application.Providers;
using DbExplorer.Core.Connections;
using DbExplorer.Desktop.ViewModels;
using DbExplorer.Providers.MySql;
using DbExplorer.Providers.Postgres;
using DbExplorer.Providers.SqlServer;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace DbExplorer.Tests;

public class EntraSignInTests
{
    private static ConnectionProfile SqlServer(EntraAuthMode mode, string? user = "ann@contoso.com", string? password = "secret") => new()
    {
        ProviderKey = SqlServerProviderFactory.ProviderKey, Host = "shop.database.windows.net", Database = "shop",
        EntraAuth = mode, UserName = user, Password = password, Encrypt = true, TrustServerCertificate = false
    };

    private static ProviderRegistry Registry() =>
        new([new SqlServerProviderFactory(), new PostgresProviderFactory(), new MySqlProviderFactory()]);

    // ---- SQL Server: the driver signs in ----

    [Theory]
    [InlineData(EntraAuthMode.Interactive, SqlAuthenticationMethod.ActiveDirectoryInteractive, true, false)]
    [InlineData(EntraAuthMode.Integrated, SqlAuthenticationMethod.ActiveDirectoryIntegrated, false, false)]
#pragma warning disable CS0618
    [InlineData(EntraAuthMode.Password, SqlAuthenticationMethod.ActiveDirectoryPassword, true, true)]
#pragma warning restore CS0618
    [InlineData(EntraAuthMode.Default, SqlAuthenticationMethod.ActiveDirectoryDefault, false, false)]
    [InlineData(EntraAuthMode.ServicePrincipal, SqlAuthenticationMethod.ActiveDirectoryServicePrincipal, true, true)]
    public void Sql_server_connection_string_names_the_entra_method(EntraAuthMode mode, SqlAuthenticationMethod expected, bool user, bool password)
    {
        var b = new SqlConnectionStringBuilder(SqlServerSql.BuildConnectionString(SqlServer(mode), "test"));
        Assert.Equal(expected, b.Authentication);
        Assert.False(b.IntegratedSecurity);
        Assert.Equal(user ? "ann@contoso.com" : "", b.UserID);
        Assert.Equal(password ? "secret" : "", b.Password);
    }

    [Fact]
    public void Sql_server_password_sign_in_is_unchanged()
    {
        var b = new SqlConnectionStringBuilder(SqlServerSql.BuildConnectionString(SqlServer(EntraAuthMode.None, "sa", "pw"), "test"));
        Assert.Equal(SqlAuthenticationMethod.NotSpecified, b.Authentication);
        Assert.Equal("sa", b.UserID);
        Assert.Equal("pw", b.Password);
    }

    [Fact]
    public void Entra_overrides_a_leftover_integrated_flag()
    {
        var p = SqlServer(EntraAuthMode.Interactive);
        p.IntegratedSecurity = true;
        var b = new SqlConnectionStringBuilder(SqlServerSql.BuildConnectionString(p, "test"));
        Assert.False(b.IntegratedSecurity); // SqlClient refuses Integrated Security together with Authentication
        Assert.Equal(SqlAuthenticationMethod.ActiveDirectoryInteractive, b.Authentication);
    }

    // ---- the profile ----

    [Fact]
    public void Profile_saves_the_entra_method_by_name_and_reads_old_files()
    {
        var json = JsonSerializer.Serialize(new ConnectionProfile { EntraAuth = EntraAuthMode.ServicePrincipal, EntraTenantId = "contoso.onmicrosoft.com" });
        Assert.Contains("\"EntraAuth\":\"ServicePrincipal\"", json);
        var back = JsonSerializer.Deserialize<ConnectionProfile>(json)!;
        Assert.Equal(EntraAuthMode.ServicePrincipal, back.EntraAuth);
        Assert.Equal("contoso.onmicrosoft.com", back.EntraTenantId);

        var old = JsonSerializer.Deserialize<ConnectionProfile>("{\"Host\":\"h\",\"IntegratedSecurity\":false}")!;
        Assert.Equal(EntraAuthMode.None, old.EntraAuth);
    }

    [Theory]
    [InlineData(EntraAuthMode.None, false, true)]
    [InlineData(EntraAuthMode.None, true, false)]
    [InlineData(EntraAuthMode.Interactive, false, false)]
    [InlineData(EntraAuthMode.Integrated, false, false)]
    [InlineData(EntraAuthMode.Default, false, false)]
    [InlineData(EntraAuthMode.Password, false, true)]
    [InlineData(EntraAuthMode.ServicePrincipal, false, true)]
    public void Only_password_methods_ask_for_a_password(EntraAuthMode mode, bool integrated, bool needs)
    {
        var p = new ConnectionProfile { EntraAuth = mode, IntegratedSecurity = integrated };
        Assert.Equal(needs, p.NeedsPassword);
        Assert.Equal(needs, p.MissingPassword);
        p.Password = "x";
        Assert.False(p.MissingPassword);
    }

    [Fact]
    public void Export_and_import_keep_the_sign_in_method()
    {
        var p = SqlServer(EntraAuthMode.Interactive, password: null);
        p.EntraTenantId = "contoso.com";
        var file = ConnectionTransfer.Read(ConnectionTransfer.Export([p], null));
        var back = Assert.Single(ConnectionTransfer.Profiles(file));
        Assert.Equal(EntraAuthMode.Interactive, back.EntraAuth);
        Assert.Equal("contoso.com", back.EntraTenantId);
        Assert.False(back.MissingPassword);
    }

    [Fact]
    public void Changing_the_sign_in_method_or_tenant_needs_a_reconnect()
    {
        var current = SqlServer(EntraAuthMode.None);
        var edited = current.Clone();
        edited.EntraAuth = EntraAuthMode.Interactive;
        Assert.True(ConnectionEdits.NeedsReconnect(current, edited));

        var a = SqlServer(EntraAuthMode.Interactive);
        var b = a.Clone();
        b.EntraTenantId = "fabrikam.com";
        Assert.True(ConnectionEdits.NeedsReconnect(a, b));
        Assert.False(ConnectionEdits.NeedsReconnect(a, a.Clone()));
    }

    // ---- which engine offers what ----

    [Fact]
    public void Each_engine_lists_its_sign_in_methods()
    {
        Assert.Contains(SignInMethod.EntraIntegrated, SignInMethods.For(SqlServerProviderFactory.ProviderKey, true));
        var pg = SignInMethods.For(PostgresProviderFactory.ProviderKey, true);
        Assert.Contains(SignInMethod.EntraInteractive, pg);
        Assert.DoesNotContain(SignInMethod.EntraIntegrated, pg);
        Assert.Equal([SignInMethod.Password], SignInMethods.For(MySqlProviderFactory.ProviderKey, false));
    }

    [Fact]
    public void Sign_in_method_round_trips_through_a_profile()
    {
        foreach (var m in Enum.GetValues<SignInMethod>())
        {
            var p = new ConnectionProfile();
            SignInMethods.Apply(p, m);
            Assert.Equal(m, SignInMethods.Of(p));
        }
    }

    [Theory]
    [InlineData("shop.database.windows.net", true)]
    [InlineData("tcp:shop.database.windows.net,1433", true)]
    [InlineData("pg-prod.postgres.database.azure.com", true)]
    [InlineData("SHOP.DATABASE.WINDOWS.NET.", true)]
    [InlineData("localhost", false)]
    [InlineData("windows.net.example.com", false)]
    public void Azure_hosts_are_recognised(string host, bool azure) => Assert.Equal(azure, SignInMethods.IsAzureHost(host));

    // ---- connection strings ----

    [Theory]
    [InlineData("Server=tcp:shop.database.windows.net,1433;Database=shop;Authentication=Active Directory Interactive;User ID=ann@contoso.com", EntraAuthMode.Interactive)]
    [InlineData("Server=shop.database.windows.net;Authentication=\"Active Directory Default\"", EntraAuthMode.Default)]
    [InlineData("Server=shop.database.windows.net;Authentication=ActiveDirectoryMSI", EntraAuthMode.Default)]
    [InlineData("Server=shop.database.windows.net;Authentication=Active Directory Service Principal;User ID=1234;Password=s", EntraAuthMode.ServicePrincipal)]
    [InlineData("jdbc:sqlserver://shop.database.windows.net:1433;database=shop;authentication=ActiveDirectoryIntegrated", EntraAuthMode.Integrated)]
    [InlineData("Server=shop.database.windows.net;Authentication=SqlPassword;User ID=sa", EntraAuthMode.None)]
    public void Pasted_authentication_picks_the_entra_method(string text, EntraAuthMode expected)
    {
        Assert.True(ConnectionStrings.TryParse(text, ConnectionStrings.Postgres, out var parsed, out var error), error);
        Assert.Equal(ConnectionStrings.SqlServer, parsed.ProviderKey);
        Assert.Equal(expected, parsed.EntraAuth);
        Assert.Empty(parsed.Ignored);
    }

    [Fact]
    public void Unknown_authentication_is_reported()
    {
        Assert.False(ConnectionStrings.TryParse("Server=x;Authentication=Carrier Pigeon", ConnectionStrings.SqlServer, out _, out var error));
        Assert.Contains("Carrier Pigeon", error);
    }

    [Theory]
    [InlineData(EntraAuthMode.Interactive)]
    [InlineData(EntraAuthMode.Default)]
    [InlineData(EntraAuthMode.ServicePrincipal)]
    public void Copied_sql_server_strings_keep_the_entra_method(EntraAuthMode mode)
    {
        var p = SqlServer(mode);
        var keyValue = ConnectionStrings.Format(p, ConnectionStringStyle.KeyValue, includePassword: false);
        var b = new SqlConnectionStringBuilder(keyValue);
        Assert.Equal(SqlServerSql.EntraMethod(mode), b.Authentication);
        Assert.Equal("", b.Password);
        Assert.True(ConnectionStrings.TryParse(keyValue, ConnectionStrings.Postgres, out var back, out _));
        Assert.Equal(mode, back.EntraAuth);

        var jdbc = ConnectionStrings.Format(p, ConnectionStringStyle.Url);
        Assert.Contains(";authentication=ActiveDirectory", jdbc);
        Assert.True(ConnectionStrings.TryParse(jdbc, ConnectionStrings.Postgres, out var fromJdbc, out _));
        Assert.Equal(mode, fromJdbc.EntraAuth);
    }

    // ---- errors ----

    [Fact]
    public void Entra_failures_are_explained_as_a_microsoft_sign_in()
    {
        var profile = SqlServer(EntraAuthMode.Interactive);
        var aad = ConnectionFailure.Classify(new Exception("AADSTS50076: Due to a configuration change made by your administrator, you must use multi-factor authentication."), profile);
        Assert.Equal(ConnectionFailureKind.Login, aad.Kind);
        Assert.Equal("Microsoft sign-in failed", aad.Title);

        var ours = ConnectionFailure.Classify(new EntraSignInException("No Microsoft sign-in found on this computer. Run `az login`."), profile);
        Assert.Equal(ConnectionFailureKind.Login, ours.Kind);
        Assert.Contains("az login", ours.Advice);

        var plain = ConnectionFailure.Classify(new Exception("Login failed for user 'sa'."), SqlServer(EntraAuthMode.None, "sa"));
        Assert.Equal("Sign-in failed", plain.Title);
    }

    // ---- the dialog ----

    [Fact]
    public void Dialog_shows_the_fields_each_method_uses()
    {
        var vm = new ConnectionDialogViewModel(Registry(), new ConnectionProfile { ProviderKey = SqlServerProviderFactory.ProviderKey }, isNew: true);
        Assert.Contains(vm.SignInOptions, o => o.Method == SignInMethod.EntraInteractive);
        vm.UserName = "ann@contoso.com";
        vm.Password = "left over";
        vm.SelectedSignIn = vm.SignInOptions.Single(o => o.Method == SignInMethod.EntraInteractive);
        Assert.True(vm.UsesUser);
        Assert.False(vm.UsesPassword);
        Assert.True(vm.UsesTenant);
        Assert.NotNull(vm.SignInHint);

        var p = vm.ToProfile();
        Assert.Equal(EntraAuthMode.Interactive, p.EntraAuth);
        Assert.Null(p.Password);
        Assert.False(p.IntegratedSecurity);

        vm.SelectedSignIn = vm.SignInOptions.Single(o => o.Method == SignInMethod.EntraServicePrincipal);
        Assert.Equal("Client ID", vm.UserLabel);
        Assert.Equal("Client secret", vm.PasswordLabel);
        Assert.Equal("left over", vm.ToProfile().Password);
    }

    [Fact]
    public void Dialog_keeps_the_method_when_the_engine_offers_it_and_falls_back_when_not()
    {
        var registry = Registry();
        var vm = new ConnectionDialogViewModel(registry, new ConnectionProfile { ProviderKey = SqlServerProviderFactory.ProviderKey }, isNew: true);
        vm.SelectedSignIn = vm.SignInOptions.Single(o => o.Method == SignInMethod.EntraInteractive);
        vm.SelectedProvider = registry.All.Single(p => p.Key == PostgresProviderFactory.ProviderKey);
        Assert.Equal(SignInMethod.EntraInteractive, vm.SignIn);
        vm.SelectedProvider = registry.All.Single(p => p.Key == MySqlProviderFactory.ProviderKey);
        Assert.Equal(SignInMethod.Password, vm.SignIn);
        Assert.Single(vm.SignInOptions);
    }

    [Fact]
    public void Dialog_requires_a_user_for_postgres_entra()
    {
        var vm = new ConnectionDialogViewModel(Registry(), new ConnectionProfile { ProviderKey = PostgresProviderFactory.ProviderKey, Host = "pg.postgres.database.azure.com" }, isNew: true);
        vm.SelectedSignIn = vm.SignInOptions.Single(o => o.Method == SignInMethod.EntraDefault);
        Assert.True(vm.UsesUser); // PostgreSQL needs the role name
        var closed = false;
        vm.CloseRequested += _ => closed = true;
        vm.SaveCommand.Execute(null);
        Assert.False(closed);
        Assert.Contains("Entra user", vm.Message);
        vm.UserName = "ann@contoso.com";
        vm.SaveCommand.Execute(null);
        Assert.True(closed);
    }

    [Fact]
    public void Dialog_offers_entra_for_azure_servers()
    {
        var vm = new ConnectionDialogViewModel(Registry(), new ConnectionProfile { ProviderKey = SqlServerProviderFactory.ProviderKey }, isNew: true);
        Assert.False(vm.ShowEntraOffer);
        vm.Host = "shop.database.windows.net";
        Assert.True(vm.ShowEntraOffer);
        vm.UseEntraSignInCommand.Execute(null);
        Assert.Equal(SignInMethod.EntraInteractive, vm.SignIn);
        Assert.False(vm.ShowEntraOffer);
    }

    [Fact]
    public void Dialog_fills_the_method_from_a_pasted_string()
    {
        var vm = new ConnectionDialogViewModel(Registry(), new ConnectionProfile { ProviderKey = PostgresProviderFactory.ProviderKey }, isNew: true);
        Assert.True(vm.Fill("Server=tcp:shop.database.windows.net,1433;Initial Catalog=shop;Authentication=Active Directory Default", clearBox: true));
        Assert.Equal(SqlServerProviderFactory.ProviderKey, vm.SelectedProvider.Key);
        Assert.Equal(SignInMethod.EntraDefault, vm.SignIn);
        Assert.Equal(EntraAuthMode.Default, vm.ToProfile().EntraAuth);
    }

    [Fact]
    public void Postgres_app_sign_in_takes_a_role_name_and_a_client_id()
    {
        var vm = new ConnectionDialogViewModel(Registry(), new ConnectionProfile { ProviderKey = PostgresProviderFactory.ProviderKey, Host = "pg.postgres.database.azure.com" }, isNew: true);
        vm.SelectedSignIn = vm.SignInOptions.Single(o => o.Method == SignInMethod.EntraServicePrincipal);
        Assert.True(vm.UsesClientId);
        vm.UserName = "reporting-app";
        vm.TenantId = "contoso.onmicrosoft.com";
        Assert.Equal("Enter the app's client ID.", vm.SignInProblem());
        vm.ClientId = "6f1c2b0e-0000-0000-0000-000000000000";
        Assert.Null(vm.SignInProblem());
        var p = vm.ToProfile();
        Assert.Equal("reporting-app", p.UserName);
        Assert.Equal("6f1c2b0e-0000-0000-0000-000000000000", p.EntraClientId);

        vm.SelectedSignIn = vm.SignInOptions.Single(o => o.Method == SignInMethod.EntraInteractive);
        Assert.Null(vm.ToProfile().EntraClientId);
    }

    [Fact]
    public void Existing_entra_connection_opens_with_its_method()
    {
        var vm = new ConnectionDialogViewModel(Registry(), SqlServer(EntraAuthMode.ServicePrincipal));
        Assert.Equal(SignInMethod.EntraServicePrincipal, vm.SignIn);
    }
}

/// <summary>Azure Database for PostgreSQL: an Entra token is the password. The Microsoft sign-in is replaced.</summary>
[CollectionDefinition(nameof(PostgresEntraTests), DisableParallelization = true)]
public class PostgresEntraCollection;

[Collection(nameof(PostgresEntraTests))]
public class PostgresEntraTests : IDisposable
{
    private int _calls;
    private DateTimeOffset _expires = DateTimeOffset.UtcNow.AddHours(1);

    public PostgresEntraTests()
    {
        PostgresEntra.TokenSourceOverride = (_, _) =>
        {
            _calls++;
            return ValueTask.FromResult(new AccessToken($"token-{_calls}", _expires));
        };
    }

    public void Dispose() => PostgresEntra.TokenSourceOverride = null;

    private static ConnectionProfile Profile(string? user) => new()
    {
        ProviderKey = PostgresProviderFactory.ProviderKey, Host = "pg.postgres.database.azure.com", Database = "shop",
        EntraAuth = EntraAuthMode.Interactive, UserName = user, Password = "ignored", Encrypt = true, TrustServerCertificate = false
    };

    [Fact]
    public void Token_is_the_password_and_is_cached()
    {
        var p = Profile($"ann-{Guid.NewGuid():N}@contoso.com");
        var b = new NpgsqlConnectionStringBuilder(PostgresSql.BuildConnectionString(p));
        Assert.Equal(p.UserName, b.Username);
        Assert.Equal("token-1", b.Password);
        Assert.Equal(SslMode.VerifyFull, b.SslMode);

        PostgresSql.BuildConnectionString(p);
        Assert.Equal(1, _calls);
    }

    [Fact]
    public void Token_is_renewed_before_it_expires()
    {
        _expires = DateTimeOffset.UtcNow.AddMinutes(3); // inside the five-minute margin
        var p = Profile($"bob-{Guid.NewGuid():N}@contoso.com");
        PostgresSql.BuildConnectionString(p);
        PostgresSql.BuildConnectionString(p);
        Assert.Equal(2, _calls);
    }

    [Fact]
    public void Data_source_signs_in_only_when_a_connection_opens()
    {
        var p = Profile($"cy-{Guid.NewGuid():N}@contoso.com");
        using var ds = PostgresSql.CreateDataSource(p);
        Assert.Equal(0, _calls);
        Assert.DoesNotContain("token", ds.ConnectionString);
    }

    [Fact]
    public void Missing_user_is_reported_before_signing_in()
    {
        var ex = Assert.Throws<EntraSignInException>(() => PostgresSql.BuildConnectionString(Profile(null)));
        Assert.Contains("Entra user", ex.Message);
        Assert.Equal(0, _calls);
    }

    [Fact]
    public void Password_sign_in_is_unchanged()
    {
        var p = Profile("app");
        p.EntraAuth = EntraAuthMode.None;
        var b = new NpgsqlConnectionStringBuilder(PostgresSql.BuildConnectionString(p));
        Assert.Equal("ignored", b.Password);
        Assert.Equal(0, _calls);
    }
}
