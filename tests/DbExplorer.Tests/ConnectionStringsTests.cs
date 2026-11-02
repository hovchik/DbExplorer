using DbExplorer.Application.Connections;
using DbExplorer.Application.Providers;
using DbExplorer.Core.Abstractions;
using DbExplorer.Core.Connections;
using DbExplorer.Desktop.ViewModels;
using DbExplorer.Providers.MySql;
using DbExplorer.Providers.Postgres;
using DbExplorer.Providers.SqlServer;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;

namespace DbExplorer.Tests;

public class ConnectionStringsTests
{
    private static ParsedConnection Parse(string text, string fallback = ConnectionStrings.Postgres)
    {
        Assert.True(ConnectionStrings.TryParse(text, fallback, out var parsed, out var error), error);
        return parsed;
    }

    private static string Fail(string text)
    {
        Assert.False(ConnectionStrings.TryParse(text, ConnectionStrings.Postgres, out _, out var error));
        Assert.NotEmpty(error);
        return error;
    }

    [Fact]
    public void Provider_keys_match_the_factories()
    {
        Assert.Equal(PostgresProviderFactory.ProviderKey, ConnectionStrings.Postgres);
        Assert.Equal(SqlServerProviderFactory.ProviderKey, ConnectionStrings.SqlServer);
        Assert.Equal(MySqlProviderFactory.ProviderKey, ConnectionStrings.MySql);
    }

    // ---- URLs ----

    [Fact]
    public void Postgres_url_with_everything()
    {
        var p = Parse("postgresql://app%40corp:p%40ss%3Aword@db.example.com:6543/shop%20db?sslmode=verify-full&application_name=x",
            ConnectionStrings.MySql);
        Assert.Equal(ConnectionStrings.Postgres, p.ProviderKey);
        Assert.True(p.EngineDetected);
        Assert.Equal("db.example.com", p.Host);
        Assert.Equal(6543, p.Port);
        Assert.Equal("shop db", p.Database);
        Assert.Equal("app@corp", p.UserName);
        Assert.Equal("p@ss:word", p.Password);
        Assert.True(p.Encrypt);
        Assert.False(p.TrustServerCertificate);
        Assert.Equal(["application_name"], p.Ignored);
    }

    [Theory]
    [InlineData("postgres://u@h/db", "Postgres")]
    [InlineData("postgresql+psycopg2://u@h/db", "Postgres")]
    [InlineData("jdbc:postgresql://h:5432/db", "Postgres")]
    [InlineData("mysql://u@h/db", "MySql")]
    [InlineData("mariadb://u@h/db", "MySql")]
    [InlineData("jdbc:mysql://h:3306/db", "MySql")]
    [InlineData("mysql+pymysql://u@h/db", "MySql")]
    [InlineData("sqlserver://u@h?database=db", "SqlServer")]
    [InlineData("mssql+pyodbc://u@h", "SqlServer")]
    public void Url_scheme_picks_the_engine(string url, string engine) => Assert.Equal(engine, Parse(url).ProviderKey);

    [Theory]
    [InlineData("disable", false, null)]
    [InlineData("prefer", false, null)]
    [InlineData("require", true, true)]
    [InlineData("verify-ca", true, false)]
    [InlineData("verify-full", true, false)]
    public void Postgres_sslmode_is_respected(string mode, bool encrypt, bool? trust)
    {
        var p = Parse($"postgres://u@h/db?sslmode={mode}");
        Assert.Equal(encrypt, p.Encrypt);
        Assert.Equal(trust, p.TrustServerCertificate);
    }

    [Theory]
    [InlineData("ssl-mode=DISABLED", false, null)]
    [InlineData("ssl-mode=REQUIRED", true, true)]
    [InlineData("ssl-mode=VERIFY_CA", true, false)]
    [InlineData("ssl-mode=VERIFY_IDENTITY", true, false)]
    [InlineData("sslMode=REQUIRED", true, true)]
    [InlineData("useSSL=false", false, null)]
    [InlineData("useSSL=true", true, true)]
    [InlineData("useSSL=true&verifyServerCertificate=true", true, false)]
    public void Mysql_url_tls_options(string query, bool encrypt, bool? trust)
    {
        var p = Parse($"jdbc:mysql://h/db?{query}");
        Assert.Equal(encrypt, p.Encrypt);
        Assert.Equal(trust, p.TrustServerCertificate);
    }

    [Fact]
    public void Url_without_tls_options_leaves_tls_to_the_dialog()
    {
        var p = Parse("postgres://h/db");
        Assert.Null(p.Encrypt);
        Assert.Null(p.TrustServerCertificate);
        Assert.Null(p.UserName);
        Assert.Null(p.Port);
    }

    [Fact]
    public void Jdbc_sqlserver_url_with_properties()
    {
        var p = Parse("jdbc:sqlserver://sql01\\SALES:1444;databaseName=Orders;user=sa;password={a;b}};c};encrypt=true;trustServerCertificate=true;applicationIntent=ReadOnly;loginTimeout=30");
        Assert.Equal(ConnectionStrings.SqlServer, p.ProviderKey);
        Assert.Equal("sql01\\SALES", p.Host);
        Assert.Equal(1444, p.Port);
        Assert.Equal("Orders", p.Database);
        Assert.Equal("sa", p.UserName);
        Assert.Equal("a;b};c", p.Password);
        Assert.True(p.Encrypt);
        Assert.True(p.TrustServerCertificate);
        Assert.True(p.ReadOnlyIntent);
        Assert.Equal(["loginTimeout"], p.Ignored);
    }

    [Fact]
    public void Jdbc_sqlserver_integrated_security()
    {
        var p = Parse("jdbc:sqlserver://localhost;integratedSecurity=true;databaseName=x");
        Assert.Equal("localhost", p.Host);
        Assert.True(p.IntegratedSecurity);
    }

    [Fact]
    public void Go_style_sqlserver_url_puts_the_instance_in_the_path()
    {
        var p = Parse("sqlserver://sa:secret@sql01:1433/SQLEXPRESS?database=Shop&encrypt=disable");
        Assert.Equal("sql01\\SQLEXPRESS", p.Host);
        Assert.Equal("Shop", p.Database);
        Assert.Equal("secret", p.Password);
        Assert.False(p.Encrypt);
    }

    [Fact]
    public void Postgres_url_with_several_hosts_uses_the_first_and_says_so()
    {
        var p = Parse("postgresql://u@a.example:5433,b.example:5434/db?target_session_attrs=read-write");
        Assert.Equal("a.example", p.Host);
        Assert.Equal(5433, p.Port);
        Assert.Contains(p.Notes, n => n.Contains("first of 2 hosts"));
        Assert.Contains("target_session_attrs", p.Ignored);
    }

    [Fact]
    public void Ipv6_host_in_brackets()
    {
        var p = Parse("postgres://u@[::1]:5432/db");
        Assert.Equal("::1", p.Host);
        Assert.Equal(5432, p.Port);
    }

    [Fact]
    public void Password_with_an_unescaped_at_sign_splits_on_the_last_at()
    {
        var p = Parse("mysql://root:p@ss@db.local/app");
        Assert.Equal("root", p.UserName);
        Assert.Equal("p@ss", p.Password);
        Assert.Equal("db.local", p.Host);
    }

    [Fact]
    public void Url_host_from_query_parameter()
    {
        var p = Parse("postgresql:///mydb?host=db.internal&port=5555&user=me");
        Assert.Equal("db.internal", p.Host);
        Assert.Equal(5555, p.Port);
        Assert.Equal("mydb", p.Database);
        Assert.Equal("me", p.UserName);
    }

    [Fact]
    public void Unix_socket_host_becomes_localhost_with_a_note()
    {
        var p = Parse("postgresql:///mydb?host=/var/run/postgresql");
        Assert.Equal("localhost", p.Host);
        Assert.Contains(p.Notes, n => n.Contains("Unix sockets"));
    }

    [Fact]
    public void Unknown_url_scheme_is_refused()
    {
        Assert.Contains("mongodb://", Fail("mongodb://u@h/db"));
    }

    // ---- key=value ----

    [Fact]
    public void Sql_server_ado_string()
    {
        var p = Parse("Data Source=tcp:sql01.corp,1450;Initial Catalog=Shop;User ID=app;Password=\"x;y\"\"z\";Encrypt=True;TrustServerCertificate=False;MultipleActiveResultSets=True;ApplicationIntent=ReadWrite");
        Assert.Equal(ConnectionStrings.SqlServer, p.ProviderKey);
        Assert.Equal("sql01.corp", p.Host);
        Assert.Equal(1450, p.Port);
        Assert.Equal("Shop", p.Database);
        Assert.Equal("app", p.UserName);
        Assert.Equal("x;y\"z", p.Password);
        Assert.True(p.Encrypt);
        Assert.False(p.TrustServerCertificate);
        Assert.False(p.ReadOnlyIntent);
        Assert.Equal(["MultipleActiveResultSets"], p.Ignored);
    }

    [Fact]
    public void Sql_server_trusted_connection_with_instance()
    {
        var p = Parse("Server=.\\SQLEXPRESS;Database=master;Trusted_Connection=Yes;", ConnectionStrings.MySql);
        Assert.Equal(ConnectionStrings.SqlServer, p.ProviderKey);
        Assert.Equal(".\\SQLEXPRESS", p.Host);
        Assert.True(p.IntegratedSecurity);
    }

    [Theory]
    [InlineData("Mandatory", true, false)]
    [InlineData("Strict", true, false)]
    [InlineData("Optional", false, null)]
    [InlineData("False", false, null)]
    public void Sql_server_encrypt_values(string value, bool encrypt, bool? trust)
    {
        var p = Parse($"Server=s,1433;Encrypt={value}");
        Assert.Equal(encrypt, p.Encrypt);
        Assert.Equal(trust, p.TrustServerCertificate);
    }

    [Fact]
    public void Odbc_driver_names_the_engine_and_braces_quote_values()
    {
        var p = Parse("Driver={ODBC Driver 18 for SQL Server};Server=s;Uid=u;Pwd={se;cr}}et};");
        Assert.Equal(ConnectionStrings.SqlServer, p.ProviderKey);
        Assert.Equal("se;cr}et", p.Password);
        Assert.Empty(p.Ignored);

        Assert.Equal(ConnectionStrings.MySql, Parse("Driver={MySQL ODBC 8.0 Unicode Driver};Server=s;Database=d").ProviderKey);
        Assert.Equal(ConnectionStrings.Postgres, Parse("Driver={PostgreSQL Unicode};Server=s;Database=d", ConnectionStrings.MySql).ProviderKey);
    }

    [Fact]
    public void Npgsql_string()
    {
        var p = Parse("Host=pg.local;Port=5432;Database=app;Username=me;Password=pw;SSL Mode=Require;Trust Server Certificate=true;Search Path=public",
            ConnectionStrings.SqlServer);
        Assert.Equal(ConnectionStrings.Postgres, p.ProviderKey);
        Assert.Equal("pg.local", p.Host);
        Assert.Equal("me", p.UserName);
        Assert.True(p.Encrypt);
        Assert.True(p.TrustServerCertificate);
        Assert.Equal(["Search Path"], p.Ignored);
    }

    [Fact]
    public void MySqlConnector_string()
    {
        var p = Parse("Server=my.local;Port=3307;Database=app;Uid=root;Pwd=pw;SslMode=Preferred;AllowPublicKeyRetrieval=true", ConnectionStrings.SqlServer);
        Assert.Equal(ConnectionStrings.MySql, p.ProviderKey);
        Assert.Equal(3307, p.Port);
        Assert.Equal("root", p.UserName);
        Assert.False(p.Encrypt);
        Assert.Equal(["AllowPublicKeyRetrieval"], p.Ignored);
    }

    [Fact]
    public void Mysql_failover_server_list_uses_the_first()
    {
        var p = Parse("Server=a,b,c;Database=x;CharSet=utf8mb4");
        Assert.Equal(ConnectionStrings.MySql, p.ProviderKey);
        Assert.Equal("a", p.Host);
        Assert.Contains(p.Notes, n => n.Contains("first of 3"));
    }

    [Fact]
    public void Libpq_keyword_string()
    {
        var p = Parse("host=localhost port=5433 dbname='my db' user=me password='it\\'s' sslmode=disable", ConnectionStrings.SqlServer);
        Assert.Equal(ConnectionStrings.Postgres, p.ProviderKey);
        Assert.Equal(5433, p.Port);
        Assert.Equal("my db", p.Database);
        Assert.Equal("it's", p.Password);
        Assert.False(p.Encrypt);
    }

    [Fact]
    public void Libpq_quoted_value_may_hold_a_semicolon()
    {
        var p = Parse("host=h dbname=d user=u password='a;b'", ConnectionStrings.SqlServer);
        Assert.Equal(ConnectionStrings.Postgres, p.ProviderKey);
        Assert.Equal("a;b", p.Password);
        Assert.True(ConnectionStrings.LooksLikeConnectionString("host=h password='a;b'"));
    }

    [Theory]
    [InlineData("mssql+pyodbc://app:pw@sql01/Orders")]
    [InlineData("mssql://app:pw@sql01/Orders")]
    public void Sqlalchemy_mssql_path_is_the_database(string url)
    {
        var p = Parse(url);
        Assert.Equal("sql01", p.Host);
        Assert.Equal("Orders", p.Database);
    }

    [Theory]
    [InlineData("'secret")]
    [InlineData("\"secret")]
    public void Jdbc_password_starting_with_a_quote_round_trips(string password)
    {
        var profile = new ConnectionProfile { ProviderKey = ConnectionStrings.SqlServer, Host = "h", UserName = "u", Password = password };
        Assert.Equal(password, Parse(ConnectionStrings.Format(profile, ConnectionStringStyle.Url, includePassword: true)).Password);
    }

    [Fact]
    public void Ambiguous_string_keeps_the_selected_engine()
    {
        var p = Parse("Server=db1;Database=x;User ID=u;Password=p", ConnectionStrings.MySql);
        Assert.Equal(ConnectionStrings.MySql, p.ProviderKey);
        Assert.False(p.EngineDetected);
        Assert.Contains("selected one was kept", ConnectionStrings.Describe(p));
    }

    [Fact]
    public void Quoted_whole_string_from_code_is_unwrapped()
    {
        Assert.Equal("h", Parse("\"Host=h;Username=u;SSL Mode=Prefer\"").Host);
    }

    [Theory]
    [InlineData("")]
    [InlineData("just-a-host")]
    [InlineData("Pooling=true;Timeout=5")]
    [InlineData("Server=h;Port=abc")]
    [InlineData("Server=h;Encrypt=maybe")]
    [InlineData("Server=h;Password=\"unterminated")]
    [InlineData("Server")]
    public void Bad_input_is_explained(string text) => Fail(text);

    [Theory]
    [InlineData("postgres://u@h/db", true)]
    [InlineData("Server=h;Database=d", true)]
    [InlineData("host=h dbname=d", true)]
    [InlineData("db.example.com", false)]
    [InlineData("sql01\\SQLEXPRESS", false)]
    [InlineData("https://example.com", false)]
    [InlineData("Server=h", false)]
    public void Looks_like_connection_string(string text, bool expected) =>
        Assert.Equal(expected, ConnectionStrings.LooksLikeConnectionString(text));

    // ---- writing, and reading back ----

    private static ConnectionProfile Profile(string engine, bool encrypt = true, bool trust = false, int? port = null) => new()
    {
        ProviderKey = engine,
        Host = "db.example.com",
        Port = port,
        Database = "shop;main",
        UserName = "app user",
        Password = "p@ss;w=rd\"",
        Encrypt = encrypt,
        TrustServerCertificate = trust,
        ReadOnlyIntent = false
    };

    public static TheoryData<string, ConnectionStringStyle, bool, bool> RoundTrips()
    {
        var data = new TheoryData<string, ConnectionStringStyle, bool, bool>();
        foreach (var engine in new[] { ConnectionStrings.Postgres, ConnectionStrings.SqlServer, ConnectionStrings.MySql })
        foreach (var style in Enum.GetValues<ConnectionStringStyle>())
        foreach (var (encrypt, trust) in new[] { (false, true), (true, true), (true, false) })
            data.Add(engine, style, encrypt, trust);
        return data;
    }

    [Theory]
    [MemberData(nameof(RoundTrips))]
    public void Format_then_parse_gives_the_same_connection(string engine, ConnectionStringStyle style, bool encrypt, bool trust)
    {
        var profile = Profile(engine, encrypt, trust, port: 4000);
        var text = ConnectionStrings.Format(profile, style, includePassword: true);
        // Read back with a different engine selected: the text itself must say which engine it is.
        var other = engine == ConnectionStrings.MySql ? ConnectionStrings.Postgres : ConnectionStrings.MySql;
        var p = Parse(text, other);

        Assert.Equal(engine, p.ProviderKey);
        Assert.True(p.EngineDetected, text);
        Assert.Equal(profile.Host, p.Host);
        Assert.Equal(4000, p.Port);
        Assert.Equal(profile.Database, p.Database);
        Assert.Equal(profile.UserName, p.UserName);
        Assert.Equal(profile.Password, p.Password);
        Assert.Equal(encrypt, p.Encrypt);
        if (encrypt) Assert.Equal(trust, p.TrustServerCertificate);
        Assert.Empty(p.Ignored);
    }

    [Theory]
    [InlineData("Postgres")]
    [InlineData("SqlServer")]
    [InlineData("MySql")]
    public void Format_leaves_the_password_out_unless_asked(string engine)
    {
        foreach (var style in Enum.GetValues<ConnectionStringStyle>())
        {
            Assert.DoesNotContain("w=rd", ConnectionStrings.Format(Profile(engine), style));
            Assert.Contains("rd", ConnectionStrings.Format(Profile(engine), style, includePassword: true));
        }
    }

    [Fact]
    public void Key_value_output_is_read_by_the_real_drivers()
    {
        var pg = new NpgsqlConnectionStringBuilder(ConnectionStrings.Format(Profile(ConnectionStrings.Postgres), ConnectionStringStyle.KeyValue, true));
        Assert.Equal("shop;main", pg.Database);
        Assert.Equal("p@ss;w=rd\"", pg.Password);
        Assert.Equal(SslMode.VerifyFull, pg.SslMode);

        var sql = new SqlConnectionStringBuilder(ConnectionStrings.Format(Profile(ConnectionStrings.SqlServer, port: 1450), ConnectionStringStyle.KeyValue, true));
        Assert.Equal("db.example.com,1450", sql.DataSource);
        Assert.Equal("p@ss;w=rd\"", sql.Password);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, sql.Encrypt);
        Assert.False(sql.TrustServerCertificate);

        var my = new MySqlConnectionStringBuilder(ConnectionStrings.Format(Profile(ConnectionStrings.MySql, encrypt: true, trust: true), ConnectionStringStyle.KeyValue, true));
        Assert.Equal("app user", my.UserID);
        Assert.Equal("p@ss;w=rd\"", my.Password);
        Assert.Equal(MySqlSslMode.Required, my.SslMode);
    }

    [Fact]
    public void Integrated_security_is_written_without_a_user()
    {
        var p = Profile(ConnectionStrings.SqlServer);
        p.IntegratedSecurity = true;
        var text = ConnectionStrings.Format(p, ConnectionStringStyle.KeyValue, includePassword: true);
        Assert.Contains("Integrated Security=True", text);
        Assert.DoesNotContain("User ID", text);
        Assert.DoesNotContain("Password", text);
        Assert.True(Parse(text).IntegratedSecurity);
    }

    // ---- the dialog ----

    private static ProviderRegistry Registry() => new(
    [
        new PostgresProviderFactory(),
        new SqlServerProviderFactory(),
        new MySqlProviderFactory()
    ]);

    private static ConnectionDialogViewModel NewDialog(string engine = "Postgres") =>
        new(Registry(), new ConnectionProfile { ProviderKey = engine }, isNew: true);

    [Fact]
    public void Dialog_fill_sets_every_field_and_clears_the_box()
    {
        var vm = NewDialog();
        vm.ConnectionStringText = "jdbc:sqlserver://sql01:1450;databaseName=Shop;user=app;password=pw;encrypt=true;trustServerCertificate=true";
        vm.FillFromConnectionStringCommand.Execute(null);

        Assert.Equal("SqlServer", vm.SelectedProvider.Key);
        Assert.Equal("sql01", vm.Host);
        Assert.Equal("1450", vm.Port);
        Assert.Equal("Shop", vm.Database);
        Assert.Equal("app", vm.UserName);
        Assert.Equal("pw", vm.Password);
        Assert.True(vm.Encrypt);
        Assert.True(vm.TrustServerCertificate);
        Assert.Equal("", vm.ConnectionStringText);
        Assert.False(vm.ConnectionStringFailed);
        Assert.StartsWith("✓ Filled in SQL Server: sql01/Shop", vm.ConnectionStringMessage);
    }

    [Fact]
    public void Dialog_tls_follows_the_string_and_then_stays()
    {
        var vm = NewDialog();
        Assert.True(vm.Fill("postgres://u@db.example.com/x?sslmode=require", clearBox: true));
        Assert.True(vm.Encrypt);
        Assert.True(vm.TrustServerCertificate);
        // The string chose TLS; editing the host afterwards must not reset it to the host defaults.
        vm.Host = "localhost";
        Assert.True(vm.Encrypt);
    }

    [Fact]
    public void Dialog_without_tls_in_the_string_uses_the_host_defaults()
    {
        var vm = NewDialog();
        Assert.True(vm.Fill("postgres://u@db.example.com/x", clearBox: true));
        Assert.Equal(ConnectionSecurity.DefaultsFor("db.example.com"), (vm.Encrypt, vm.TrustServerCertificate));
        Assert.True(vm.Fill("postgres://u@localhost/x", clearBox: true));
        Assert.Equal(ConnectionSecurity.DefaultsFor("localhost"), (vm.Encrypt, vm.TrustServerCertificate));
    }

    [Fact]
    public void Dialog_pasting_into_host_fills_everything()
    {
        var vm = NewDialog("SqlServer");
        vm.Host = "mysql://root:pw@my.local:3307/app";
        Assert.Equal("MySql", vm.SelectedProvider.Key);
        Assert.Equal("my.local", vm.Host);
        Assert.Equal("3307", vm.Port);
        Assert.Equal("app", vm.Database);
        Assert.Equal("pw", vm.Password);
    }

    [Fact]
    public void Dialog_keeps_a_typed_password_only_for_the_same_user()
    {
        var vm = NewDialog();
        vm.UserName = "me";
        vm.Password = "typed";
        vm.Fill("postgres://me@h/db", clearBox: true);
        Assert.Equal("typed", vm.Password);
        vm.Fill("postgres://someone@h/db", clearBox: true);
        Assert.Equal("", vm.Password);
    }

    [Fact]
    public void Dialog_failed_fill_keeps_fields_and_explains()
    {
        var vm = NewDialog();
        vm.Host = "keep.me";
        vm.ConnectionStringText = "Server=h;Port=nope";
        vm.FillFromConnectionStringCommand.Execute(null);
        Assert.Equal("keep.me", vm.Host);
        Assert.True(vm.ConnectionStringFailed);
        Assert.Contains("port", vm.ConnectionStringMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Server=h;Port=nope", vm.ConnectionStringText);
    }

    [Fact]
    public void Dialog_offers_the_clipboard_only_for_new_connections()
    {
        var vm = NewDialog();
        vm.OfferClipboard("just some text");
        Assert.False(vm.HasClipboardOffer);
        vm.OfferClipboard("postgresql://u:pw@db.example.com/shop");
        Assert.Equal("The clipboard has a PostgreSQL connection string for db.example.com/shop.", vm.ClipboardOffer);
        Assert.Equal("localhost", vm.Host); // only offered, not used
        vm.UseClipboardCommand.Execute(null);
        Assert.Equal("db.example.com", vm.Host);
        Assert.False(vm.HasClipboardOffer);

        var edit = new ConnectionDialogViewModel(Registry(), new ConnectionProfile { ProviderKey = "Postgres" });
        edit.OfferClipboard("postgresql://u:pw@db.example.com/shop");
        Assert.False(edit.HasClipboardOffer);
    }

    [Fact]
    public void Dialog_copy_leaves_out_the_password_by_default()
    {
        var vm = NewDialog();
        vm.Host = "h";
        vm.UserName = "u";
        vm.Password = "secret";
        string? copied = null;
        vm.CopyRequested += text => copied = text;
        vm.CopyConnectionStringCommand.Execute("url");
        Assert.Equal("postgresql://u@h/?sslmode=verify-full", copied); // a remote host: encrypt and verify by default
        vm.CopyConnectionStringCommand.Execute("keyvalue+password");
        Assert.Contains("Password=secret", copied);
    }
}
