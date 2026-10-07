using System.Text.Json;
using DbExplorer.Core.Connections;

namespace DbExplorer.Application.Connections;

/// <summary>Saved connections in a JSON file; passwords are encrypted separately (or not saved).</summary>
public sealed class ConnectionStore(AppPaths paths, ISecretProtector protector)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public bool CanSavePasswords => protector.IsSupported;

    public async Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(paths.ConnectionsFile)) return [];

        List<StoredProfile> stored;
        try
        {
            await using var fs = File.OpenRead(paths.ConnectionsFile);
            stored = await JsonSerializer.DeserializeAsync<List<StoredProfile>>(fs, Json, ct) ?? [];
        }
        catch (JsonException ex)
        {
            // Keep the damaged file: the next save would otherwise overwrite every saved connection with an empty list.
            var backup = $"{paths.ConnectionsFile}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(paths.ConnectionsFile, backup, overwrite: true);
            throw new InvalidDataException($"The saved connections file was damaged and has been moved to {backup}.", ex);
        }

        foreach (var s in stored)
        {
            if (!protector.IsSupported) continue;
            if (s.ProtectedPassword is not null) s.Profile.Password = protector.Unprotect(s.ProtectedPassword);
            if (s.ProtectedSshPassword is not null) s.Profile.Ssh.Password = protector.Unprotect(s.ProtectedSshPassword);
            if (s.ProtectedSshPassphrase is not null) s.Profile.Ssh.Passphrase = protector.Unprotect(s.ProtectedSshPassphrase);
        }

        return stored.Select(s => s.Profile).ToList();
    }

    public async Task SaveAsync(IEnumerable<ConnectionProfile> profiles, CancellationToken ct = default)
    {
        var stored = profiles.Select(p => new StoredProfile
        {
            Profile = p,
            ProtectedPassword = Protect(p, p.Password),
            ProtectedSshPassword = p.Ssh.Enabled ? Protect(p, p.Ssh.Password) : null,
            ProtectedSshPassphrase = p.Ssh.Enabled ? Protect(p, p.Ssh.Passphrase) : null
        }).ToList();

        var tmp = paths.ConnectionsFile + ".tmp";
        await using (var fs = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(fs, stored, Json, ct);
        }
        File.Move(tmp, paths.ConnectionsFile, overwrite: true);
    }

    /// <summary>"Save password" covers the SSH password and key passphrase too.</summary>
    private string? Protect(ConnectionProfile profile, string? secret) =>
        profile.SavePassword && protector.IsSupported && !string.IsNullOrEmpty(secret) ? protector.Protect(secret) : null;

    private sealed class StoredProfile
    {
        public ConnectionProfile Profile { get; set; } = new();
        public string? ProtectedPassword { get; set; }
        public string? ProtectedSshPassword { get; set; }
        public string? ProtectedSshPassphrase { get; set; }
    }
}
