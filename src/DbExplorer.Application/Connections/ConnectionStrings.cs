using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DbExplorer.Core.Connections;

namespace DbExplorer.Application.Connections;

/// <summary>What a pasted connection string says. Null means the text did not say, so the dialog keeps its own value.</summary>
public sealed record ParsedConnection
{
    public required string ProviderKey { get; init; }

    /// <summary>False when nothing in the text named the engine and the caller's guess was kept.</summary>
    public bool EngineDetected { get; init; } = true;

    public string? Host { get; init; }
    public int? Port { get; init; }
    public string? Database { get; init; }
    public string? UserName { get; init; }
    public string? Password { get; init; }
    public bool? IntegratedSecurity { get; init; }

    /// <summary>Microsoft Entra ID sign-in from "Authentication=Active Directory …" (SQL Server); null when not said.</summary>
    public EntraAuthMode? EntraAuth { get; init; }

    public bool? Encrypt { get; init; }
    public bool? TrustServerCertificate { get; init; }
    public bool? ReadOnlyIntent { get; init; }

    /// <summary>Keys (or URL parameters) the app has no field for, as they were written.</summary>
    public IReadOnlyList<string> Ignored { get; init; } = [];

    /// <summary>Things the user should know, e.g. "Only the first of 2 hosts is used."</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

public enum ConnectionStringStyle
{
    /// <summary>Key=value pairs the engine's .NET driver reads (Npgsql, SqlClient, MySqlConnector).</summary>
    KeyValue,

    /// <summary>postgresql://, mysql:// or (for SQL Server, which has no common URL form) jdbc:sqlserver://.</summary>
    Url
}

/// <summary>
/// Reads connection strings and URLs people paste (postgres://, mysql://, sqlserver://, jdbc:…, ADO.NET and ODBC
/// key=value, libpq "host=… dbname=…") into connection fields, and writes a profile back out as one. No driver is
/// involved: the text is only read, never used to connect, so unknown keys can be listed instead of failing.
/// </summary>
public static class ConnectionStrings
{
    public const string Postgres = "Postgres";
    public const string SqlServer = "SqlServer";
    public const string MySql = "MySql";

    public static string EngineName(string providerKey) => providerKey switch
    {
        Postgres => "PostgreSQL",
        SqlServer => "SQL Server",
        MySql => "MySQL",
        _ => providerKey
    };

    /// <summary>True when the text is plainly meant as a connection string or URL rather than a host name.</summary>
    public static bool LooksLikeConnectionString(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        if (UrlPattern.IsMatch(t)) return SchemeEngine(UrlPattern.Match(t).Groups["scheme"].Value) is not null;
        return t.Contains('=') && (t.Contains(';') || IsLibpq(t));
    }

    /// <summary>
    /// Reads <paramref name="text"/>. <paramref name="fallbackProviderKey"/> is used when the text could be any
    /// engine (Server=…;Database=…;User ID=… means the same thing to all three).
    /// </summary>
    public static bool TryParse(string? text, string fallbackProviderKey, out ParsedConnection result, out string error)
    {
        result = null!;
        error = "";
        var t = text?.Trim() ?? "";
        if (t.Length == 0) { error = "Paste a connection string or URL first."; return false; }

        // The whole string was copied with its quotes, from code or a config file.
        if (t.Length > 1 && (t[0] == '"' && t[^1] == '"' || t[0] == '\'' && t[^1] == '\'')) t = t[1..^1].Trim();

        try
        {
            if (UrlPattern.Match(t) is { Success: true } url)
                return TryParseUrl(url, fallbackProviderKey, out result, out error);
            if (!t.Contains('='))
            {
                error = "This isn't a connection string. Expected a URL like postgresql://user@host/db or pairs like Server=…;Database=…";
                return false;
            }
            var libpq = IsLibpq(t);
            var pairs = libpq ? SplitLibpq(t) : SplitKeyValue(t);
            if (pairs.Count == 0) { error = "No key=value pairs found."; return false; }
            var engine = libpq ? Postgres : DetectEngine(pairs);
            var fields = new FieldReader(engine ?? fallbackProviderKey, engineDetected: engine is not null);
            foreach (var (key, value) in pairs) fields.Read(key, value);
            return fields.TryBuild(out result, out error);
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // ---- URLs -------------------------------------------------------------------------------------------------

    private static readonly Regex UrlPattern = new(@"^(?:jdbc:)?(?<scheme>[a-z][a-z0-9+.\-]*)://(?<rest>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static string? SchemeEngine(string scheme)
    {
        // SQLAlchemy writes the driver after a plus: postgresql+psycopg2://, mysql+pymysql://, mssql+pyodbc://.
        var s = scheme.ToLowerInvariant();
        var plus = s.IndexOf('+');
        if (plus > 0) s = s[..plus];
        return s switch
        {
            "postgres" or "postgresql" or "pgsql" => Postgres,
            "mysql" or "mariadb" or "mysqlx" => MySql,
            "sqlserver" or "mssql" => SqlServer,
            _ => null
        };
    }

    private static bool TryParseUrl(Match url, string fallback, out ParsedConnection result, out string error)
    {
        result = null!;
        error = "";
        var scheme = url.Groups["scheme"].Value;
        if (SchemeEngine(scheme) is not { } engine)
        {
            error = $"\"{scheme}://\" isn't a database this app connects to. It reads postgresql://, mysql://, mariadb://, sqlserver:// and jdbc: URLs.";
            return false;
        }
        var rest = url.Groups["rest"].Value.Trim();
        var fields = new FieldReader(engine, engineDetected: true);

        // jdbc:sqlserver://host\instance:port;databaseName=x;user=y — the properties follow a semicolon.
        string? properties = null;
        var semicolon = rest.IndexOf(';');
        var authorityEnd = IndexOfAny(rest, '/', '?', ';');
        if (semicolon >= 0 && semicolon == authorityEnd)
        {
            properties = rest[(semicolon + 1)..];
            rest = rest[..semicolon];
        }

        var hash = rest.IndexOf('#');
        if (hash >= 0) rest = rest[..hash];

        string? query = null;
        var q = rest.IndexOf('?');
        if (q >= 0) { query = rest[(q + 1)..]; rest = rest[..q]; }

        string? path = null;
        var slash = rest.IndexOf('/');
        if (slash >= 0) { path = rest[(slash + 1)..]; rest = rest[..slash]; }

        // user:password@host — the last @ wins, so a password with an unescaped @ still splits correctly.
        var at = rest.LastIndexOf('@');
        if (at >= 0)
        {
            var userInfo = rest[..at];
            rest = rest[(at + 1)..];
            var colon = userInfo.IndexOf(':');
            var user = Decode(colon >= 0 ? userInfo[..colon] : userInfo);
            if (user.Length > 0) fields.UserName = user;
            if (colon >= 0) fields.Password = Decode(userInfo[(colon + 1)..]);
        }

        if (rest.Length > 0) fields.ReadHostList(Decode(rest), fromUrl: true);

        if (!string.IsNullOrEmpty(path))
        {
            var p = Decode(path.TrimEnd('/'));
            // go-mssqldb puts the instance in the path (sqlserver://host/SQLEXPRESS?database=x); SQLAlchemy's
            // mssql:// and mssql+pyodbc:// put the database there, like the other engines.
            if (engine == SqlServer && scheme.Equals("sqlserver", StringComparison.OrdinalIgnoreCase)) { if (p.Length > 0) fields.Instance = p; }
            else if (p.Length > 0) fields.Database = p;
        }

        if (!string.IsNullOrEmpty(query))
            foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                fields.Read(Decode(eq >= 0 ? part[..eq] : part), eq >= 0 ? Decode(part[(eq + 1)..]) : "true");
            }

        if (properties is not null)
            foreach (var (key, value) in SplitKeyValue(properties)) fields.Read(key, value);

        return fields.TryBuild(out result, out error);
    }

    private static int IndexOfAny(string s, params char[] chars)
    {
        var i = s.IndexOfAny(chars);
        return i < 0 ? s.Length : i;
    }

    private static string Decode(string s)
    {
        try { return Uri.UnescapeDataString(s); }
        catch (UriFormatException) { return s; }
    }

    // ---- key=value --------------------------------------------------------------------------------------------

    private static readonly HashSet<string> LibpqKeywords =
    [
        "host", "hostaddr", "port", "dbname", "user", "password", "passfile", "connect_timeout", "client_encoding",
        "options", "application_name", "fallback_application_name", "keepalives", "keepalives_idle", "sslmode",
        "sslcert", "sslkey", "sslrootcert", "sslcrl", "sslpassword", "target_session_attrs", "gssencmode",
        "channel_binding", "krbsrvname", "service", "load_balance_hosts", "requiressl", "sslcompression"
    ];

    /// <summary>Space-separated pairs whose keys are all libpq keywords (libpq writes them in lower case).</summary>
    private static bool IsLibpq(string t)
    {
        if (HasUnquotedSemicolon(t) || !LibpqPattern.IsMatch(t)) return false;
        try { return SplitLibpq(t).All(kv => LibpqKeywords.Contains(kv.Key)); }
        catch (FormatException) { return false; }
    }

    /// <summary>A semicolon outside libpq's '…' quotes: a password='a;b' is still one libpq value.</summary>
    private static bool HasUnquotedSemicolon(string t)
    {
        var quoted = false;
        for (var i = 0; i < t.Length; i++)
        {
            if (quoted && t[i] == '\\') { i++; continue; }
            if (t[i] == '\'') quoted = !quoted;
            else if (t[i] == ';' && !quoted) return true;
        }
        return false;
    }

    /// <summary>libpq keyword/value form: host=x port=5432 dbname=y (spaces between pairs, no semicolons).</summary>
    private static readonly Regex LibpqPattern = new(@"^\s*[a-z_]+\s*=\s*(?:'(?:[^'\\]|\\.)*'|\S+)(?:\s+[a-z_]+\s*=\s*(?:'(?:[^'\\]|\\.)*'|\S*))*\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// ADO.NET / ODBC pairs separated by semicolons. Values may be quoted with "…" or '…' (a doubled quote is one
    /// quote) or with ODBC braces {…} (a doubled }} is one brace), so a password can hold a semicolon.
    /// </summary>
    internal static List<(string Key, string Value)> SplitKeyValue(string text)
    {
        var pairs = new List<(string, string)>();
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] == ';')) i++;
            if (i >= text.Length) break;

            var key = new StringBuilder();
            while (i < text.Length && text[i] != ';')
            {
                if (text[i] == '=')
                {
                    if (i + 1 < text.Length && text[i + 1] == '=') { key.Append('='); i += 2; continue; }
                    break;
                }
                key.Append(text[i++]);
            }
            var name = key.ToString().Trim();
            if (i >= text.Length || text[i] == ';')
            {
                if (name.Length > 0) throw new FormatException($"\"{name}\" has no value (expected {name}=…).");
                continue;
            }
            i++; // '='
            while (i < text.Length && text[i] is ' ' or '\t') i++;

            string value;
            if (i < text.Length && text[i] is '"' or '\'' or '{')
            {
                var open = text[i];
                var close = open == '{' ? '}' : open;
                var sb = new StringBuilder();
                i++;
                var closed = false;
                while (i < text.Length)
                {
                    if (text[i] == close)
                    {
                        if (i + 1 < text.Length && text[i + 1] == close) { sb.Append(close); i += 2; continue; }
                        i++;
                        closed = true;
                        break;
                    }
                    sb.Append(text[i++]);
                }
                if (!closed) throw new FormatException($"The value of \"{name}\" has no closing {close}.");
                value = sb.ToString();
                while (i < text.Length && text[i] != ';') i++; // anything after the closing quote is dropped
            }
            else
            {
                var start = i;
                while (i < text.Length && text[i] != ';') i++;
                value = text[start..i].Trim();
            }
            if (name.Length > 0) pairs.Add((name, value));
        }
        return pairs;
    }

    private static List<(string Key, string Value)> SplitLibpq(string text)
    {
        var pairs = new List<(string, string)>();
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length) break;
            var start = i;
            while (i < text.Length && text[i] != '=' && !char.IsWhiteSpace(text[i])) i++;
            var key = text[start..i];
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length || text[i] != '=') throw new FormatException($"\"{key}\" has no value (expected {key}=…).");
            i++;
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            var sb = new StringBuilder();
            if (i < text.Length && text[i] == '\'')
            {
                i++;
                var closed = false;
                while (i < text.Length)
                {
                    if (text[i] == '\\' && i + 1 < text.Length) { sb.Append(text[i + 1]); i += 2; continue; }
                    if (text[i] == '\'') { i++; closed = true; break; }
                    sb.Append(text[i++]);
                }
                if (!closed) throw new FormatException($"The value of \"{key}\" has no closing quote.");
            }
            else
            {
                while (i < text.Length && !char.IsWhiteSpace(text[i]))
                {
                    if (text[i] == '\\' && i + 1 < text.Length) { sb.Append(text[i + 1]); i += 2; continue; }
                    sb.Append(text[i++]);
                }
            }
            pairs.Add((key, sb.ToString()));
        }
        return pairs;
    }

    /// <summary>"User ID", "user_id" and "userId" are the same key to the drivers.</summary>
    internal static string Normalize(string key) =>
        new(key.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static readonly HashSet<string> SqlServerKeys =
    [
        "datasource", "initialcatalog", "integratedsecurity", "authentication", "trustedconnection", "trustservercertificate", "encrypt",
        "applicationintent", "multisubnetfailover", "multipleactiveresultsets", "addr", "networkaddress", "failoverpartner",
        "attachdbfilename", "workstationid", "wsid", "packetsize", "connectretrycount", "connectretryinterval",
        "authentication", "hostnameincertificate", "servercertificate", "columnencryptionsetting", "transactionbinding",
        "databasename", "servername", "instancename", "portnumber"
    ];

    private static readonly HashSet<string> PostgresKeys =
    [
        "username", "dbname", "searchpath", "includeerrordetail", "servercompatibilitymode", "keepalive",
        "tcpkeepalive", "targetsessionattributes", "targetsessionattrs", "hostaddr", "rootcertificate", "sslrootcert",
        "sslcert", "sslkey", "sslpassword", "passfile", "loadtabletypes", "loadbalancehosts", "options",
        "maxautoprepare", "autoprepareminusages", "checkcertificaterevocation", "kerberosservicename", "gssencmode"
    ];

    private static readonly HashSet<string> MySqlKeys =
    [
        "allowpublickeyretrieval", "allowuservariables", "convertzerodatetime", "allowzerodatetime", "characterset",
        "charset", "guidformat", "treattinyasboolean", "oldguids", "defaultcommandtimeout", "sslca", "certificatefile",
        "allowloadlocalinfile", "usecompression", "compress", "usesslcertificate", "serverrsapublickeyfile",
        "tlsversion", "nobackslashescapes", "usexainterface", "cancellationtimeout"
    ];

    private static readonly HashSet<string> PostgresSslModes = ["disable", "allow", "prefer", "require", "verify-ca", "verify-full"];
    private static readonly HashSet<string> MySqlSslModes = ["none", "disabled", "preferred", "required"];

    /// <summary>Which engine a key=value string was written for, or null when it could be any of them.</summary>
    private static string? DetectEngine(List<(string Key, string Value)> pairs)
    {
        int sql = 0, pg = 0, my = 0;
        foreach (var (rawKey, rawValue) in pairs)
        {
            var key = Normalize(rawKey);
            var value = rawValue.Trim().ToLowerInvariant();
            if (SqlServerKeys.Contains(key)) sql += 2;
            if (PostgresKeys.Contains(key)) pg += 2;
            if (MySqlKeys.Contains(key)) my += 2;
            switch (key)
            {
                case "driver":
                case "provider":
                    if (value.Contains("sql server") || value.Contains("sqlncli") || value.Contains("sqloledb") || value.Contains("msoledbsql") || value.Contains("sqlclient")) sql += 5;
                    else if (value.Contains("postgres") || value.Contains("npgsql")) pg += 5;
                    else if (value.Contains("mysql") || value.Contains("maria")) my += 5;
                    break;
                case "sslmode":
                    if (PostgresSslModes.Contains(value)) pg += 3;
                    else if (MySqlSslModes.Contains(value)) my += 3;
                    // Both drivers accept both spellings, but each writes its own: Npgsql "SSL Mode", MySqlConnector "SslMode".
                    else if (rawKey.Trim().Contains(' ')) pg += 1;
                    else my += 1;
                    break;
                case "server" or "host" or "datasource":
                    if (value.StartsWith("tcp:") || value.StartsWith("np:") || value.StartsWith("(localdb)") || value.Contains('\\')
                        || Regex.IsMatch(value, @",\s*\d+\s*$")) sql += 3;
                    if (key == "host") { pg += 1; my += 1; }
                    else if (key == "server") { sql += 1; my += 1; } // Npgsql writes Host
                    break;
                case "port":
                    if (value == "5432") pg += 1;
                    else if (value == "3306") my += 1;
                    else if (value == "1433") sql += 1;
                    break;
                case "uid" or "pwd":
                    my += 1; sql += 1;
                    break;
            }
        }
        var best = Math.Max(sql, Math.Max(pg, my));
        if (best == 0) return null;
        var winners = (sql == best ? 1 : 0) + (pg == best ? 1 : 0) + (my == best ? 1 : 0);
        if (winners > 1) return null;
        return sql == best ? SqlServer : pg == best ? Postgres : MySql;
    }

    /// <summary>Collects fields from keys in any of the engines' spellings, then checks the result.</summary>
    private sealed class FieldReader(string engine, bool engineDetected)
    {
        public string? Host, Instance, Database, UserName, Password;
        public int? Port;
        public bool? Integrated, Encrypt, Trust, ReadOnlyIntent, UseSsl, VerifyServerCertificate;
        public EntraAuthMode? Entra;
        private readonly List<string> _ignored = [];
        private readonly List<string> _notes = [];

        public void Read(string key, string value)
        {
            switch (Normalize(key))
            {
                case "host" or "server" or "datasource" or "address" or "addr" or "networkaddress" or "hostname" or "servername" or "hostaddr":
                    ReadHostList(value, fromUrl: false);
                    break;
                case "port" or "portnumber":
                    Port = ParsePort(value);
                    break;
                case "instance" or "instancename":
                    if (value.Length > 0) Instance = value;
                    break;
                case "database" or "initialcatalog" or "dbname" or "databasename" or "db":
                    Database = value;
                    break;
                case "user" or "userid" or "uid" or "username" or "login":
                    UserName = value;
                    break;
                case "password" or "pwd" or "pass":
                    Password = value;
                    break;
                case "integratedsecurity" or "trustedconnection":
                    Integrated = value.Trim().ToLowerInvariant() is "sspi" || Bool(key, value);
                    break;
                case "authentication":
                    Entra = EntraOf(key, value);
                    break;
                case "encrypt":
                    Encrypt = value.Trim().ToLowerInvariant() switch
                    {
                        "mandatory" or "strict" => true,
                        "optional" or "disable" or "disabled" => false,
                        _ => Bool(key, value)
                    };
                    if (value.Trim().Equals("strict", StringComparison.OrdinalIgnoreCase)) Trust = false;
                    break;
                case "trustservercertificate" or "trustcertificate":
                    Trust = Bool(key, value);
                    break;
                case "sslmode":
                    ReadSslMode(key, value);
                    break;
                case "ssl" or "usessl" or "requiressl":
                    UseSsl = Bool(key, value);
                    break;
                case "verifyservercertificate":
                    VerifyServerCertificate = Bool(key, value);
                    break;
                case "applicationintent":
                    ReadOnlyIntent = Normalize(value) switch
                    {
                        "readonly" => true,
                        "readwrite" => false,
                        _ => throw new FormatException($"ApplicationIntent must be ReadOnly or ReadWrite, not \"{value}\".")
                    };
                    break;
                case "driver" or "provider":
                    break; // only says which engine
                default:
                    if (!_ignored.Contains(key, StringComparer.OrdinalIgnoreCase)) _ignored.Add(key);
                    break;
            }
        }

        private void ReadSslMode(string key, string value)
        {
            switch (Normalize(value))
            {
                case "disable" or "disabled" or "none" or "allow" or "prefer" or "preferred":
                    Encrypt = false;
                    break;
                case "require" or "required":
                    Encrypt = true;
                    Trust = true;
                    break;
                case "verifyca" or "verifyfull" or "verifyidentity":
                    Encrypt = true;
                    Trust = false;
                    break;
                default:
                    throw new FormatException($"Unknown {key} \"{value}\".");
            }
        }

        /// <summary>
        /// One host, or a failover list ("a,b" or "a:5432,b:5433"), where only the first is used. On SQL Server a
        /// comma means a port instead ("host,1433") and a backslash names an instance.
        /// </summary>
        public void ReadHostList(string value, bool fromUrl)
        {
            var v = value.Trim();
            if (v.Length == 0) return;

            if (engine == SqlServer && !fromUrl)
            {
                foreach (var prefix in new[] { "tcp:", "TCP:" })
                    if (v.StartsWith(prefix, StringComparison.Ordinal)) v = v[prefix.Length..];
                if (v.StartsWith("np:", StringComparison.OrdinalIgnoreCase) || v.StartsWith("lpc:", StringComparison.OrdinalIgnoreCase))
                {
                    _notes.Add("Named pipes and shared memory aren't used; the app connects over TCP.");
                    v = v[(v.IndexOf(':') + 1)..];
                }
                var comma = v.LastIndexOf(',');
                if (comma >= 0)
                {
                    Port = ParsePort(v[(comma + 1)..]);
                    v = v[..comma].Trim();
                }
                Host = v;
                return;
            }

            var hosts = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (hosts.Length == 0) return;
            if (hosts.Length > 1) _notes.Add($"Only the first of {hosts.Length} hosts is used ({hosts[0]}).");
            var h = hosts[0];

            // [::1]:5432, host:5432, or a bare IPv6 address; SQL Server URLs are host\instance:port.
            if (h.StartsWith('['))
            {
                var close = h.IndexOf(']');
                if (close < 0) throw new FormatException($"\"{h}\" is missing the closing ].");
                if (close + 1 < h.Length && h[close + 1] == ':') Port = ParsePort(h[(close + 2)..]);
                h = h[1..close];
            }
            else if (h.Count(c => c == ':') == 1)
            {
                var colon = h.IndexOf(':');
                if (colon + 1 < h.Length) Port = ParsePort(h[(colon + 1)..]);
                h = h[..colon];
            }
            // A PostgreSQL Unix socket directory (/var/run/postgresql) is a host to libpq but not to this app.
            if (h.StartsWith('/')) { _notes.Add($"Unix sockets aren't supported; {h} was replaced with localhost."); h = "localhost"; }
            Host = h;
        }

        private static int ParsePort(string value)
        {
            var v = value.Trim();
            if (int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535) return port;
            throw new FormatException($"\"{v}\" isn't a port (a number from 1 to 65535).");
        }

        /// <summary>SqlClient's and JDBC's Authentication values ("Active Directory Interactive", ActiveDirectoryMSI…).</summary>
        private static EntraAuthMode EntraOf(string key, string value) => Normalize(value) switch
        {
            "sqlpassword" or "" => EntraAuthMode.None,
            "activedirectoryinteractive" => EntraAuthMode.Interactive,
            "activedirectoryintegrated" => EntraAuthMode.Integrated,
            "activedirectorypassword" => EntraAuthMode.Password,
            // Managed identity and workload identity are both among the sources the default chain tries.
            "activedirectorydefault" or "activedirectorymsi" or "activedirectorymanagedidentity" or "activedirectoryworkloadidentity"
                => EntraAuthMode.Default,
            "activedirectoryserviceprincipal" => EntraAuthMode.ServicePrincipal,
            _ => throw new FormatException($"{key} \"{value}\" isn't a sign-in method DB Explorer knows.")
        };

        private static bool Bool(string key, string value) => value.Trim().ToLowerInvariant() switch
        {
            "true" or "yes" or "1" or "on" => true,
            "false" or "no" or "0" or "off" => false,
            _ => throw new FormatException($"{key} must be true or false, not \"{value}\".")
        };

        public bool TryBuild(out ParsedConnection result, out string error)
        {
            result = null!;
            error = "";
            if (Host is null && Database is null && UserName is null && Password is null)
            {
                error = _ignored.Count > 0
                    ? $"No server, database or user found. Keys not recognised: {string.Join(", ", _ignored)}."
                    : "No server, database or user found.";
                return false;
            }

            var host = Host;
            if (Instance is not null)
            {
                if (engine == SqlServer) host = $"{host ?? "localhost"}\\{Instance}";
                else _ignored.Add("instance");
            }

            // ssl=true / useSSL=true: pgJDBC then verifies the certificate; MySQL Connector/J only does when
            // verifyServerCertificate=true. sslmode, when also given, wins.
            var encrypt = Encrypt;
            var trust = Trust;
            if (encrypt is null && UseSsl is { } ssl)
            {
                encrypt = ssl;
                if (ssl && trust is null) trust = engine == MySql ? VerifyServerCertificate != true : false;
            }
            if (VerifyServerCertificate is { } verify && Trust is null && Encrypt is null) trust = !verify;
            // A string that asks for encryption but says nothing about the certificate gets the drivers' default: check it.
            if (encrypt == true && trust is null) trust = false;

            if (Entra is { } entra && entra != EntraAuthMode.None && engine != SqlServer)
            {
                _notes.Add("Pick the Microsoft Entra sign-in in the dialog; this engine's strings don't carry it.");
                Entra = null;
            }

            if (Integrated == true && engine == MySql)
            {
                _notes.Add("MySQL has no integrated sign-in here; enter a user and password.");
            }

            result = new ParsedConnection
            {
                ProviderKey = engine,
                EngineDetected = engineDetected,
                Host = host,
                Port = Port,
                Database = Database,
                UserName = UserName,
                Password = Password,
                IntegratedSecurity = engine == MySql ? null : Entra is { } e && e != EntraAuthMode.None ? false : Integrated,
                EntraAuth = Entra,
                Encrypt = encrypt,
                TrustServerCertificate = trust,
                ReadOnlyIntent = engine == SqlServer ? ReadOnlyIntent : null,
                Ignored = engine != SqlServer && ReadOnlyIntent is not null ? [.. _ignored, "ApplicationIntent"] : _ignored,
                Notes = _notes
            };
            return true;
        }
    }

    /// <summary>One line for the dialog: what was filled in and what was left out.</summary>
    public static string Describe(ParsedConnection p)
    {
        var sb = new StringBuilder();
        sb.Append("Filled in ").Append(EngineName(p.ProviderKey));
        if (!p.EngineDetected) sb.Append(" (the text works for any engine, so the selected one was kept)");
        var target = p.Host is null ? p.Database : p.Database is null ? p.Host : $"{p.Host}/{p.Database}";
        if (target is not null) sb.Append(": ").Append(target);
        sb.Append('.');
        foreach (var note in p.Notes) sb.Append(' ').Append(note);
        if (p.Ignored.Count > 0) sb.Append(" Not used: ").Append(string.Join(", ", p.Ignored)).Append('.');
        return sb.ToString();
    }

    // ---- writing ----------------------------------------------------------------------------------------------

    /// <summary>
    /// The profile as a string other tools accept. The password is left out unless asked for. TLS is written the way
    /// this app connects: Encrypt alone verifies the certificate, Encrypt with Trust certificate encrypts without
    /// checking, and no Encrypt uses TLS when the server offers it.
    /// </summary>
    public static string Format(ConnectionProfile p, ConnectionStringStyle style, bool includePassword = false)
    {
        var password = includePassword && p.NeedsPassword ? p.Password : null;
        var user = p.IntegratedSecurity || p.EntraAuth is EntraAuthMode.Integrated or EntraAuthMode.Default && p.ProviderKey == SqlServer
            ? null : p.UserName;
        return (p.ProviderKey, style) switch
        {
            (SqlServer, ConnectionStringStyle.KeyValue) => SqlServerKeyValue(p, user, password),
            (SqlServer, ConnectionStringStyle.Url) => SqlServerJdbc(p, user, password),
            (MySql, ConnectionStringStyle.KeyValue) => Pairs(
                ("Server", p.Host), ("Port", (p.Port ?? 3306).ToString(CultureInfo.InvariantCulture)), ("Database", p.Database),
                ("User ID", user), ("Password", password),
                ("SslMode", !p.Encrypt ? "Preferred" : p.TrustServerCertificate ? "Required" : "VerifyFull")),
            (MySql, ConnectionStringStyle.Url) => Url("mysql", p, user, password,
                "ssl-mode=" + (!p.Encrypt ? "PREFERRED" : p.TrustServerCertificate ? "REQUIRED" : "VERIFY_IDENTITY")),
            (_, ConnectionStringStyle.KeyValue) => Pairs(
                ("Host", p.Host), ("Port", (p.Port ?? 5432).ToString(CultureInfo.InvariantCulture)), ("Database", p.Database),
                ("Username", user), ("Password", password), ("Integrated Security", p.IntegratedSecurity ? "true" : null),
                ("SSL Mode", !p.Encrypt ? "Prefer" : p.TrustServerCertificate ? "Require" : "VerifyFull")),
            _ => Url("postgresql", p, user, password,
                "sslmode=" + (!p.Encrypt ? "prefer" : p.TrustServerCertificate ? "require" : "verify-full"))
        };
    }

    private static string SqlServerKeyValue(ConnectionProfile p, string? user, string? password) => Pairs(
        ("Server", p.Port is int port ? $"{p.Host},{port.ToString(CultureInfo.InvariantCulture)}" : p.Host),
        ("Database", p.Database),
        ("Integrated Security", p.IntegratedSecurity && p.EntraAuth == EntraAuthMode.None ? "True" : null),
        ("Authentication", SqlClientAuthentication(p.EntraAuth)),
        ("User ID", user), ("Password", password),
        ("Encrypt", p.Encrypt ? "True" : "False"),
        ("TrustServerCertificate", p.TrustServerCertificate ? "True" : "False"),
        ("ApplicationIntent", p.ReadOnlyIntent ? "ReadOnly" : null));

    /// <summary>The Authentication value SqlClient writes for an Entra method (JDBC: the same without spaces).</summary>
    private static string? SqlClientAuthentication(EntraAuthMode mode) => mode switch
    {
        EntraAuthMode.Interactive => "Active Directory Interactive",
        EntraAuthMode.Integrated => "Active Directory Integrated",
        EntraAuthMode.Password => "Active Directory Password",
        EntraAuthMode.Default => "Active Directory Default",
        EntraAuthMode.ServicePrincipal => "Active Directory Service Principal",
        _ => null
    };

    private static string SqlServerJdbc(ConnectionProfile p, string? user, string? password)
    {
        var sb = new StringBuilder("jdbc:sqlserver://").Append(p.Host);
        if (p.Port is int port) sb.Append(':').Append(port.ToString(CultureInfo.InvariantCulture));
        void Add(string key, string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            // JDBC properties put a value holding ; or } inside braces, with } doubled.
            sb.Append(';').Append(key).Append('=')
                .Append(value.IndexOfAny([';', '{', '}', '=']) >= 0 || value[0] is '"' or '\'' || value != value.Trim() ? "{" + value.Replace("}", "}}") + "}" : value);
        }
        Add("databaseName", p.Database);
        Add("integratedSecurity", p.IntegratedSecurity && p.EntraAuth == EntraAuthMode.None ? "true" : null);
        Add("authentication", SqlClientAuthentication(p.EntraAuth)?.Replace(" ", ""));
        Add("user", user);
        Add("password", password);
        Add("encrypt", p.Encrypt ? "true" : "false");
        Add("trustServerCertificate", p.TrustServerCertificate ? "true" : "false");
        Add("applicationIntent", p.ReadOnlyIntent ? "ReadOnly" : null);
        return sb.ToString();
    }

    private static string Url(string scheme, ConnectionProfile p, string? user, string? password, string query)
    {
        var sb = new StringBuilder(scheme).Append("://");
        if (!string.IsNullOrEmpty(user))
        {
            sb.Append(Uri.EscapeDataString(user));
            if (!string.IsNullOrEmpty(password)) sb.Append(':').Append(Uri.EscapeDataString(password));
            sb.Append('@');
        }
        var host = p.Host.Trim();
        sb.Append(host.Contains(':') ? $"[{host}]" : host);
        if (p.Port is int port) sb.Append(':').Append(port.ToString(CultureInfo.InvariantCulture));
        sb.Append('/');
        if (!string.IsNullOrEmpty(p.Database)) sb.Append(Uri.EscapeDataString(p.Database));
        return sb.Append('?').Append(query).ToString();
    }

    private static string Pairs(params (string Key, string? Value)[] pairs) => string.Join(";",
        pairs.Where(kv => !string.IsNullOrEmpty(kv.Value)).Select(kv => $"{kv.Key}={Quote(kv.Value!)}"));

    /// <summary>Quotes a value the way DbConnectionStringBuilder does when it holds ; = quotes or edge spaces.</summary>
    private static string Quote(string value)
    {
        if (value.IndexOfAny([';', '=', '"', '\'', '{']) < 0 && value == value.Trim()) return value;
        return value.Contains('"') && !value.Contains('\'')
            ? "'" + value + "'"
            : "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
