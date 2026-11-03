using DbExplorer.Core.Models;

namespace DbExplorer.Application.Search;

/// <summary>How well an object matched; the results list is sorted by it, best first.</summary>
public enum MetadataSearchRank
{
    /// <summary>The object is called exactly what was typed.</summary>
    ExactName,

    /// <summary>The object's name starts with what was typed.</summary>
    NameStart,

    /// <summary>The name contains it (or matches the regular expression).</summary>
    Name,

    /// <summary>Only a column is called exactly what was typed.</summary>
    ExactColumn,

    /// <summary>Only a column name contains it.</summary>
    Column,

    /// <summary>Only the source code mentions it.</summary>
    Code
}

/// <summary>The object kinds the type filter offers.</summary>
public enum MetadataTypeFilter
{
    All,
    Tables,
    Views,
    Routines,
    Triggers,
    Other
}

/// <summary>Everything one search found in one object: its name, its columns and its source lines.</summary>
public sealed record MetadataSearchGroup(
    string Database,
    string Schema,
    string ObjectName,
    DbObjectType ObjectType,
    MetadataSearchRank Rank,
    MetadataSearchResult? NameMatch,
    IReadOnlyList<MetadataSearchResult> Columns,
    IReadOnlyList<MetadataSearchResult> CodeLines)
{
    public string FullName => $"{Schema}.{ObjectName}";

    public int HitCount => (NameMatch is null ? 0 : 1) + Columns.Count + CodeLines.Count;

    public DbObject ToObject() => new() { Database = Database, Schema = Schema, Name = ObjectName, Type = ObjectType };
}

public static class MetadataSearchGrouping
{
    /// <summary>One group per object, best matches first: the object called exactly that, names that start with it,
    /// other name matches, objects with such a column, then objects whose code mentions it. Ties go to the shorter
    /// name, then alphabetical.</summary>
    public static IReadOnlyList<MetadataSearchGroup> Group(IEnumerable<MetadataSearchResult> results, MetadataSearchQuery query)
    {
        var comparison = query.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var text = query.Text.Trim();

        MetadataSearchRank NameRank(string name) =>
            query.UseRegex ? MetadataSearchRank.Name
            : name.Equals(text, comparison) ? MetadataSearchRank.ExactName
            : name.StartsWith(text, comparison) ? MetadataSearchRank.NameStart
            : MetadataSearchRank.Name;

        return results
            .GroupBy(r => (r.Database, r.Schema, r.ObjectName, r.ObjectType))
            .Select(g =>
            {
                var name = g.FirstOrDefault(r => r.Kind == MetadataMatchKind.Object);
                var columns = g.Where(r => r.Kind == MetadataMatchKind.Column).ToList();
                var code = g.Where(r => r.Kind == MetadataMatchKind.Definition).OrderBy(r => r.Line).ToList();
                var rank = name is not null ? NameRank(g.Key.ObjectName)
                    : columns.Count > 0 ? (!query.UseRegex && columns.Any(c => string.Equals(c.Column, text, comparison))
                        ? MetadataSearchRank.ExactColumn : MetadataSearchRank.Column)
                    : MetadataSearchRank.Code;
                return new MetadataSearchGroup(g.Key.Database, g.Key.Schema, g.Key.ObjectName, g.Key.ObjectType, rank, name, columns, code);
            })
            .OrderBy(g => g.Rank)
            .ThenBy(g => g.ObjectName.Length)
            .ThenBy(g => g.Schema, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.ObjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Database, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool Matches(this MetadataTypeFilter filter, DbObjectType type) => filter switch
    {
        MetadataTypeFilter.All => true,
        MetadataTypeFilter.Tables => type is DbObjectType.Table or DbObjectType.ForeignTable,
        MetadataTypeFilter.Views => type is DbObjectType.View or DbObjectType.MaterializedView,
        MetadataTypeFilter.Routines => type is DbObjectType.Procedure or DbObjectType.Function
            or DbObjectType.ScalarFunction or DbObjectType.TableFunction,
        MetadataTypeFilter.Triggers => type is DbObjectType.Trigger,
        _ => type is DbObjectType.Sequence or DbObjectType.Synonym or DbObjectType.Other
    };

    public static string Label(this MetadataTypeFilter filter) => filter switch
    {
        MetadataTypeFilter.All => "All types",
        MetadataTypeFilter.Tables => "Tables",
        MetadataTypeFilter.Views => "Views",
        MetadataTypeFilter.Routines => "Procedures & functions",
        MetadataTypeFilter.Triggers => "Triggers",
        _ => "Sequences, synonyms, other"
    };

    /// <summary>The text the preview shows for an object, and which line a hit is on in it: a module's source
    /// (overloads joined by a blank line, the way <see cref="MetadataSearchService"/> numbers lines), or for a table
    /// or view without cached source, null so the caller scripts it.</summary>
    public static string? ModuleText(Metadata.MetadataSnapshot snapshot, string database, string schema, string name, DbObjectType type)
    {
        var texts = snapshot.Modules
            .Where(m => m.Database == database && m.Schema == schema && m.Name == name && m.Type == type && m.Definition is not null)
            .Select(m => m.Definition!)
            .ToList();
        return texts.Count == 0 ? null : string.Join("\n\n", texts).Replace("\r\n", "\n");
    }
}
