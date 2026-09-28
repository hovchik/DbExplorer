using DbExplorer.Application.Metadata;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Compare;

public enum StructureChange
{
    Same,
    Changed,
    OnlyLeft,
    OnlyRight
}

/// <summary>One column, index or foreign key of a table, as it exists on each side.</summary>
public sealed record StructureDiffRow
{
    /// <summary>"Column", "Index" or "Foreign key".</summary>
    public required string Category { get; init; }
    public required string Name { get; init; }
    public required StructureChange Change { get; init; }
    public string? Left { get; init; }
    public string? Right { get; init; }

    /// <summary>What exactly differs, e.g. "type int → bigint; now NOT NULL".</summary>
    public string Details { get; init; } = "";
}

/// <summary>Compares the columns, indexes and foreign keys of two tables using catalog metadata only
/// (no server round-trip).</summary>
public static class TableStructureComparer
{
    public const string ColumnCategory = "Column";
    public const string IndexCategory = "Index";
    public const string ForeignKeyCategory = "Foreign key";

    public static IReadOnlyList<StructureDiffRow> Compare(
        MetadataSnapshot left, DbObject leftTable, MetadataSnapshot right, DbObject rightTable) =>
        Compare(
            left.ColumnsOf(leftTable.Database, leftTable.Schema, leftTable.Name).ToList(),
            left.IndexesOf(leftTable.Database, leftTable.Schema, leftTable.Name).ToList(),
            left.ForeignKeysOf(leftTable.Database, leftTable.Schema, leftTable.Name).ToList(),
            right.ColumnsOf(rightTable.Database, rightTable.Schema, rightTable.Name).ToList(),
            right.IndexesOf(rightTable.Database, rightTable.Schema, rightTable.Name).ToList(),
            right.ForeignKeysOf(rightTable.Database, rightTable.Schema, rightTable.Name).ToList());

    public static IReadOnlyList<StructureDiffRow> Compare(
        IReadOnlyList<DbColumn> leftColumns, IReadOnlyList<DbIndex> leftIndexes, IReadOnlyList<DbForeignKey> leftForeignKeys,
        IReadOnlyList<DbColumn> rightColumns, IReadOnlyList<DbIndex> rightIndexes, IReadOnlyList<DbForeignKey> rightForeignKeys)
    {
        var rows = new List<StructureDiffRow>();
        rows.AddRange(CompareColumns(leftColumns, rightColumns));
        rows.AddRange(CompareByName(IndexCategory, leftIndexes, rightIndexes, i => i.IsPrimaryKey ? "(primary key)" : i.Name, Describe));
        rows.AddRange(CompareByName(ForeignKeyCategory, leftForeignKeys, rightForeignKeys, f => f.Name, Describe));
        return rows;
    }

    private static IEnumerable<StructureDiffRow> CompareColumns(IReadOnlyList<DbColumn> left, IReadOnlyList<DbColumn> right)
    {
        var rightByName = right.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var leftNames = left.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Positions among the columns both sides share, so one added column doesn't flag every later one as moved.
        var leftCommon = left.Where(c => rightByName.ContainsKey(c.Name)).OrderBy(c => c.Ordinal).Select(c => c.Name).ToList();
        var rightCommon = right.Where(c => leftNames.Contains(c.Name)).OrderBy(c => c.Ordinal).Select(c => c.Name).ToList();

        foreach (var l in left.OrderBy(c => c.Ordinal))
        {
            if (!rightByName.TryGetValue(l.Name, out var r))
            {
                yield return new StructureDiffRow
                {
                    Category = ColumnCategory, Name = l.Name, Change = StructureChange.OnlyLeft, Left = Describe(l),
                    Details = "Column only exists in left"
                };
                continue;
            }

            var changes = new List<string>();
            if (!string.Equals(l.DataType, r.DataType, StringComparison.OrdinalIgnoreCase))
                changes.Add($"type {l.DataType} → {r.DataType}");
            if (l.IsNullable != r.IsNullable)
                changes.Add(r.IsNullable ? "now NULL" : "now NOT NULL");
            if (l.IsPrimaryKey != r.IsPrimaryKey)
                changes.Add(r.IsPrimaryKey ? "added to primary key" : "removed from primary key");
            if (l.IsComputed != r.IsComputed)
                changes.Add(r.IsComputed ? "now computed" : "no longer computed");
            var lp = IndexOf(leftCommon, l.Name);
            var rp = IndexOf(rightCommon, r.Name);
            if (lp != rp)
                changes.Add($"position {lp + 1} → {rp + 1}");

            yield return new StructureDiffRow
            {
                Category = ColumnCategory, Name = l.Name,
                Change = changes.Count == 0 ? StructureChange.Same : StructureChange.Changed,
                Left = Describe(l), Right = Describe(r), Details = string.Join("; ", changes)
            };
        }

        foreach (var r in right.OrderBy(c => c.Ordinal).Where(c => !leftNames.Contains(c.Name)))
            yield return new StructureDiffRow
            {
                Category = ColumnCategory, Name = r.Name, Change = StructureChange.OnlyRight, Right = Describe(r),
                Details = "Column only exists in right"
            };
    }

    /// <summary>Matches by name first; an unmatched item on one side with an identical definition on the other
    /// is reported as a rename rather than a drop plus a create (auto-generated names differ between servers).</summary>
    private static IEnumerable<StructureDiffRow> CompareByName<T>(
        string category, IReadOnlyList<T> left, IReadOnlyList<T> right, Func<T, string> name, Func<T, string> describe)
    {
        var rightByName = right.GroupBy(name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var matchedRight = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unmatchedLeft = new List<T>();
        var rows = new List<StructureDiffRow>();

        foreach (var l in left.OrderBy(name, StringComparer.OrdinalIgnoreCase))
        {
            if (!rightByName.TryGetValue(name(l), out var r))
            {
                unmatchedLeft.Add(l);
                continue;
            }

            matchedRight.Add(name(r));
            var ld = describe(l);
            var rd = describe(r);
            var same = string.Equals(ld, rd, StringComparison.OrdinalIgnoreCase);
            rows.Add(new StructureDiffRow
            {
                Category = category, Name = name(l), Change = same ? StructureChange.Same : StructureChange.Changed,
                Left = ld, Right = rd, Details = same ? "" : "Definition differs"
            });
        }

        var unmatchedRight = right.Where(r => !matchedRight.Contains(name(r))).ToList();
        foreach (var l in unmatchedLeft)
        {
            var ld = describe(l);
            var renamed = unmatchedRight.FirstOrDefault(r => string.Equals(describe(r), ld, StringComparison.OrdinalIgnoreCase));
            if (renamed is not null)
            {
                unmatchedRight.Remove(renamed);
                rows.Add(new StructureDiffRow
                {
                    Category = category, Name = name(l), Change = StructureChange.Changed, Left = ld, Right = ld,
                    Details = $"renamed {name(l)} → {name(renamed)}"
                });
                continue;
            }

            rows.Add(new StructureDiffRow
            {
                Category = category, Name = name(l), Change = StructureChange.OnlyLeft, Left = ld,
                Details = $"{category} only exists in left"
            });
        }

        foreach (var r in unmatchedRight)
            rows.Add(new StructureDiffRow
            {
                Category = category, Name = name(r), Change = StructureChange.OnlyRight, Right = describe(r),
                Details = $"{category} only exists in right"
            });

        return rows;
    }

    private static int IndexOf(List<string> names, string name) =>
        names.FindIndex(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

    private static string Describe(DbColumn c) =>
        $"{c.DataType}{(c.IsNullable ? " NULL" : " NOT NULL")}{(c.IsPrimaryKey ? " PK" : "")}{(c.IsComputed ? " computed" : "")}";

    private static string Describe(DbIndex i)
    {
        var parts = new List<string>();
        if (i.IsPrimaryKey) parts.Add("PRIMARY KEY");
        else if (i.IsUnique) parts.Add("UNIQUE");
        if (!string.IsNullOrWhiteSpace(i.Type)) parts.Add(i.Type.ToUpperInvariant());
        parts.Add($"({Normalize(i.Columns)})");
        if (!string.IsNullOrWhiteSpace(i.IncludedColumns)) parts.Add($"INCLUDE ({Normalize(i.IncludedColumns)})");
        if (!string.IsNullOrWhiteSpace(i.Filter)) parts.Add("WHERE " + i.Filter.Trim());
        if (i.IsDisabled) parts.Add("disabled");
        return string.Join(" ", parts);
    }

    private static string Describe(DbForeignKey f) =>
        $"({Normalize(f.Columns)}) → {f.ReferencedSchema}.{f.ReferencedTable} ({Normalize(f.ReferencedColumns)})" +
        (f.IsDisabled ? " disabled" : "");

    private static string Normalize(string? list) =>
        string.Join(", ", (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
