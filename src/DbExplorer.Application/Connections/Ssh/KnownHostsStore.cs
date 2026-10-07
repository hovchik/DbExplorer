using System.Text.Json;

namespace DbExplorer.Application.Connections.Ssh;

/// <summary>An SSH server's host key as received, and the one remembered for that server before (if any).</summary>
public sealed record SshHostKey(string Host, int Port, string KeyType, string Fingerprint, string? KnownFingerprint)
{
    /// <summary>The server was trusted before with a different key: a reinstall, or someone in the middle.</summary>
    public bool Changed => KnownFingerprint is not null && KnownFingerprint != Fingerprint;

    public string Server => Port == 22 ? Host : $"{Host}:{Port}";
}

/// <summary>Host keys the user accepted, per SSH server, like OpenSSH's known_hosts (fingerprints only).</summary>
public sealed class KnownHostsStore(AppPaths paths)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _gate = new();

    /// <summary>The remembered "SHA256:…" fingerprint of the server, or null when it was never accepted.</summary>
    public string? Find(string host, int port)
    {
        lock (_gate) return Load().TryGetValue(Key(host, port), out var entry) ? entry.Fingerprint : null;
    }

    public void Trust(SshHostKey key)
    {
        lock (_gate)
        {
            var all = Load();
            all[Key(key.Host, key.Port)] = new Entry { KeyType = key.KeyType, Fingerprint = key.Fingerprint, AcceptedAt = DateTimeOffset.Now };
            var tmp = paths.KnownHostsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all, Json));
            File.Move(tmp, paths.KnownHostsFile, overwrite: true);
        }
    }

    private static string Key(string host, int port) => $"{host.Trim().ToLowerInvariant()}:{port}";

    private Dictionary<string, Entry> Load()
    {
        if (!File.Exists(paths.KnownHostsFile)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            var all = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(paths.KnownHostsFile), Json);
            return new(all ?? [], StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            // A damaged file trusts nothing: every server is asked about again.
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private sealed class Entry
    {
        public string KeyType { get; set; } = "";
        public string Fingerprint { get; set; } = "";
        public DateTimeOffset AcceptedAt { get; set; }
    }
}

/// <summary>Asks the user whether to trust an SSH server's host key that is new or has changed.</summary>
public interface ISshHostKeyPrompt
{
    Task<bool> TrustAsync(SshHostKey key, CancellationToken ct);
}

/// <summary>No one to ask (tests, background work): unknown keys are refused.</summary>
public sealed class RejectUnknownHostKeys : ISshHostKeyPrompt
{
    public Task<bool> TrustAsync(SshHostKey key, CancellationToken ct) => Task.FromResult(false);
}
