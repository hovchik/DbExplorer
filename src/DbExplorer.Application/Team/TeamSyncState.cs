using System.Text.Json;
using DbExplorer.Application.Connections;

namespace DbExplorer.Application.Team;

/// <summary>A saved connection that came from, or was shared to, a file in the team folder.</summary>
public sealed class TeamConnectionLink
{
    public string RelativePath { get; set; } = "";

    /// <summary>The shared file as this app last wrote or read it: a different hash means a teammate changed it.</summary>
    public string SharedHash { get; set; } = "";

    /// <summary>The local connection at that moment (<see cref="TeamSync.Fingerprint"/>): a different one means it was edited here.</summary>
    public string LocalFingerprint { get; set; } = "";
}

/// <summary>This machine's side of team sharing, in <c>team-sync.json</c>. Nothing here is written to the team folder.</summary>
public sealed class TeamSyncState
{
    public string? FolderPath { get; set; }

    /// <summary>Name used for the "keep both" copy when a teammate changed the same file ("Orders (alice).sql").</summary>
    public string MemberName { get; set; } = "";

    /// <summary>The team password, encrypted for this Windows user (DPAPI); never saved where that is unavailable.</summary>
    public string? ProtectedTeamPassword { get; set; }

    /// <summary>Hash of each shared file as the user last saw it; anything else is shown as new or updated.</summary>
    public Dictionary<string, string> Seen { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Saved connection id → its shared file.</summary>
    public Dictionary<Guid, TeamConnectionLink> Links { get; set; } = [];
}

public sealed class TeamSyncStore(AppPaths paths, ISecretProtector protector)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private string FilePath => Path.Combine(paths.Root, "team-sync.json");

    public bool CanSavePassword => protector.IsSupported;

    public TeamSyncState Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new TeamSyncState();
            var state = JsonSerializer.Deserialize<TeamSyncState>(File.ReadAllText(FilePath), Json) ?? new TeamSyncState();
            // Deserialization loses the case-insensitive comparer.
            state.Seen = new Dictionary<string, string>(state.Seen ?? [], StringComparer.OrdinalIgnoreCase);
            state.Links ??= [];
            return state;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // Losing this only means everything in the team folder shows as new once.
            return new TeamSyncState();
        }
    }

    public void Save(TeamSyncState state)
    {
        Directory.CreateDirectory(paths.Root);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public string? Protect(string? password) =>
        protector.IsSupported && !string.IsNullOrEmpty(password) ? protector.Protect(password) : null;

    public string? Unprotect(string? protectedText) =>
        protector.IsSupported && protectedText is not null ? protector.Unprotect(protectedText) : null;
}
