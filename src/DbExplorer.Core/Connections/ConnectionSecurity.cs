using System.Net;
using System.Security.Authentication;

namespace DbExplorer.Core.Connections;

/// <summary>How well a connection is protected in transit.</summary>
public enum TlsProtection
{
    /// <summary>Encrypt is off: the drivers encrypt when the server offers it, but anyone in between can stop that.</summary>
    Opportunistic,

    /// <summary>Encrypted, but any certificate is accepted, so a man in the middle can still read everything.</summary>
    EncryptedUnverified,

    /// <summary>Encrypted and the server's certificate is checked.</summary>
    Verified
}

/// <summary>
/// TLS defaults and the explanations that go with them. New connections to another machine encrypt and verify the
/// certificate; a server on this machine keeps the old relaxed settings, because local traffic never leaves the
/// computer and local development servers rarely have a trusted certificate. Saved connections keep the values they
/// were saved with: <see cref="ConnectionProfile"/>'s own property defaults are unchanged.
/// </summary>
public static class ConnectionSecurity
{
    /// <summary>
    /// True for addresses that stay on this computer: localhost, loopback IPs, SQL Server's "." / "(local)" /
    /// "(localdb)", with or without a "\instance" suffix, and this machine's own name.
    /// </summary>
    public static bool IsLocalHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return true;
        var h = host.Trim();

        // SQL Server: "server\instance" and "tcp:server,port" name the same machine as "server".
        if (h.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)) h = h[4..];
        var slash = h.IndexOf('\\');
        if (slash >= 0) h = h[..slash];
        var comma = h.IndexOf(',');
        if (comma >= 0) h = h[..comma];
        h = h.Trim().TrimEnd('.');
        if (h.Length == 0) return true; // "." or ".\SQLEXPRESS"

        if (h.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || h.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || h.Equals("(local)", StringComparison.OrdinalIgnoreCase)
            || h.Equals("(localdb)", StringComparison.OrdinalIgnoreCase)
            || h.Equals(System.Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            return true;

        var ip = h.StartsWith('[') && h.EndsWith(']') ? h[1..^1] : h;
        return IPAddress.TryParse(ip, out var address) && IPAddress.IsLoopback(address);
    }

    /// <summary>
    /// The TLS settings a new connection to <paramref name="host"/> starts with: encrypted and verified for another
    /// machine, relaxed for this one.
    /// </summary>
    public static (bool Encrypt, bool TrustServerCertificate) DefaultsFor(string? host) =>
        IsLocalHost(host) ? (false, true) : (true, false);

    public static void ApplyDefaults(ConnectionProfile profile) =>
        (profile.Encrypt, profile.TrustServerCertificate) = DefaultsFor(profile.Host);

    public static TlsProtection Protection(bool encrypt, bool trustServerCertificate) =>
        !encrypt ? TlsProtection.Opportunistic
        : trustServerCertificate ? TlsProtection.EncryptedUnverified
        : TlsProtection.Verified;

    public static TlsProtection Protection(ConnectionProfile p) => Protection(p.Encrypt, p.TrustServerCertificate);

    /// <summary>One line on what the two check boxes mean together, shown under them in the connection dialog.</summary>
    public static string Describe(bool encrypt, bool trustServerCertificate, string? host) =>
        Protection(encrypt, trustServerCertificate) switch
        {
            TlsProtection.Verified => "Encrypted, and the server's certificate is checked.",
            TlsProtection.EncryptedUnverified =>
                "Encrypted, but any certificate is accepted: someone between you and the server could still read the traffic.",
            _ when IsLocalHost(host) => "Encrypted only if the server offers it. Fine for a server on this computer.",
            _ => "Encrypted only if the server offers it: someone on the network could read passwords and data."
        };

    /// <summary>A warning for a Production connection that does not check the server certificate; null otherwise.</summary>
    public static string? Warning(bool encrypt, bool trustServerCertificate, ConnectionEnvironment environment) =>
        environment == ConnectionEnvironment.Production && Protection(encrypt, trustServerCertificate) != TlsProtection.Verified
            ? "This is a Production connection, but its server certificate is not checked. Tick Encrypt (TLS) and untick Trust certificate unless you are sure of the network."
            : null;

    public static string? Warning(ConnectionProfile p) => Warning(p.Encrypt, p.TrustServerCertificate, p.Environment);

    /// <summary>
    /// MySQL 8's default sign-in (caching_sha2_password) asks for the server's RSA key on an unencrypted connection.
    /// Anyone in between can hand over their own key and read the password, so the key is only fetched when the
    /// server is on this computer (or, through an SSH tunnel, on the SSH server itself).
    /// </summary>
    public static bool AllowMySqlPublicKeyRetrieval(ConnectionProfile p) =>
        !p.Encrypt && IsLocalHost(p.CertificateHostName ?? p.Host);

    /// <summary>
    /// The driver's error with one line on what to change when it is a TLS problem (certificate not trusted, server
    /// without TLS, MySQL RSA key refused); null for any other error.
    /// </summary>
    public static string? ExplainFailure(Exception error)
    {
        var messages = new List<string>();
        var certificate = false;
        for (var e = error; e is not null; e = e.InnerException)
        {
            messages.Add(e.Message);
            if (e is AuthenticationException) certificate = true;
        }
        var text = string.Join(" ", messages);

        bool Has(string s) => text.Contains(s, StringComparison.OrdinalIgnoreCase);

        if (Has("RSA public key is not enabled") || Has("AllowPublicKeyRetrieval"))
            return "This MySQL sign-in needs encryption when the server is not on this computer. Tick Encrypt (TLS) for the connection (add Trust certificate if the server's certificate is self-signed), or connect through an SSH tunnel.";

        if (Has("does not support SSL") || Has("doesn't support SSL") || Has("does not support TLS")
            || Has("No SSL enabled connection") || Has("SSL connection is required but the server"))
            return "The server does not accept encrypted connections. Turn on TLS on the server, or untick Encrypt (TLS) for this connection if the network is trusted.";

        if (certificate || Has("certificate") || Has("RemoteCertificate") || Has("SSL Authentication")
            || Has("SSL authentication") || Has("authority that is not trusted") || Has("UntrustedRoot"))
            return "The server's TLS certificate could not be verified (self-signed, expired, or issued for another name). If you trust this server, tick Trust certificate in the connection settings.";

        return null;
    }

    /// <summary>The error message, followed by the TLS hint when there is one.</summary>
    public static string Describe(Exception error) =>
        ExplainFailure(error) is { } hint ? $"{error.Message} {hint}" : error.Message;
}
