using System.Text.Json.Serialization;

namespace DbExplorer.Core.Connections;

public sealed class ConnectionProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";

    /// <summary>Groups connections in the list ("Shop", "Clients/Acme"); empty for none.</summary>
    public string Folder { get; set; } = "";
    public string ProviderKey { get; set; } = "";
    public string Host { get; set; } = "localhost";
    public int? Port { get; set; }
    public string Database { get; set; } = "";
    public bool IntegratedSecurity { get; set; }

    /// <summary>Sign in with Microsoft Entra ID (Azure AD) instead of a database login; see <see cref="SignInMethods"/>.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<EntraAuthMode>))]
    public EntraAuthMode EntraAuth { get; set; }

    /// <summary>Entra directory (tenant) ID or domain; empty for the account's home tenant.</summary>
    public string? EntraTenantId { get; set; }

    /// <summary>
    /// PostgreSQL with a service principal: the app's client ID. The server knows the app by its role name (the User),
    /// so the two differ there; SQL Server takes the client ID as the user.
    /// </summary>
    public string? EntraClientId { get; set; }

    public string? UserName { get; set; }

    /// <summary>Whether connecting needs <see cref="Password"/>: a database login, Entra password or a client secret.
    /// A database file (SQLite) never does.</summary>
    [JsonIgnore]
    public bool NeedsPassword => !IsFile && !IntegratedSecurity && EntraAuth is EntraAuthMode.None or EntraAuthMode.Password or EntraAuthMode.ServicePrincipal;

    /// <summary>True when the password is needed but missing: the app asks for it on connect.</summary>
    [JsonIgnore]
    public bool MissingPassword => NeedsPassword && string.IsNullOrEmpty(Password);

    /// <summary>Never serialized in plain text; the connection store encrypts it separately.</summary>
    [JsonIgnore]
    public string? Password { get; set; }

    public bool SavePassword { get; set; }
    public bool Encrypt { get; set; }
    public bool TrustServerCertificate { get; set; } = true;

    /// <summary>SQL Server: ApplicationIntent=ReadOnly (routes to a readable secondary when available).</summary>
    public bool ReadOnlyIntent { get; set; } = true;

    /// <summary>
    /// Nothing that writes may run on this connection: scripts that change data or schema, result edits, table creation,
    /// copies and procedures are refused. PostgreSQL also opens its sessions read-only on the server.
    /// </summary>
    public bool ReadOnly { get; set; }

    /// <summary>Connect through an SSH server; <see cref="Host"/> and <see cref="Port"/> are then as that server sees them.</summary>
    public SshTunnelSettings Ssh { get => _ssh; set => _ssh = value ?? new(); }
    private SshTunnelSettings _ssh = new();

    /// <summary>
    /// "host:port" of the SSH server when the connection uses a tunnel, else null. Two tunnelled connections to
    /// "localhost" are only the same server when they go through the same SSH server.
    /// </summary>
    [JsonIgnore]
    public string? TunnelKey => Ssh.Enabled ? $"{Ssh.Host.Trim()}:{Ssh.Port}".ToLowerInvariant() : null;

    /// <summary>
    /// The name the server's TLS certificate is checked against when the driver dials another address for it (the local
    /// end of an SSH tunnel). Set for the connection only, never saved.
    /// </summary>
    [JsonIgnore]
    public string? CertificateHostName { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter<ConnectionEnvironment>))]
    public ConnectionEnvironment Environment { get; set; }

    [JsonIgnore]
    public bool IsProduction => Environment == ConnectionEnvironment.Production;

    /// <summary>A database file (SQLite): Host holds its path, and there is no server, sign-in or password.</summary>
    [JsonIgnore]
    public bool IsFile => string.Equals(ProviderKey, "Sqlite", StringComparison.OrdinalIgnoreCase);

    public ConnectionProfile Clone()
    {
        var copy = (ConnectionProfile)MemberwiseClone();
        copy._ssh = _ssh.Clone();
        return copy;
    }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"{Host}/{Database}" : Name;

    /// <summary>"Folder / Name", or the name alone outside folders.</summary>
    [JsonIgnore]
    public string QualifiedName => string.IsNullOrWhiteSpace(Folder) ? DisplayName : $"{Folder.Trim()} / {DisplayName}";

    public override string ToString() =>
        (Environment.ShortTag() is { } tag ? $"{QualifiedName}  [{tag}]" : QualifiedName) + (ReadOnly ? "  (read-only)" : "");
}
