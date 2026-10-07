using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DbExplorer.Core.Connections;

namespace DbExplorer.Application.Connections;

/// <summary>What to do with an imported connection whose name is already used.</summary>
public enum ImportConflictChoice
{
    Skip,
    Overwrite,
    KeepBoth
}

/// <summary>Thrown when the export password does not decrypt the file's passwords.</summary>
public sealed class WrongExportPasswordException() : Exception("The password is not the one the connections were exported with.");

/// <summary>A connections file as read, before its passwords (if any) are decrypted.</summary>
public sealed class ConnectionExportFile
{
    internal ConnectionExportFile(ConnectionTransfer.ExportDocument document) => Document = document;

    internal ConnectionTransfer.ExportDocument Document { get; }

    /// <summary>True when the file was exported with passwords and needs the export password to read them.</summary>
    public bool HasPasswords => Document.Passwords is not null;

    public int Count => Document.Connections.Count;
}

/// <summary>
/// Writes and reads a portable connections file. Passwords are never written in plain text: they are left out,
/// or encrypted with AES-256-GCM under a key derived from an export password (PBKDF2-SHA256), so the file can be
/// opened on another machine or Windows account, unlike the DPAPI-protected connections.json.
/// </summary>
public static class ConnectionTransfer
{
    public const string FormatName = "DbExplorer.Connections";
    /// <summary>2 added SSH tunnels: an older app would silently connect without the tunnel, so it refuses the file.</summary>
    public const int FormatVersion = 2;
    public const string FileExtension = "dbxconnections";

    private const int Iterations = 310_000;
    private const int MinIterations = 1_000;
    private const int MaxIterations = 10_000_000;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const string CheckText = "DbExplorer connections export";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <param name="exportPassword">Null or empty exports without passwords.</param>
    public static string Export(IEnumerable<ConnectionProfile> profiles, string? exportPassword, DateTimeOffset? exportedAt = null)
    {
        var withPasswords = !string.IsNullOrEmpty(exportPassword);
        byte[]? key = null;
        var document = new ExportDocument { ExportedAt = exportedAt ?? DateTimeOffset.Now };

        if (withPasswords)
        {
            var salt = RandomNumberGenerator.GetBytes(SaltSize);
            key = DeriveKey(exportPassword!, salt, Iterations);
            document.Passwords = new PasswordProtection
            {
                Iterations = Iterations,
                Salt = Convert.ToBase64String(salt),
                Check = Encrypt(key, CheckText)
            };
        }

        foreach (var profile in profiles)
        {
            var copy = profile.Clone();
            copy.Password = null;
            copy.Ssh.Password = null;
            copy.Ssh.Passphrase = null;
            var ssh = profile.Ssh.Enabled;
            document.Connections.Add(new ExportedConnection
            {
                Profile = copy,
                Password = EncryptOrNull(key, profile.Password),
                SshPassword = ssh ? EncryptOrNull(key, profile.Ssh.Password) : null,
                SshPassphrase = ssh ? EncryptOrNull(key, profile.Ssh.Passphrase) : null
            });
        }

        return JsonSerializer.Serialize(document, Json);
    }

    /// <exception cref="InvalidDataException">Not a connections export file.</exception>
    public static ConnectionExportFile Read(string json)
    {
        ExportDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<ExportDocument>(json, Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("This is not a DB Explorer connections file.", ex);
        }

        if (document is null || document.Format != FormatName)
            throw new InvalidDataException("This is not a DB Explorer connections file.");
        if (document.Version > FormatVersion)
            throw new InvalidDataException("This connections file was made by a newer version of DB Explorer.");
        if (document.Passwords is { } p && (p.Kdf != "PBKDF2-SHA256" || p.Cipher != "AES-256-GCM"))
            throw new InvalidDataException("The file's passwords are encrypted in a way this version of DB Explorer does not know.");
        if (document.Passwords is { } q && (q.Iterations <= 0 || string.IsNullOrEmpty(q.Salt) || string.IsNullOrEmpty(q.Check)))
            throw new InvalidDataException("The file's password section is damaged.");
        // The file chooses the work factor: an absurd one would hang the import (or be too weak to trust).
        if (document.Passwords is { } r && r.Iterations is < MinIterations or > MaxIterations)
            throw new InvalidDataException(
                $"The file's passwords use {r.Iterations:N0} key iterations; DB Explorer accepts {MinIterations:N0} to {MaxIterations:N0}.");

        return new ConnectionExportFile(document);
    }

    /// <summary>
    /// The file's connections. With <paramref name="exportPassword"/> their passwords are decrypted; without it
    /// (or for a file exported without passwords) they come back with no password and are asked for on connect.
    /// </summary>
    /// <exception cref="WrongExportPasswordException">The password does not match the export.</exception>
    public static IReadOnlyList<ConnectionProfile> Profiles(ConnectionExportFile file, string? exportPassword = null)
    {
        byte[]? key = null;
        if (file.Document.Passwords is { } protection && !string.IsNullOrEmpty(exportPassword))
        {
            byte[] salt;
            try { salt = Convert.FromBase64String(protection.Salt); }
            catch (FormatException ex) { throw new InvalidDataException("The file's password section is damaged.", ex); }

            key = DeriveKey(exportPassword, salt, protection.Iterations);
            if (Decrypt(key, protection.Check) != CheckText) throw new WrongExportPasswordException();
        }

        var result = new List<ConnectionProfile>();
        foreach (var entry in file.Document.Connections)
        {
            if (entry.Profile is null) continue;
            var profile = entry.Profile.Clone();
            profile.Password = null;
            profile.Ssh.Password = null;
            profile.Ssh.Passphrase = null;
            if (key is not null)
            {
                profile.Password = DecryptOrNull(key, entry.Password, profile, "password");
                profile.Ssh.Password = DecryptOrNull(key, entry.SshPassword, profile, "SSH password");
                profile.Ssh.Passphrase = DecryptOrNull(key, entry.SshPassphrase, profile, "SSH key passphrase");
                if (entry.Password is not null || entry.SshPassword is not null || entry.SshPassphrase is not null) profile.SavePassword = true;
            }
            result.Add(profile);
        }
        return result;
    }

    /// <summary>Names of imported connections that are already used (case-insensitive).</summary>
    public static IReadOnlyList<string> Conflicts(IEnumerable<ConnectionProfile> existing, IEnumerable<ConnectionProfile> incoming)
    {
        var names = existing.Select(p => p.DisplayName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return incoming.Select(p => p.DisplayName).Where(names.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The saved connections after the import. Every imported connection gets a fresh id unless it overwrites
    /// an existing one, which keeps that connection's id and position.
    /// </summary>
    public static MergeResult Merge(IEnumerable<ConnectionProfile> existing, IEnumerable<ConnectionProfile> incoming, ImportConflictChoice choice)
    {
        var merged = existing.ToList();
        int added = 0, replaced = 0, skipped = 0;

        foreach (var source in incoming)
        {
            var profile = source.Clone();
            var index = merged.FindIndex(p => string.Equals(p.DisplayName, profile.DisplayName, StringComparison.OrdinalIgnoreCase));

            if (index >= 0 && choice == ImportConflictChoice.Skip)
            {
                skipped++;
                continue;
            }

            if (index >= 0 && choice == ImportConflictChoice.Overwrite)
            {
                profile.Id = merged[index].Id;
                merged[index] = profile;
                replaced++;
                continue;
            }

            if (index >= 0) profile.Name = UniqueName(merged, profile.DisplayName);
            profile.Id = Guid.NewGuid();
            merged.Add(profile);
            added++;
        }

        return new MergeResult(merged, added, replaced, skipped);
    }

    private static string UniqueName(List<ConnectionProfile> profiles, string name)
    {
        for (var n = 2; ; n++)
        {
            var candidate = $"{name} ({n})";
            if (!profiles.Any(p => string.Equals(p.DisplayName, candidate, StringComparison.OrdinalIgnoreCase))) return candidate;
        }
    }

    private static string? EncryptOrNull(byte[]? key, string? secret) =>
        key is not null && !string.IsNullOrEmpty(secret) ? Encrypt(key, secret) : null;

    private static string? DecryptOrNull(byte[] key, string? secret, ConnectionProfile profile, string what) =>
        secret is null ? null : Decrypt(key, secret) ?? throw new InvalidDataException($"The {what} of {profile.DisplayName} is damaged.");

    private static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);

    private static string Encrypt(byte[] key, string plainText)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plain = Encoding.UTF8.GetBytes(plainText);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(key, TagSize)) aes.Encrypt(nonce, plain, cipher, tag);
        return Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    /// <summary>Null when the key is wrong or the value was tampered with.</summary>
    private static string? Decrypt(byte[] key, string protectedText)
    {
        try
        {
            var data = Convert.FromBase64String(protectedText);
            if (data.Length < NonceSize + TagSize) return null;
            var plain = new byte[data.Length - NonceSize - TagSize];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(data.AsSpan(0, NonceSize), data.AsSpan(NonceSize + TagSize), data.AsSpan(NonceSize, TagSize), plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    internal sealed class ExportDocument
    {
        public string Format { get; set; } = FormatName;
        public int Version { get; set; } = FormatVersion;
        public DateTimeOffset ExportedAt { get; set; }
        public PasswordProtection? Passwords { get; set; }
        public List<ExportedConnection> Connections { get; set; } = [];
    }

    internal sealed class PasswordProtection
    {
        public string Kdf { get; set; } = "PBKDF2-SHA256";
        public int Iterations { get; set; }
        public string Salt { get; set; } = "";
        public string Cipher { get; set; } = "AES-256-GCM";

        /// <summary>A known text encrypted with the key, so a wrong password is reported even when no connection has one.</summary>
        public string Check { get; set; } = "";
    }

    internal sealed class ExportedConnection
    {
        public ConnectionProfile? Profile { get; set; }
        public string? Password { get; set; }
        public string? SshPassword { get; set; }
        public string? SshPassphrase { get; set; }
    }
}

public sealed record MergeResult(IReadOnlyList<ConnectionProfile> Profiles, int Added, int Replaced, int Skipped);
