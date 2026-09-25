using System.Text.Json.Serialization;

namespace DbExplorer.Core.Connections;

public sealed class ConnectionProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
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

    public ConnectionProfile Clone() => (ConnectionProfile)MemberwiseClone();

    public override string ToString() =>
        string.IsNullOrWhiteSpace(Name) ? $"{Host}/{Database}" : Name;
}
