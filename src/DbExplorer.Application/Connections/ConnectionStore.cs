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
            if (s.ProtectedPassword is not null && protector.IsSupported)
                s.Profile.Password = protector.Unprotect(s.ProtectedPassword);
        }

        return stored.Select(s => s.Profile).ToList();
    }

    public async Task SaveAsync(IEnumerable<ConnectionProfile> profiles, CancellationToken ct = default)
    {
        var stored = profiles.Select(p => new StoredProfile
        {
            Profile = p,
            ProtectedPassword = p.SavePassword && protector.IsSupported && !string.IsNullOrEmpty(p.Password)
                ? protector.Protect(p.Password)
                : null
        }).ToList();

        var tmp = paths.ConnectionsFile + ".tmp";
        await using (var fs = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(fs, stored, Json, ct);
        }
        File.Move(tmp, paths.ConnectionsFile, overwrite: true);
    }

    private sealed class StoredProfile
    {
        public ConnectionProfile Profile { get; set; } = new();
        public string? ProtectedPassword { get; set; }
    }
}
