using System.Globalization;
using DbExplorer.Application.Metadata;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Query;

/// <summary>
/// What a Query tab knows while nothing is connected: the engine of the connection picked in the toolbar (for the
/// dialect's keywords, DDL templates and identifier quoting) and, when that connection was opened before, the
/// catalog saved in the metadata cache, so table and column names are still suggested. Nothing here touches a server.
/// </summary>
/// <param name="ProviderKey">"SqlServer", "Postgres" or "MySql".</param>
/// <param name="ConnectionName">The connection's display name, for the hint under the editor.</param>
/// <param name="Snapshot">The saved catalog, or null when the connection was never opened on this machine.</param>
public sealed record OfflineCatalog(string ProviderKey, string ConnectionName, MetadataSnapshot? Snapshot)
{
    public static readonly MetadataSnapshot Empty = new()
    {
        Objects = [],
        Columns = [],
        Modules = [],
        ForeignKeys = [],
        Indexes = [],
        RefreshedAt = DateTimeOffset.MinValue
    };

    /// <summary>The names suggested for a tab in <paramref name="database"/>: that database's part of a whole-server
    /// catalog, the whole catalog when the database is not in it, and nothing when no catalog was saved.</summary>
    public MetadataSnapshot SnapshotFor(string? database)
    {
        if (Snapshot is not { } s) return Empty;
        return !string.IsNullOrEmpty(database) && s.Databases.Count > 1 && s.ContainsDatabase(database) ? s.ForDatabase(database) : s;
    }

    /// <summary>Quotes an identifier the way the connection's engine does.</summary>
    public string Quote(string identifier) => Quote(ProviderKey, identifier);

    public static string Quote(string? providerKey, string identifier) => providerKey switch
    {
        "SqlServer" => "[" + identifier.Replace("]", "]]") + "]",
        "MySql" => "`" + identifier.Replace("`", "``") + "`",
        _ => "\"" + identifier.Replace("\"", "\"\"") + "\""
    };

    /// <summary>The hint under the editor: where the suggestions come from while offline.</summary>
    public string Describe(MetadataSnapshot shown)
    {
        if (Snapshot is null || ReferenceEquals(shown, Empty))
            return $"Offline · keywords only for {ConnectionName} (no saved catalog yet; connect once for table names)";
        var objects = shown.Objects.Count(o => o.Type is not DbObjectType.Trigger);
        var saved = Snapshot.RefreshedAt == DateTimeOffset.MinValue
            ? ""
            : " from " + Snapshot.RefreshedAt.LocalDateTime.ToString("d", CultureInfo.CurrentCulture);
        return $"Offline · suggestions: {objects:N0} objects saved{saved} for {ConnectionName}";
    }
}
