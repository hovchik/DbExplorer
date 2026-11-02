using DbExplorer.Core.Connections;

namespace DbExplorer.Application.Connections;

/// <summary>
/// Rules for changing saved connections while one of them is open: what a duplicate is called, which edits the open
/// session can take at once and which only apply after reconnecting.
/// </summary>
public static class ConnectionEdits
{
    /// <summary>
    /// A copy of <paramref name="source"/> with a new id and a free name ("Shop (copy)", "Shop (copy 2)"…), passwords
    /// included so the copy connects the same way.
    /// </summary>
    public static ConnectionProfile Duplicate(ConnectionProfile source, IEnumerable<ConnectionProfile> existing)
    {
        var copy = source.Clone();
        copy.Id = Guid.NewGuid();
        var taken = existing.Select(p => p.DisplayName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = $"{source.DisplayName} (copy)";
        for (var n = 2; taken.Contains(name); n++) name = $"{source.DisplayName} (copy {n})";
        copy.Name = name;
        return copy;
    }

    /// <summary>
    /// True when <paramref name="edited"/> reaches the server differently from <paramref name="current"/> (server,
    /// sign-in, TLS, SSH, read-only): the open session keeps the old settings until it reconnects. Name, folder,
    /// environment and a changed or no-longer-saved password do not need a reconnect.
    /// </summary>
    public static bool NeedsReconnect(ConnectionProfile current, ConnectionProfile edited) =>
        !string.Equals(current.ProviderKey, edited.ProviderKey, StringComparison.OrdinalIgnoreCase)
        || !string.Equals(current.Host.Trim(), edited.Host.Trim(), StringComparison.OrdinalIgnoreCase)
        || current.Port != edited.Port
        || !string.Equals(current.Database, edited.Database, StringComparison.Ordinal)
        || current.IntegratedSecurity != edited.IntegratedSecurity
        || !string.Equals(current.UserName ?? "", edited.UserName ?? "", StringComparison.Ordinal)
        || current.Encrypt != edited.Encrypt
        || current.TrustServerCertificate != edited.TrustServerCertificate
        || current.ReadOnlyIntent != edited.ReadOnlyIntent
        || current.ReadOnly != edited.ReadOnly
        || SshDiffers(current.Ssh, edited.Ssh);

    private static bool SshDiffers(SshTunnelSettings a, SshTunnelSettings b)
    {
        if (a.Enabled != b.Enabled) return true;
        if (!a.Enabled) return false;
        return !string.Equals(a.Host.Trim(), b.Host.Trim(), StringComparison.OrdinalIgnoreCase)
               || a.Port != b.Port
               || !string.Equals(a.UserName, b.UserName, StringComparison.Ordinal)
               || a.AuthMethod != b.AuthMethod
               || !string.Equals(a.PrivateKeyPath, b.PrivateKeyPath, StringComparison.Ordinal);
    }

    /// <summary>
    /// Copies the settings the app itself enforces (name, folder, environment, read-only) onto the open session's
    /// profile, so the banner, title and the confirmations for production and read-only connections follow the edit
    /// at once instead of after the next connect. Returns true when anything changed.
    /// </summary>
    public static bool ApplyToOpenSession(ConnectionProfile sessionProfile, ConnectionProfile edited)
    {
        var changed = sessionProfile.Name != edited.Name
                      || sessionProfile.Folder != edited.Folder
                      || sessionProfile.Environment != edited.Environment
                      || sessionProfile.ReadOnly != edited.ReadOnly;
        sessionProfile.Name = edited.Name;
        sessionProfile.Folder = edited.Folder;
        sessionProfile.Environment = edited.Environment;
        sessionProfile.ReadOnly = edited.ReadOnly;
        return changed;
    }
}
