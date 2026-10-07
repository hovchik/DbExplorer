using System.Text;
using System.Text.Json;
using DbExplorer.Application.Connections;
using DbExplorer.Application.Query;
using DbExplorer.Core.Connections;

namespace DbExplorer.Application.Team;

public enum TeamItemStatus
{
    Seen,
    New,
    Updated
}

/// <summary>A shared file and whether the user has seen this version of it.</summary>
public sealed record TeamItem(TeamFile File, TeamItemStatus Status)
{
    public TeamItemKind Kind => File.Kind;
    public string Name => File.Name;
    public string RelativePath => File.RelativePath;
}

/// <param name="Removed">Files the user had seen that a teammate has since deleted.</param>
/// <param name="FolderMissing">The team folder is not there (a network share offline, a sync tool not running).</param>
public sealed record TeamSnapshot(IReadOnlyList<TeamItem> Items, IReadOnlyList<string> Removed, bool FolderMissing)
{
    public static TeamSnapshot Empty { get; } = new([], [], false);

    /// <summary>New, updated and removed items: what the Team button counts.</summary>
    public int ChangedCount => Items.Count(i => i.Status != TeamItemStatus.Seen) + Removed.Count;
}

/// <summary>The saved connections after taking shared ones, and what happened to each.</summary>
/// <param name="KeptBoth">Shared connections added next to a local one of the same name or with local edits.</param>
/// <param name="PasswordsLeftOut">The files had passwords but no (or a wrong) team password is set: they are asked on connect.</param>
/// <param name="Failed">Shared files that could not be read (locked by the sync tool, or not a valid export), with why;
/// they stay new and can be pulled again.</param>
public sealed record TeamPullResult(
    IReadOnlyList<ConnectionProfile> Profiles,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Updated,
    IReadOnlyList<string> KeptBoth,
    bool PasswordsLeftOut,
    IReadOnlyList<string> Failed);

/// <summary>
/// Shares connections, queries and snippets with teammates through a folder the team already syncs, with no server.
/// Connections use the <c>.dbxconnections</c> export format, so their passwords are left out or encrypted with a team
/// password (never plain text). Nothing is overwritten that a teammate changed since this app last saw it: both
/// versions are kept, and the user picks.
/// </summary>
public sealed class TeamSync
{
    private static readonly JsonSerializerOptions FingerprintJson = new() { WriteIndented = false };

    private readonly TeamSyncStore _store;
    private readonly TeamSyncState _state;
    private readonly object _gate = new();
    private string? _password;
    private IReadOnlyList<UserSnippet> _snippets = [];

    public TeamSync(TeamSyncStore store)
    {
        _store = store;
        _state = store.Load();
        _password = store.Unprotect(_state.ProtectedTeamPassword);
    }

    public string? FolderPath => _state.FolderPath;

    public bool IsConfigured => !string.IsNullOrEmpty(_state.FolderPath);

    public TeamFolder? Folder => IsConfigured ? new TeamFolder(_state.FolderPath!) : null;

    /// <summary>The name on "keep both" copies; the OS user name unless set.</summary>
    public string MemberName
    {
        get => string.IsNullOrWhiteSpace(_state.MemberName) ? Environment.UserName : _state.MemberName;
        set
        {
            _state.MemberName = value.Trim();
            Save();
        }
    }

    /// <summary>Encrypts and decrypts the passwords of shared connections; null when not set.</summary>
    public string? TeamPassword => _password;

    /// <summary>Whether the team password is kept (encrypted for this Windows user) across restarts.</summary>
    public bool RemembersPassword => _state.ProtectedTeamPassword is not null;

    public bool CanRememberPassword => _store.CanSavePassword;

    /// <summary>Snippets in the team folder as of the last <see cref="Scan"/>, sorted by name.</summary>
    public IReadOnlyList<UserSnippet> Snippets => _snippets;

    /// <summary>Starts syncing with another folder (null stops). What was seen in the old one no longer applies.</summary>
    public void SetFolder(string? path)
    {
        path = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path.Trim());
        lock (_gate)
        {
            if (string.Equals(path, _state.FolderPath, StringComparison.OrdinalIgnoreCase)) return;
            _state.FolderPath = path;
            _state.Seen.Clear();
            _state.Links.Clear();
            _snippets = [];
        }
        Save();
    }

    public void SetTeamPassword(string? password, bool remember)
    {
        _password = string.IsNullOrEmpty(password) ? null : password;
        _state.ProtectedTeamPassword = remember ? _store.Protect(_password) : null;
        Save();
    }

    /// <summary>Reads the team folder (safe on a background thread: it can be a slow network share).</summary>
    public TeamSnapshot Scan()
    {
        if (Folder is not { } folder) return TeamSnapshot.Empty;
        if (!folder.Exists) return new TeamSnapshot([], [], true);

        var files = folder.List();
        _snippets = files.Where(f => f.Kind == TeamItemKind.Snippet)
            .Select(f =>
            {
                try { return new UserSnippet { Name = f.Name, Sql = folder.ReadText(f.RelativePath) }; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
            })
            .OfType<UserSnippet>()
            .Where(s => !string.IsNullOrWhiteSpace(s.Sql))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        lock (_gate)
        {
            var items = files
                .Select(f => new TeamItem(f, !_state.Seen.TryGetValue(f.RelativePath, out var seen) ? TeamItemStatus.New
                    : seen == f.Hash ? TeamItemStatus.Seen : TeamItemStatus.Updated))
                .OrderBy(i => i.Kind).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var present = files.Select(f => f.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var removed = _state.Seen.Keys.Where(k => !present.Contains(k)).Order(StringComparer.OrdinalIgnoreCase).ToList();
            return new TeamSnapshot(items, removed, false);
        }
    }

    /// <summary>The user has looked at these versions: they stop counting as new.</summary>
    public void MarkSeen(IEnumerable<TeamItem> items, IEnumerable<string>? removed = null)
    {
        lock (_gate)
        {
            foreach (var item in items) _state.Seen[item.RelativePath] = item.File.Hash;
            foreach (var path in removed ?? []) _state.Seen.Remove(path);
        }
        Save();
    }

    public string ReadText(TeamItem item) => RequireFolder().ReadText(item.RelativePath);

    /// <summary>Shares a query or snippet; replaces the shared file only if it is still the version the user last saw.</summary>
    public TeamWriteResult ShareText(TeamItemKind kind, string name, string sql)
    {
        if (kind == TeamItemKind.Connection) throw new ArgumentException("Use ShareConnection for connections.", nameof(kind));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A shared item needs a name.", nameof(name));
        if (string.IsNullOrWhiteSpace(sql)) throw new ArgumentException("There is no SQL to share.", nameof(sql));

        var folder = RequireFolder();
        var target = TargetPath(kind, name);
        string? expected;
        lock (_gate) expected = _state.Seen.GetValueOrDefault(target);
        var result = folder.Write(kind, name, sql, expected, MemberName);
        MarkWritten(result.RelativePath, result.Hash);
        return result;
    }

    /// <summary>
    /// Writes one connection to <c>connections/Name.dbxconnections</c>. Passwords are included only when
    /// <paramref name="includePasswords"/> and a team password is set, encrypted with it.
    /// </summary>
    public TeamWriteResult ShareConnection(ConnectionProfile profile, bool includePasswords)
    {
        var folder = RequireFolder();
        var password = includePasswords ? _password : null;
        if (includePasswords && password is null)
            throw new InvalidOperationException("Set a team password first: shared passwords are always encrypted with it.");

        var target = TargetPath(TeamItemKind.Connection, profile.DisplayName);
        var fingerprint = Fingerprint(profile);
        TeamConnectionLink? link;
        lock (_gate) link = _state.Links.GetValueOrDefault(profile.Id);

        // Shared before and nothing changed on either side: writing again would only show as "updated" to everyone.
        if (link is not null && link.RelativePath == target && link.LocalFingerprint == fingerprint &&
            CurrentHash(folder, target) == link.SharedHash && HasPasswords(folder, target) == (password is not null))
            return new TeamWriteResult(target, link.SharedHash, false, true);

        var content = ConnectionTransfer.Export([profile], password);
        var expected = link?.RelativePath == target ? link.SharedHash : null;
        var result = folder.Write(TeamItemKind.Connection, profile.DisplayName, content, expected, MemberName,
            canReplace: existing => IsSameConnection(existing, profile.Id),
            copyContent: suffix =>
            {
                // Kept next to a teammate's version: a connection of its own, so pulling one never replaces the other.
                var copy = profile.Clone();
                copy.Id = Guid.NewGuid();
                copy.Name = profile.DisplayName + suffix;
                return ConnectionTransfer.Export([copy], password);
            });

        if (!result.Conflict)
        {
            // Renamed here: the file under the old name goes, unless a teammate changed it meanwhile.
            if (link is not null && !string.Equals(link.RelativePath, result.RelativePath, StringComparison.OrdinalIgnoreCase))
            {
                if (folder.Delete(link.RelativePath, link.SharedHash))
                    lock (_gate) _state.Seen.Remove(link.RelativePath);
            }
            lock (_gate) _state.Links[profile.Id] = new TeamConnectionLink { RelativePath = result.RelativePath, SharedHash = result.Hash, LocalFingerprint = fingerprint };
        }
        MarkWritten(result.RelativePath, result.Hash);
        return result;
    }

    /// <summary>
    /// Adds or updates saved connections from shared files. A connection already taken from the team is updated in
    /// place unless it was edited here since; then both are kept (the local one becomes a connection of its own).
    /// A new connection whose name is taken is added as "Name (2)". Local passwords stay when the file has none
    /// and the connection still goes to the same server. A file that cannot be read is skipped, reported in
    /// <see cref="TeamPullResult.Failed"/> and stays unseen.
    /// </summary>
    public TeamPullResult PullConnections(IEnumerable<TeamItem> items, IReadOnlyList<ConnectionProfile> local)
    {
        var folder = RequireFolder();
        var merged = local.ToList();
        List<string> added = [], updated = [], keptBoth = [], failed = [];
        var passwordsLeftOut = false;

        foreach (var item in items.Where(i => i.Kind == TeamItemKind.Connection))
        {
            // Read and decrypt first: a file that fails is skipped (and stays unseen) without touching anything.
            string hash;
            IReadOnlyList<ConnectionProfile> incoming;
            var leftOut = false;
            try
            {
                var bytes = File.ReadAllBytes(folder.FullPath(item.RelativePath));
                hash = TeamFolder.HashOf(bytes);
                var file = ConnectionTransfer.Read(Encoding.UTF8.GetString(bytes));
                if (file.HasPasswords && _password is not null)
                {
                    try { incoming = ConnectionTransfer.Profiles(file, _password); }
                    catch (WrongExportPasswordException)
                    {
                        incoming = ConnectionTransfer.Profiles(file);
                        leftOut = true;
                    }
                }
                else
                {
                    incoming = ConnectionTransfer.Profiles(file);
                    leftOut = file.HasPasswords;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                failed.Add($"{item.Name} ({ex.Message})");
                continue;
            }
            passwordsLeftOut |= leftOut;

            foreach (var shared in incoming)
            {
                var result = Take(merged, shared, added, updated, keptBoth);
                lock (_gate) _state.Links[result.Id] = new TeamConnectionLink { RelativePath = item.RelativePath, SharedHash = hash, LocalFingerprint = Fingerprint(result) };
            }
            lock (_gate) _state.Seen[item.RelativePath] = hash;
        }

        Save();
        return new TeamPullResult(merged, added, updated, keptBoth, passwordsLeftOut, failed);
    }

    private ConnectionProfile Take(List<ConnectionProfile> merged, ConnectionProfile shared,
        List<string> added, List<string> updated, List<string> keptBoth)
    {
        var index = merged.FindIndex(p => p.Id == shared.Id);
        if (index >= 0)
        {
            var current = merged[index];
            var sharedFingerprint = Fingerprint(shared);
            var currentFingerprint = Fingerprint(current);
            TeamConnectionLink? link;
            lock (_gate) link = _state.Links.GetValueOrDefault(current.Id);
            var editedHere = link is null || link.LocalFingerprint != currentFingerprint;

            if (sharedFingerprint == currentFingerprint || !editedHere)
            {
                var result = WithLocalSecrets(shared, current);
                // Another saved connection took the shared name meanwhile: keep the local name.
                if (merged.Any(p => p.Id != current.Id && SameName(p, result))) result.Name = current.Name;
                merged[index] = result;
                if (sharedFingerprint != currentFingerprint) updated.Add(result.DisplayName);
                return result;
            }

            // Edited on both sides: the local version becomes a connection of its own, the team's takes the link.
            var fork = current.Clone();
            fork.Id = Guid.NewGuid();
            merged[index] = fork;
            lock (_gate) _state.Links.Remove(current.Id);
            var team = WithLocalSecrets(shared, current);
            team.Name = UniqueName(merged, shared.DisplayName + " (team)");
            merged.Add(team);
            keptBoth.Add(team.DisplayName);
            return team;
        }

        var copy = shared.Clone();
        if (merged.Any(p => SameName(p, copy)))
        {
            copy.Name = UniqueName(merged, copy.DisplayName);
            keptBoth.Add(copy.DisplayName);
        }
        else added.Add(copy.DisplayName);
        merged.Add(copy);
        return copy;
    }

    /// <summary>Deletes a shared file; false when a teammate changed it after the user saw it (it is kept).</summary>
    public bool Remove(TeamItem item)
    {
        if (!RequireFolder().Delete(item.RelativePath, item.File.Hash)) return false;
        lock (_gate)
        {
            _state.Seen.Remove(item.RelativePath);
            foreach (var id in _state.Links.Where(l => string.Equals(l.Value.RelativePath, item.RelativePath, StringComparison.OrdinalIgnoreCase)).Select(l => l.Key).ToList())
                _state.Links.Remove(id);
        }
        Save();
        return true;
    }

    /// <summary>The shared file a saved connection is linked to, if any.</summary>
    public string? LinkedPath(Guid connectionId)
    {
        lock (_gate) return _state.Links.GetValueOrDefault(connectionId)?.RelativePath;
    }

    /// <summary>
    /// What makes two versions of a connection different for sharing. Secrets, "save password" and the SSH key path
    /// are left out: they are this machine's, so changing them is not an edit that conflicts with the team's version.
    /// </summary>
    public static string Fingerprint(ConnectionProfile profile)
    {
        var copy = profile.Clone();
        copy.Id = Guid.Empty;
        copy.Password = null;
        copy.SavePassword = false;
        copy.Ssh.Password = null;
        copy.Ssh.Passphrase = null;
        copy.Ssh.PrivateKeyPath = "";
        return JsonSerializer.Serialize(copy, FingerprintJson);
    }

    /// <summary>
    /// The shared version, keeping this machine's passwords and key file where the file has none. Saved secrets are
    /// kept only while the connection still goes to the same server as the same user (and through the same SSH
    /// server): otherwise anyone who can write to the team folder could point it at their own server and collect
    /// them. Dropped secrets are asked for on connect.
    /// </summary>
    private static ConnectionProfile WithLocalSecrets(ConnectionProfile shared, ConnectionProfile current)
    {
        var result = shared.Clone();
        result.Id = current.Id;
        if (SameEndpoint(shared, current))
        {
            if (string.IsNullOrEmpty(result.Password)) result.Password = current.Password;
            if (string.IsNullOrEmpty(result.Ssh.Password)) result.Ssh.Password = current.Ssh.Password;
            if (string.IsNullOrEmpty(result.Ssh.Passphrase)) result.Ssh.Passphrase = current.Ssh.Passphrase;
        }
        result.SavePassword = shared.SavePassword || current.SavePassword;
        if (!string.IsNullOrEmpty(current.Ssh.PrivateKeyPath) &&
            (string.IsNullOrEmpty(result.Ssh.PrivateKeyPath) || !File.Exists(result.Ssh.PrivateKeyPath)))
            result.Ssh.PrivateKeyPath = current.Ssh.PrivateKeyPath;
        return result;
    }

    /// <summary>Same provider, server, port and user, and the same SSH tunnel (if any).</summary>
    private static bool SameEndpoint(ConnectionProfile a, ConnectionProfile b) =>
        string.Equals(a.ProviderKey, b.ProviderKey, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Host.Trim(), b.Host.Trim(), StringComparison.OrdinalIgnoreCase) &&
        a.Port == b.Port &&
        a.IntegratedSecurity == b.IntegratedSecurity &&
        string.Equals(a.UserName ?? "", b.UserName ?? "", StringComparison.Ordinal) &&
        a.Ssh.Enabled == b.Ssh.Enabled &&
        (!a.Ssh.Enabled ||
         (string.Equals(a.Ssh.Host.Trim(), b.Ssh.Host.Trim(), StringComparison.OrdinalIgnoreCase) &&
          a.Ssh.Port == b.Ssh.Port &&
          string.Equals(a.Ssh.UserName, b.Ssh.UserName, StringComparison.Ordinal)));

    private static bool SameName(ConnectionProfile a, ConnectionProfile b) =>
        string.Equals(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);

    private static string UniqueName(List<ConnectionProfile> profiles, string name)
    {
        if (!profiles.Any(p => string.Equals(p.DisplayName, name, StringComparison.OrdinalIgnoreCase))) return name;
        for (var n = 2; ; n++)
        {
            var candidate = $"{name} ({n})";
            if (!profiles.Any(p => string.Equals(p.DisplayName, candidate, StringComparison.OrdinalIgnoreCase))) return candidate;
        }
    }

    private static bool IsSameConnection(string json, Guid id)
    {
        try
        {
            var file = ConnectionTransfer.Read(json);
            return ConnectionTransfer.Profiles(file) is [var only] && only.Id == id;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static string? CurrentHash(TeamFolder folder, string relative)
    {
        var path = folder.FullPath(relative);
        return File.Exists(path) ? TeamFolder.HashOf(File.ReadAllBytes(path)) : null;
    }

    private static bool HasPasswords(TeamFolder folder, string relative)
    {
        try { return ConnectionTransfer.Read(folder.ReadText(relative)).HasPasswords; }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { return false; }
    }

    private static string TargetPath(TeamItemKind kind, string name)
    {
        var (directory, extension) = TeamFolder.Location(kind);
        return $"{directory}/{TeamFolder.SafeRelativeName(name, allowFolders: kind == TeamItemKind.Query)}{extension}";
    }

    private void MarkWritten(string relative, string hash)
    {
        lock (_gate) _state.Seen[relative] = hash;
        Save();
    }

    private TeamFolder RequireFolder() =>
        Folder ?? throw new InvalidOperationException("Pick a team folder first.");

    private void Save()
    {
        lock (_gate) _store.Save(_state);
    }
}
