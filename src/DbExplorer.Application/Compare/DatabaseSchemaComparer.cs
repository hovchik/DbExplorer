using System.Globalization;
using DbExplorer.Application.Metadata;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Compare;

public enum ObjectCompareStatus
{
    Different,
    OnlyLeft,
    OnlyRight,
    Identical,

    /// <summary>Exists on both sides but the content could not be compared (e.g. source not cached, or a
    /// sequence/synonym whose definition isn't in the catalog snapshot).</summary>
    NotCompared
}

/// <summary>One object of a whole-database comparison.</summary>
public sealed record ObjectComparisonEntry
{
    public required DbObjectType Type { get; init; }
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public required ObjectCompareStatus Status { get; init; }
    public string Details { get; init; } = "";
    public DbObject? Left { get; init; }
    public DbObject? Right { get; init; }

    public string FullName => $"{Schema}.{Name}";
}

/// <summary>Compares every object of two databases using the cached catalog snapshots only, so it is instant
/// and puts no load on either server: tables by structure, views/routines/triggers by source code.</summary>
public static class DatabaseSchemaComparer
{
    public static IReadOnlyList<ObjectComparisonEntry> Compare(
        MetadataSnapshot left, string? leftDatabase, MetadataSnapshot right, string? rightDatabase, DiffOptions? options = null)
    {
        options ??= DiffOptions.Default;
        var leftObjects = ObjectsIn(left, leftDatabase);
        var rightObjects = ObjectsIn(right, rightDatabase);
        var leftModules = ModulesIn(left, leftDatabase);
        var rightModules = ModulesIn(right, rightDatabase);

        var entries = new List<ObjectComparisonEntry>();
        var matchedRight = new HashSet<DbObject>(ReferenceEqualityComparer.Instance);

        foreach (var l in leftObjects.Values)
        {
            if (!rightObjects.TryGetValue(KeyOf(l), out var r))
            {
                entries.Add(Entry(l, null, ObjectCompareStatus.OnlyLeft, "Only exists in left"));
                continue;
            }

            matchedRight.Add(r);
            entries.Add(CompareMatched(left, l, right, r, leftModules, rightModules, options));
        }

        foreach (var r in rightObjects.Values.Where(r => !matchedRight.Contains(r)))
            entries.Add(Entry(null, r, ObjectCompareStatus.OnlyRight, "Only exists in right"));

        return entries
            .OrderBy(e => e.Status)
            .ThenBy(e => e.Type)
            .ThenBy(e => e.Schema, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ObjectComparisonEntry CompareMatched(
        MetadataSnapshot leftSnapshot, DbObject l, MetadataSnapshot rightSnapshot, DbObject r,
        IReadOnlyDictionary<(DbObjectType, string, string), string?> leftModules,
        IReadOnlyDictionary<(DbObjectType, string, string), string?> rightModules,
        DiffOptions options)
    {
        if (l.Type is DbObjectType.Table or DbObjectType.ForeignTable)
        {
            var structure = TableStructureComparer.Compare(leftSnapshot, l, rightSnapshot, r);
            var rowNote = RowCountNote(l, r);
            var changes = structure.Where(s => s.Change != StructureChange.Same).ToList();
            if (changes.Count == 0)
                return Entry(l, r, ObjectCompareStatus.Identical, "Same structure" + rowNote);

            var summary = string.Join(", ", changes.GroupBy(c => c.Category)
                .Select(g => $"{g.Count()} {g.Key.ToLowerInvariant()} change(s)"));
            return Entry(l, r, ObjectCompareStatus.Different, summary + rowNote);
        }

        var key = KeyOf(l);
        var hasLeft = leftModules.TryGetValue(key, out var leftSource) && leftSource is not null;
        var hasRight = rightModules.TryGetValue(key, out var rightSource) && rightSource is not null;
        if (!hasLeft || !hasRight)
        {
            var reason = leftModules.ContainsKey(key) || rightModules.ContainsKey(key)
                ? "Source not available on " + (!hasLeft && !hasRight ? "either side" : !hasLeft ? "left" : "right")
                : "Exists on both sides (only existence is compared for this type)";
            return Entry(l, r, ObjectCompareStatus.NotCompared, reason);
        }

        if (NormalizeSource(leftSource!, options) == NormalizeSource(rightSource!, options))
            return Entry(l, r, ObjectCompareStatus.Identical, "Same source (ignoring whitespace)");

        var stats = TextDiffer.Statistics(TextDiffer.Diff(leftSource, rightSource, SchemaCompareMode.LineByLine,
            options with { IgnoreWhitespace = true }));
        var detail = stats.IsIdentical
            ? "Differs only in line breaks"
            : $"−{stats.Removed} / +{stats.Added} line(s), {stats.SimilarityPercent.ToString("0.#", CultureInfo.InvariantCulture)}% similar";
        return Entry(l, r, ObjectCompareStatus.Different, detail);
    }

    private static string RowCountNote(DbObject l, DbObject r) =>
        l.RowCount is long lc && r.RowCount is long rc
            ? $" · rows ≈ {lc.ToString("N0", CultureInfo.CurrentCulture)} vs {rc.ToString("N0", CultureInfo.CurrentCulture)}"
            : "";

    /// <summary>Whitespace-insensitive (and optionally case-insensitive) form of a module's source.</summary>
    private static string NormalizeSource(string source, DiffOptions options)
    {
        var tokens = source.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var joined = string.Join(' ', tokens);
        return options.IgnoreCase ? joined.ToUpperInvariant() : joined;
    }

    private static ObjectComparisonEntry Entry(DbObject? l, DbObject? r, ObjectCompareStatus status, string details)
    {
        var o = l ?? r!;
        return new ObjectComparisonEntry
        {
            Type = o.Type, Schema = o.Schema, Name = o.Name, Status = status, Details = details, Left = l, Right = r
        };
    }

    private static (DbObjectType, string, string) KeyOf(DbObject o) =>
        (o.Type, o.Schema.ToUpperInvariant(), o.Name.ToUpperInvariant());

    private static bool InDatabase(string objectDatabase, string? database) =>
        string.IsNullOrEmpty(database) || string.Equals(objectDatabase, database, StringComparison.OrdinalIgnoreCase);

    private static Dictionary<(DbObjectType, string, string), DbObject> ObjectsIn(MetadataSnapshot snapshot, string? database)
    {
        var dict = new Dictionary<(DbObjectType, string, string), DbObject>();
        foreach (var o in snapshot.Objects.Where(o => InDatabase(o.Database, database)))
            dict.TryAdd(KeyOf(o), o);
        return dict;
    }

    /// <summary>Source per object; several modules with the same name (e.g. PostgreSQL overloads) are joined,
    /// the same way the definition service shows them.</summary>
    private static Dictionary<(DbObjectType, string, string), string?> ModulesIn(MetadataSnapshot snapshot, string? database) =>
        snapshot.Modules
            .Where(m => InDatabase(m.Database, database))
            .GroupBy(m => (m.Type, m.Schema.ToUpperInvariant(), m.Name.ToUpperInvariant()))
            .ToDictionary(
                g => g.Key,
                g => g.Any(m => m.Definition is not null)
                    ? string.Join("\n\n", g.Where(m => m.Definition is not null).Select(m => m.Definition))
                    : null);
}
