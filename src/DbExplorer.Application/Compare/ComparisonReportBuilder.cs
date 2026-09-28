using System.Globalization;
using System.Net;
using System.Text;
using DbExplorer.Application.Export;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Compare;

/// <summary>One side of a comparison as it should be labelled in a report.</summary>
public sealed record ReportSide(string Connection, string Database, string ObjectName)
{
    public string Label => string.IsNullOrEmpty(Database) ? $"{Connection} · {ObjectName}" : $"{Connection} · {Database} · {ObjectName}";
}

/// <summary>Self-contained HTML (single file, inline CSS) and Markdown reports of schema/data comparisons,
/// suitable for attaching to change tickets or pull requests.</summary>
public static class ComparisonReportBuilder
{
    public const int MaxDataRows = 5000;

    public static string SchemaMarkdown(ReportSide left, ReportSide right, IReadOnlyList<DiffLine> diff, DateTimeOffset generatedAt)
    {
        var (removed, added) = Count(diff);
        var sb = new StringBuilder();
        sb.Append("# Schema comparison: ").Append(right.ObjectName).Append("\n\n");
        AppendMarkdownHeader(sb, left, right, generatedAt);
        sb.Append(removed == 0 && added == 0
            ? "**Definitions are identical.**\n"
            : $"**{removed} line(s) only on left · {added} line(s) only on right**\n");
        if (removed == 0 && added == 0) return sb.ToString();

        sb.Append("\n```diff\n");
        sb.Append("--- ").Append(left.Label).Append('\n');
        sb.Append("+++ ").Append(right.Label).Append('\n');
        foreach (var line in diff)
        {
            switch (line.Kind)
            {
                case DiffLineKind.Equal: sb.Append("  ").Append(line.LeftText).Append('\n'); break;
                case DiffLineKind.Removed: sb.Append("- ").Append(line.LeftText).Append('\n'); break;
                case DiffLineKind.Added: sb.Append("+ ").Append(line.RightText).Append('\n'); break;
            }
        }
        sb.Append("```\n");
        return sb.ToString();
    }

    public static string SchemaHtml(ReportSide left, ReportSide right, IReadOnlyList<DiffLine> diff, DateTimeOffset generatedAt)
    {
        var (removed, added) = Count(diff);
        var sb = new StringBuilder();
        AppendHtmlStart(sb, $"Schema comparison: {right.ObjectName}", left, right, generatedAt);
        sb.Append("<p class=\"summary\">")
          .Append(removed == 0 && added == 0
              ? "Definitions are identical."
              : $"<span class=\"del\">{removed} line(s) only on left</span> · <span class=\"add\">{added} line(s) only on right</span>")
          .Append("</p>\n");

        sb.Append("<table class=\"diff\"><thead><tr><th>#</th><th>Left</th><th>#</th><th>Right</th></tr></thead><tbody>\n");
        foreach (var line in diff)
        {
            var cls = line.Kind switch { DiffLineKind.Removed => "del", DiffLineKind.Added => "add", _ => "" };
            sb.Append("<tr class=\"").Append(cls).Append("\"><td class=\"n\">").Append(line.LeftLineNumber)
              .Append("</td><td><pre>").Append(E(line.LeftText)).Append("</pre></td><td class=\"n\">").Append(line.RightLineNumber)
              .Append("</td><td><pre>").Append(E(line.RightText)).Append("</pre></td></tr>\n");
        }
        sb.Append("</tbody></table>\n");
        AppendHtmlEnd(sb);
        return sb.ToString();
    }

    public static string DataMarkdown(
        ReportSide left, ReportSide right, DataComparisonResult result, bool onlyDifferences, DateTimeOffset generatedAt)
    {
        var sb = new StringBuilder();
        sb.Append("# Data comparison: ").Append(right.ObjectName).Append("\n\n");
        AppendMarkdownHeader(sb, left, right, generatedAt);
        AppendDataSummaryMarkdown(sb, result);

        var rows = SelectRows(result, onlyDifferences, out var truncated);
        if (rows.Count == 0) return sb.ToString();

        sb.Append("\n| Key | Status | Differences |\n| --- | --- | --- |\n");
        foreach (var row in rows)
            sb.Append("| ").Append(Md(row.Key)).Append(" | ").Append(row.StatusText)
              .Append(" | ").Append(Md(row.Summary)).Append(" |\n");
        if (truncated) sb.Append($"\n_Only the first {MaxDataRows:N0} rows are listed._\n");
        return sb.ToString();
    }

    public static string DataHtml(
        ReportSide left, ReportSide right, DataComparisonResult result, bool onlyDifferences, DateTimeOffset generatedAt)
    {
        var sb = new StringBuilder();
        AppendHtmlStart(sb, $"Data comparison: {right.ObjectName}", left, right, generatedAt);

        var (same, different, onlyLeft, onlyRight) = Counts(result);
        sb.Append("<p class=\"summary\">")
          .Append($"{same:N0} same · <span class=\"chg\">{different:N0} different</span> · ")
          .Append($"<span class=\"del\">{onlyLeft:N0} only in left</span> · <span class=\"add\">{onlyRight:N0} only in right</span>")
          .Append("</p>\n");
        sb.Append("<ul class=\"meta\">");
        foreach (var note in DataNotes(result)) sb.Append("<li>").Append(E(note)).Append("</li>");
        sb.Append("</ul>\n");
        AppendColumnDifferencesHtml(sb, result);

        var rows = SelectRows(result, onlyDifferences, out var truncated);
        var columns = rows.FirstOrDefault()?.Cells.Select(c => c.Column).ToList() ?? [];
        if (rows.Count > 0)
        {
            sb.Append("<table class=\"data\"><thead><tr><th>Key</th><th>Status</th>");
            foreach (var c in columns) sb.Append("<th>").Append(E(c)).Append("</th>");
            sb.Append("</tr></thead><tbody>\n");
            foreach (var row in rows)
            {
                var cls = row.Status switch
                {
                    DataRowStatus.Different => "chg",
                    DataRowStatus.OnlyLeft => "del",
                    DataRowStatus.OnlyRight => "add",
                    _ => ""
                };
                sb.Append("<tr class=\"").Append(cls).Append("\"><td>").Append(E(row.Key)).Append("</td><td>").Append(row.StatusText).Append("</td>");
                foreach (var cell in row.Cells)
                {
                    if (row.Status == DataRowStatus.Different && cell.IsDifferent)
                        sb.Append("<td class=\"cell-chg\"><span class=\"old\">").Append(V(cell.LeftValue))
                          .Append("</span> → <span class=\"new\">").Append(V(cell.RightValue)).Append("</span></td>");
                    else
                        sb.Append("<td>").Append(V(row.Status == DataRowStatus.OnlyRight ? cell.RightValue : cell.LeftValue)).Append("</td>");
                }
                sb.Append("</tr>\n");
            }
            sb.Append("</tbody></table>\n");
            if (truncated) sb.Append($"<p class=\"meta\">Only the first {MaxDataRows:N0} rows are listed.</p>\n");
        }

        AppendHtmlEnd(sb);
        return sb.ToString();
    }

    private static IReadOnlyList<DataComparisonRow> SelectRows(DataComparisonResult result, bool onlyDifferences, out bool truncated)
    {
        var rows = onlyDifferences ? result.Rows.Where(r => r.Status != DataRowStatus.Same).ToList() : result.Rows.ToList();
        truncated = rows.Count > MaxDataRows;
        return truncated ? rows.Take(MaxDataRows).ToList() : rows;
    }

    private static (int Removed, int Added) Count(IReadOnlyList<DiffLine> diff) =>
        (diff.Count(d => d.Kind == DiffLineKind.Removed), diff.Count(d => d.Kind == DiffLineKind.Added));

    private static (int Same, int Different, int OnlyLeft, int OnlyRight) Counts(DataComparisonResult r) =>
        (r.Rows.Count(x => x.Status == DataRowStatus.Same), r.Rows.Count(x => x.Status == DataRowStatus.Different),
         r.Rows.Count(x => x.Status == DataRowStatus.OnlyLeft), r.Rows.Count(x => x.Status == DataRowStatus.OnlyRight));

    private static void AppendDataSummaryMarkdown(StringBuilder sb, DataComparisonResult result)
    {
        var (same, different, onlyLeft, onlyRight) = Counts(result);
        sb.Append($"**{same:N0} same · {different:N0} different · {onlyLeft:N0} only in left · {onlyRight:N0} only in right**\n\n");
        foreach (var note in DataNotes(result)) sb.Append("- ").Append(Md(note)).Append('\n');
        if (result.ColumnDifferences.Count > 0)
        {
            sb.Append("\n| Column | Rows that differ |\n| --- | ---: |\n");
            foreach (var c in result.ColumnDifferences)
                sb.Append("| ").Append(Md(c.Column)).Append(" | ").Append(c.Count.ToString("N0", CultureInfo.InvariantCulture)).Append(" |\n");
        }
    }

    private static void AppendColumnDifferencesHtml(StringBuilder sb, DataComparisonResult result)
    {
        if (result.ColumnDifferences.Count == 0) return;
        sb.Append("<table class=\"data cols\"><thead><tr><th>Column</th><th>Rows that differ</th></tr></thead><tbody>\n");
        foreach (var c in result.ColumnDifferences)
            sb.Append("<tr><td>").Append(E(c.Column)).Append("</td><td>").Append(c.Count.ToString("N0", CultureInfo.InvariantCulture)).Append("</td></tr>\n");
        sb.Append("</tbody></table>\n");
    }

    /// <summary>Plain-language facts about how rows were matched and what may make the result incomplete;
    /// shown in the app and in both report formats.</summary>
    public static IReadOnlyList<string> DataNotes(DataComparisonResult result)
    {
        var notes = new List<string>
        {
            result.UsedFallbackKey
                ? "No primary key in common; rows matched by all compared columns, so a changed row shows as one row only in left plus one only in right."
                : $"Rows matched by {string.Join(", ", result.KeyColumns)}.",
            $"{result.LeftRowCount:N0} row(s) read from left, {result.RightRowCount:N0} from right; {result.ComparedColumns.Count} column(s) compared."
        };
        if (result.LeftTruncated || result.RightTruncated)
            notes.Add($"Row limit reached on {(result.LeftTruncated && result.RightTruncated ? "both sides" : result.LeftTruncated ? "the left" : "the right")}: results are partial, and rows past the limit may show as missing.");
        if (result.LeftDuplicateKeys + result.RightDuplicateKeys > 0)
            notes.Add($"Duplicate keys skipped: {result.LeftDuplicateKeys:N0} in left, {result.RightDuplicateKeys:N0} in right (only the first row per key is compared).");
        if (result.ColumnsOnlyInLeft.Count > 0)
            notes.Add("Columns only in left (not compared): " + string.Join(", ", result.ColumnsOnlyInLeft) + ".");
        if (result.ColumnsOnlyInRight.Count > 0)
            notes.Add("Columns only in right (not compared): " + string.Join(", ", result.ColumnsOnlyInRight) + ".");
        if (result.IgnoredColumns.Count > 0)
            notes.Add("Ignored columns: " + string.Join(", ", result.IgnoredColumns) + ".");
        return notes;
    }

    private static void AppendMarkdownHeader(StringBuilder sb, ReportSide left, ReportSide right, DateTimeOffset generatedAt)
    {
        sb.Append("| | |\n| --- | --- |\n");
        sb.Append("| Left (baseline) | ").Append(Md(left.Label)).Append(" |\n");
        sb.Append("| Right | ").Append(Md(right.Label)).Append(" |\n");
        sb.Append("| Generated | ").Append(generatedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)).Append(" |\n\n");
    }

    private static void AppendHtmlStart(StringBuilder sb, string title, ReportSide left, ReportSide right, DateTimeOffset generatedAt)
    {
        sb.Append("<!DOCTYPE html>\n<html lang=\"en\"><head><meta charset=\"utf-8\">")
          .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
          .Append("<title>").Append(E(title)).Append("</title><style>")
          .Append("""
            body{font:14px/1.45 system-ui,-apple-system,Segoe UI,Roboto,sans-serif;margin:24px;color:#1b1b1b;background:#fff}
            h1{font-size:20px;margin:0 0 12px}
            table{border-collapse:collapse}
            .sides td{padding:2px 12px 2px 0}.sides td:first-child{color:#666}
            .summary{font-weight:600;margin:16px 0 4px}.meta{color:#666;margin:4px 0 12px}ul.meta{padding-left:18px}
            table.cols{width:auto;margin-bottom:16px}
            table.diff,table.data{width:100%;border:1px solid #ddd;font-size:12.5px}
            table.diff th,table.data th{background:#f4f4f4;text-align:left;padding:4px 8px;border-bottom:1px solid #ddd;position:sticky;top:0}
            table.diff td,table.data td{padding:1px 8px;border-bottom:1px solid #f0f0f0;vertical-align:top}
            table.diff td.n{color:#999;text-align:right;width:40px}
            pre{margin:0;font:12.5px/1.4 ui-monospace,Cascadia Mono,Consolas,Menlo,monospace;white-space:pre-wrap}
            tr.del,span.del{background:#fde7e9}tr.add,span.add{background:#e6f4ea}tr.chg,span.chg{background:#fff4d6}
            td.cell-chg{background:#ffe8a3}.old{text-decoration:line-through;color:#a4262c}.new{color:#0b6a0b;font-weight:600}
            .null{color:#999;font-style:italic}
            @media (prefers-color-scheme:dark){body{background:#1e1e1e;color:#e6e6e6}table.diff th,table.data th{background:#2a2a2a}
            table.diff,table.data{border-color:#444}table.diff td,table.data td{border-color:#333}
            tr.del,span.del{background:#4a2327}tr.add,span.add{background:#1f3d2a}tr.chg,span.chg{background:#4a3f1c}td.cell-chg{background:#5c4a12}
            .old{color:#ff9a9f}.new{color:#8fe3a0}}
            """)
          .Append("</style></head><body>\n<h1>").Append(E(title)).Append("</h1>\n<table class=\"sides\">")
          .Append("<tr><td>Left (baseline)</td><td>").Append(E(left.Label)).Append("</td></tr>")
          .Append("<tr><td>Right</td><td>").Append(E(right.Label)).Append("</td></tr>")
          .Append("<tr><td>Generated</td><td>").Append(generatedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)).Append("</td></tr>")
          .Append("</table>\n");
    }

    private static void AppendHtmlEnd(StringBuilder sb) =>
        sb.Append("<p class=\"meta\">Generated by DB Explorer.</p>\n</body></html>\n");

    private static string E(string? text) => WebUtility.HtmlEncode(text ?? "");

    private static string V(object? value) =>
        value is null ? "<span class=\"null\">NULL</span>" : E(ResultExporter.FormatInvariant(value));

    private static string Md(string text) => text.Replace("|", "\\|").Replace("\r", "").Replace("\n", " ");
}
