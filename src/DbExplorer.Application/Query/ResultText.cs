using System.Globalization;
using System.Text;

namespace DbExplorer.Application.Query;

/// <summary>
/// A result as plain text, like psql or SSMS's "Results to text": a header, a rule, one line per row with columns
/// padded to the widest value (numbers right-aligned), and a row count. Long values are cut and line breaks shown as ↵,
/// so every row stays on one line and the text can be pasted into a ticket or chat as it looks.
/// </summary>
public static class ResultText
{
    public const int DefaultMaxRows = 1000;
    public const int MaxColumnWidth = 60;
    private const string Null = "NULL";

    public static string Format(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<object?>> rows,
        int maxRows = DefaultMaxRows, long? totalRows = null, bool totalIsExact = true)
    {
        if (columns.Count == 0) return rows.Count == 0 ? "(no columns)\n" : "";
        var shown = rows.Take(Math.Max(0, maxRows)).ToList();
        var cells = shown.Select(r => columns.Select((_, i) => Cell(i < r.Count ? r[i] : null)).ToArray()).ToList();
        var numeric = columns.Select((_, i) => shown.Count > 0 && shown.All(r => i >= r.Count || r[i] is null or DBNull || IsNumber(r[i]))
                                                && shown.Any(r => i < r.Count && IsNumber(r[i]))).ToArray();
        var widths = columns.Select((c, i) => Math.Max(Fit(c).Length, cells.Count == 0 ? 0 : cells.Max(r => r[i].Length))).ToArray();

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(" | ", columns.Select((c, i) => Fit(c).PadRight(widths[i]))).TrimEnd());
        sb.AppendLine(string.Join("-+-", widths.Select(w => new string('-', w))));
        foreach (var row in cells)
            sb.AppendLine(string.Join(" | ", row.Select((v, i) => numeric[i] ? v.PadLeft(widths[i]) : v.PadRight(widths[i]))).TrimEnd());

        var total = totalRows ?? rows.Count;
        sb.Append('(').Append(shown.Count.ToString("N0", CultureInfo.CurrentCulture)).Append(shown.Count == 1 ? " row" : " rows");
        if (shown.Count < rows.Count || total > shown.Count || !totalIsExact)
            sb.Append(" shown of ").Append(totalIsExact ? total.ToString("N0", CultureInfo.CurrentCulture) : "more than " + rows.Count.ToString("N0", CultureInfo.CurrentCulture));
        sb.AppendLine(")");
        return sb.ToString();
    }

    private static string Cell(object? value) => ResultViewQuery.DisplayText(value) is { } text ? Fit(text) : Null;

    private static string Fit(string text)
    {
        var line = text.Replace("\r\n", "↵").Replace('\n', '↵').Replace('\r', '↵').Replace('\t', ' ');
        return line.Length > MaxColumnWidth ? line[..(MaxColumnWidth - 1)] + "…" : line;
    }

    private static bool IsNumber(object? value) => value is sbyte or byte or short or ushort or int or uint or long or ulong
        or float or double or decimal or System.Numerics.BigInteger;
}
