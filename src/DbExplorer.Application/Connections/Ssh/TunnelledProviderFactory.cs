using DbExplorer.Core.Abstractions;
using DbExplorer.Core.Connections;

namespace DbExplorer.Application.Connections.Ssh;

/// <summary>
/// The engine's factory, with every connection it makes sent through the session's SSH tunnel. Callers keep using the
/// saved profile (its id, name and database); only the address the driver dials is swapped for the tunnel's local end.
/// </summary>
public sealed class TunnelledProviderFactory(IDatabaseProviderFactory inner, SshTunnel tunnel) : IDatabaseProviderFactory
{
    public SshTunnel Tunnel { get; } = tunnel;

    public string Key => inner.Key;
    public string DisplayName => inner.DisplayName;
    public int DefaultPort => inner.DefaultPort;
    public bool SupportsIntegratedSecurity => inner.SupportsIntegratedSecurity;
    public bool SupportsReadOnlyIntent => inner.SupportsReadOnlyIntent;

    public IDatabaseProvider Create(ConnectionProfile profile) => inner.Create(Tunnel.Local(profile));

    public async Task<IReadOnlyList<string>> ListDatabasesAsync(ConnectionProfile profile, CancellationToken ct = default)
    {
        try
        {
            return await inner.ListDatabasesAsync(Tunnel.Local(profile), ct);
        }
        catch (Exception ex) when (Tunnel.Explain(ex) is SshTunnelException explained)
        {
            throw explained;
        }
    }
}
