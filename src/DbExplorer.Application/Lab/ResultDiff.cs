using System.Globalization;

namespace DbExplorer.Application.Lab;

public enum ResultRowChange
{
    Added,
    Removed,
    Changed
}

/// <summary>One cell of a row that is in both runs but holds a different value now.</summary>
public sealed record ResultCellChange(string Column, object? Before, object? After);

/// <summary>A row that appeared, disappeared or changed between two runs of the same query.</summary>
/// <param name="Key">How the row is identified: its key values ("Id=7"), or "(whole row)" without a key.</param>
/// <param name="Values">The row as it is now; for a removed row, as it was.</param>
public sealed record ResultRowDiff(ResultRowChange Change, string Key, IReadOnlyList<object?> Values, IReadOnlyList<ResultCellChange> Cells);

/// <summary>What changed in a result set between two runs.</summary>
/// <param name="KeyColumns">The columns rows were matched on; empty when whole rows were compared.</param>
public sealed record ResultDiffReport(
    IReadOnlyList<string> KeyColumns, int Added, int Removed, int Changed, int Unchanged, IReadOnlyList<ResultRowDiff> Rows)
{
    public bool HasChanges => Added + Removed + Changed > 0;

    /// <summary>"+3 new · −1 gone · ~2 changed", or "no changes".</summary>
    public string Summary
    {
        get
        {
            if (!HasChanges) return "no changes";
            var parts = new List<string>();
            if (Added > 0) parts.Add($"+{Added:N0} new");
            if (Removed > 0) parts.Add($"−{Removed:N0} gone");
            if (Changed > 0) parts.Add($"~{Changed:N0} changed");
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// Compares two results of the same query, row by row. Rows are matched on a key: the one given (the primary key of the
/// table the result reads), else an id-like or first column whose values are unique and non-null in both runs. Without
/// such a column whole rows are compared, so a changed row shows as one gone and one new. Runs entirely on the rows
/// already read; nothing is sent to the database.
/// </summary>
public static class ResultDiff
{
    /// <summary>The diff, or null when the two results do not have the same columns (the query changed shape).</summary>
    public static ResultDiffReport? Compare(
        IReadOnlyList<string> beforeColumns, IReadOnlyList<IReadOnlyList<object?>> beforeRows,
        IReadOnlyList<string> afterColumns, IReadOnlyList<IReadOnlyList<object?>> afterRows,
        IReadOnlyList<int>? keyColumns = null)
    {
        if (!beforeColumns.SequenceEqual(afterColumns, StringComparer.OrdinalIgnoreCase)) return null;

        var key = keyColumns is { Count: > 0 } given && given.All(k => k >= 0 && k < afterColumns.Count) && IsUnique(beforeRows, given) && IsUnique(afterRows, given)
            ? given
            : GuessKey(afterColumns, beforeRows, afterRows);

        return key is null
            ? CompareWholeRows(afterColumns, beforeRows, afterRows)
            : CompareByKey(afterColumns, beforeRows, afterRows, key);
    }

    /// <summary>An id-like column ("Id", "OrderId", "order_id") or else the first column, when unique and non-null in both runs.</summary>
    public static IReadOnlyList<int>? GuessKey(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<object?>> before, IReadOnlyList<IReadOnlyList<object?>> after)
    {
        var candidates = Enumerable.Range(0, columns.Count)
            .Where(i => IsIdLike(columns[i]))
            .OrderBy(i => string.Equals(columns[i], "id", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(i => i)
            .Append(0)
            .Distinct();
        foreach (var c in candidates)
        {
            if (c >= columns.Count) continue;
            int[] key = [c];
            if (IsUnique(before, key) && IsUnique(after, key)) return key;
        }
        return null;
    }

    private static bool IsIdLike(string column) =>
        string.Equals(column, "id", StringComparison.OrdinalIgnoreCase) ||
        column.EndsWith("_id", StringComparison.OrdinalIgnoreCase) ||
        column.Length > 2 && column.EndsWith("Id", StringComparison.Ordinal) && char.IsLower(column[^3]);

    private static bool IsUnique(IReadOnlyList<IReadOnlyList<object?>> rows, IReadOnlyList<int> key)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (key.Any(k => k >= row.Count || row[k] is null or DBNull)) return false;
            if (!seen.Add(KeyOf(row, key))) return false;
        }
        return true;
    }

    private static ResultDiffReport CompareByKey(
        IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<object?>> before, IReadOnlyList<IReadOnlyList<object?>> after, IReadOnlyList<int> key)
    {
        var old = new Dictionary<string, IReadOnlyList<object?>>(StringComparer.Ordinal);
        foreach (var row in before) old[KeyOf(row, key)] = row;

        var diffs = new List<ResultRowDiff>();
        int added = 0, changed = 0, unchanged = 0;
        foreach (var row in after)
        {
            var k = KeyOf(row, key);
            if (!old.Remove(k, out var previous))
            {
                added++;
                diffs.Add(new ResultRowDiff(ResultRowChange.Added, Describe(columns, row, key), row, []));
                continue;
            }
            var cells = new List<ResultCellChange>();
            for (var i = 0; i < columns.Count; i++)
                if (Text(Cell(previous, i)) != Text(Cell(row, i)))
                    cells.Add(new ResultCellChange(columns[i], Cell(previous, i), Cell(row, i)));
            if (cells.Count == 0) { unchanged++; continue; }
            changed++;
            diffs.Add(new ResultRowDiff(ResultRowChange.Changed, Describe(columns, row, key), row, cells));
        }
        // Rows of the earlier run that are not there any more, in their original order.
        var removed = before.Where(r => old.ContainsKey(KeyOf(r, key))).ToList();
        diffs.AddRange(removed.Select(r => new ResultRowDiff(ResultRowChange.Removed, Describe(columns, r, key), r, [])));

        return new ResultDiffReport(key.Select(k => columns[k]).ToList(), added, removed.Count, changed, unchanged, Order(diffs));
    }

    /// <summary>Without a key, rows are compared as whole values (as a multiset, so duplicates count).</summary>
    private static ResultDiffReport CompareWholeRows(
        IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<object?>> before, IReadOnlyList<IReadOnlyList<object?>> after)
    {
        var all = Enumerable.Range(0, columns.Count).ToArray();
        var remaining = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in before)
        {
            var k = KeyOf(row, all);
            remaining[k] = remaining.GetValueOrDefault(k) + 1;
        }

        var diffs = new List<ResultRowDiff>();
        var unchanged = 0;
        foreach (var row in after)
        {
            var k = KeyOf(row, all);
            if (remaining.TryGetValue(k, out var n) && n > 0)
            {
                remaining[k] = n - 1;
                unchanged++;
            }
            else
            {
                diffs.Add(new ResultRowDiff(ResultRowChange.Added, "(whole row)", row, []));
            }
        }
        foreach (var row in before)
        {
            var k = KeyOf(row, all);
            if (remaining.TryGetValue(k, out var n) && n > 0)
            {
                remaining[k] = n - 1;
                diffs.Add(new ResultRowDiff(ResultRowChange.Removed, "(whole row)", row, []));
            }
        }
        var added = diffs.Count(d => d.Change == ResultRowChange.Added);
        return new ResultDiffReport([], added, diffs.Count - added, 0, unchanged, Order(diffs));
    }

    private static IReadOnlyList<ResultRowDiff> Order(List<ResultRowDiff> diffs) =>
        diffs.OrderBy(d => d.Change switch { ResultRowChange.Added => 0, ResultRowChange.Changed => 1, _ => 2 }).ToList();

    private static object? Cell(IReadOnlyList<object?> row, int i) => i < row.Count ? row[i] : null;

    private static string KeyOf(IReadOnlyList<object?> row, IReadOnlyList<int> key) =>
        string.Join("\u001F", key.Select(k => Text(Cell(row, k))));

    private static string Describe(IReadOnlyList<string> columns, IReadOnlyList<object?> row, IReadOnlyList<int> key) =>
        string.Join(", ", key.Select(k => $"{columns[k]}={Display(Cell(row, k))}"));

    /// <summary>A value as text for comparing: invariant culture, binary as hex, NULL distinct from the text "NULL".</summary>
    public static string Text(object? value) => value switch
    {
        null or DBNull => "\u0000NULL",
        byte[] b => "0x" + Convert.ToHexString(b),
        DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset d => d.ToString("O", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };

    /// <summary>A value for showing to the user.</summary>
    public static string Display(object? value)
    {
        var text = value switch
        {
            null or DBNull => "NULL",
            byte[] b => "0x" + Convert.ToHexString(b.Length > 16 ? b[..16] : b) + (b.Length > 16 ? "…" : ""),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? ""
        };
        return text.Length > 80 ? text[..80] + "…" : text;
    }
}
