using DbExplorer.Core.Models;

namespace DbExplorer.Application.Compare;

/// <summary>The kinds of object the Comparer's Overview can be narrowed to.</summary>
public enum OverviewTypeGroup
{
    AllTypes,
    Tables,
    Views,
    Routines,
    Triggers,
    Other
}

/// <summary>What the Overview shows: which statuses, which kind of object, and a name filter.</summary>
public sealed record OverviewFilter
{
    public bool Different { get; init; } = true;
    public bool OnlyLeft { get; init; } = true;
    public bool OnlyRight { get; init; } = true;
    public bool Identical { get; init; }
    public bool NotCompared { get; init; }
    public OverviewTypeGroup Type { get; init; } = OverviewTypeGroup.AllTypes;
    public string Text { get; init; } = "";

    public static OverviewTypeGroup GroupOf(DbObjectType type) => type switch
    {
        DbObjectType.Table or DbObjectType.ForeignTable => OverviewTypeGroup.Tables,
        DbObjectType.View or DbObjectType.MaterializedView => OverviewTypeGroup.Views,
        DbObjectType.Procedure or DbObjectType.Function or DbObjectType.ScalarFunction or DbObjectType.TableFunction => OverviewTypeGroup.Routines,
        DbObjectType.Trigger => OverviewTypeGroup.Triggers,
        _ => OverviewTypeGroup.Other
    };

    public bool Shows(ObjectCompareStatus status) => status switch
    {
        ObjectCompareStatus.Different => Different,
        ObjectCompareStatus.OnlyLeft => OnlyLeft,
        ObjectCompareStatus.OnlyRight => OnlyRight,
        ObjectCompareStatus.Identical => Identical,
        _ => NotCompared
    };

    public bool Matches(ObjectComparisonEntry entry)
    {
        if (!Shows(entry.Status)) return false;
        if (Type != OverviewTypeGroup.AllTypes && GroupOf(entry.Type) != Type) return false;
        var text = Text.Trim();
        return text.Length == 0 || entry.FullName.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Entries that pass the type and name filters, whatever their status: what a status tile counts.</summary>
    public bool MatchesIgnoringStatus(ObjectComparisonEntry entry) => (this with
    {
        Different = true, OnlyLeft = true, OnlyRight = true, Identical = true, NotCompared = true
    }).Matches(entry);

    public IReadOnlyList<ObjectComparisonEntry> Apply(IEnumerable<ObjectComparisonEntry> entries) =>
        entries.Where(Matches).ToList();
}
