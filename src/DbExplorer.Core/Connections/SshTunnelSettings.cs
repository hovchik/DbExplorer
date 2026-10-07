using System.Text.Json.Serialization;

namespace DbExplorer.Core.Connections;

public enum SshAuthMethod
{
    Password,
    PrivateKey
}

/// <summary>
/// Reaching the database through an SSH server: the app forwards a local port to the profile's Host and Port as the
/// SSH server sees them (often localhost) and connects the driver to that local port.
/// </summary>
public sealed class SshTunnelSettings
{
    public const int DefaultPort = 22;

    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = DefaultPort;
    public string UserName { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter<SshAuthMethod>))]
    public SshAuthMethod AuthMethod { get; set; }

    /// <summary>Never serialized in plain text; the connection store encrypts it like the database password.</summary>
    [JsonIgnore]
    public string? Password { get; set; }

    /// <summary>Private key file on this machine (OpenSSH, PEM or PuTTY .ppk).</summary>
    public string PrivateKeyPath { get; set; } = "";

    /// <summary>The key file's passphrase, if it has one; stored like <see cref="Password"/>.</summary>
    [JsonIgnore]
    public string? Passphrase { get; set; }

    /// <summary>The password, or the passphrase for key sign-in: the one secret this sign-in uses.</summary>
    [JsonIgnore]
    public string? Secret => AuthMethod == SshAuthMethod.Password ? Password : Passphrase;

    /// <summary>True when sign-in cannot work without asking for something first (a password was not saved).</summary>
    [JsonIgnore]
    public bool NeedsPassword => Enabled && AuthMethod == SshAuthMethod.Password && string.IsNullOrEmpty(Password);

    public SshTunnelSettings Clone() => (SshTunnelSettings)MemberwiseClone();

    public override string ToString() => $"{UserName}@{Host}" + (Port == DefaultPort ? "" : $":{Port}");
}
