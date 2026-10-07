using DbExplorer.Core.Connections;

namespace DbExplorer.Application.Connections;

/// <summary>How saved connections are grouped into folders in the connection list.</summary>
public static class ConnectionFolders
{
    /// <summary>"Clients / Acme /" and "clients/acme" are the same folder: "Clients/Acme".</summary>
    public static string Normalize(string? folder) =>
        string.Join("/", (folder ?? "").Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>Connections outside folders first, then each folder alphabetically; by name inside each.</summary>
    public static IReadOnlyList<ConnectionProfile> Order(IEnumerable<ConnectionProfile> profiles) =>
        profiles
            .OrderBy(p => Normalize(p.Folder).Length > 0)
            .ThenBy(p => Normalize(p.Folder), StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Every folder in use, for the folder box of the connection dialog.</summary>
    public static IReadOnlyList<string> All(IEnumerable<ConnectionProfile> profiles) =>
        profiles.Select(p => Normalize(p.Folder))
            .Where(f => f.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
