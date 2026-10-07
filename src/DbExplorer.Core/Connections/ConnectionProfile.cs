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
    public string? UserName { get; set; }

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

    [JsonConverter(typeof(JsonStringEnumConverter<ConnectionEnvironment>))]
    public ConnectionEnvironment Environment { get; set; }

    [JsonIgnore]
    public bool IsProduction => Environment == ConnectionEnvironment.Production;

    public ConnectionProfile Clone() => (ConnectionProfile)MemberwiseClone();

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"{Host}/{Database}" : Name;

    /// <summary>"Folder / Name", or the name alone outside folders.</summary>
    [JsonIgnore]
    public string QualifiedName => string.IsNullOrWhiteSpace(Folder) ? DisplayName : $"{Folder.Trim()} / {DisplayName}";

    public override string ToString() =>
        (Environment.ShortTag() is { } tag ? $"{QualifiedName}  [{tag}]" : QualifiedName) + (ReadOnly ? "  (read-only)" : "");
}
