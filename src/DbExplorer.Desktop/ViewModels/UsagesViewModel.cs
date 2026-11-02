using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using DbExplorer.Application.Query;
using DbExplorer.Core.Models;

namespace DbExplorer.Desktop.ViewModels;

public enum UsageRowKind
{
    /// <summary>"In this script · 4": the start of a group.</summary>
    Header,

    /// <summary>One place; double-click or Enter goes there when <see cref="UsageRow.Activate"/> is set.</summary>
    Entry,

    /// <summary>An explanation (nothing found, catalog not loaded…).</summary>
    Note
}

/// <summary>One line of the Find usages list.</summary>
public sealed record UsageRow(UsageRowKind Kind, string Text, string Location = "", object? Badge = null, Action? Activate = null)
{
    public bool IsHeader => Kind == UsageRowKind.Header;
    public bool IsEntry => Kind == UsageRowKind.Entry;
    public bool IsNote => Kind == UsageRowKind.Note;
    public bool HasBadge => Badge is not null;
    public bool CanOpen => Activate is not null;
}

/// <summary>
/// The Find usages window: where a name is used in the script, which views, routines and triggers use a table or
/// column (from their cached source), and the foreign keys on either side of it.
/// </summary>
public partial class UsagesViewModel(string title, string subtitle, IReadOnlyList<UsageRow> rows) : ViewModelBase
{
    public string Title { get; } = title;

    /// <summary>"4 in this script · 2 views and routines · 1 foreign key".</summary>
    public string Subtitle { get; } = subtitle;

    public IReadOnlyList<UsageRow> Rows { get; } = rows;

    [ObservableProperty] private UsageRow? _selected;

    public void Open(UsageRow? row) => row?.Activate?.Invoke();

    /// <summary>The list as plain text, to paste into a ticket or a review.</summary>
    public string AsText()
    {
        var sb = new StringBuilder(Title).Append('\n');
        foreach (var row in Rows)
        {
            if (row.IsHeader) sb.Append('\n').Append(row.Text).Append('\n');
            else if (row.IsNote) sb.Append("  ").Append(row.Text).Append('\n');
            else sb.Append("  ").Append(row.Location).Append(row.Location.Length > 0 ? ": " : "").Append(row.Text).Append('\n');
        }
        return sb.ToString();
    }

    // ----- Building the list -----

    public static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n:N0} {many}";

    /// <summary>The places in the editor's script, one row each, with the line they are on.</summary>
    public static IEnumerable<UsageRow> ScriptRows(string text, IReadOnlyList<SqlOccurrence> occurrences, Action<SqlOccurrence> goTo)
    {
        yield return new UsageRow(UsageRowKind.Header, $"In this script · {occurrences.Count:N0}");
        foreach (var o in occurrences)
        {
            var (line, lineText) = LineOf(text, o.Start);
            yield return new UsageRow(UsageRowKind.Entry, lineText, $"line {line}{(o.IsDefinition ? " · defined" : "")}", null, () => goTo(o));
        }
    }

    /// <summary>The views, routines and triggers, one row per place, grouped under one header.</summary>
    public static IEnumerable<UsageRow> ModuleRows(IReadOnlyList<ModuleUsage> usages, Action<ModuleUsage> open, bool truncated)
    {
        var objects = usages.Select(u => (u.Module.Database, u.Module.Schema, u.Module.Name)).Distinct().Count();
        yield return new UsageRow(UsageRowKind.Header, $"Used by views, routines and triggers · {Count(objects, "object", "objects")}");
        if (usages.Count == 0)
            yield return new UsageRow(UsageRowKind.Note, "None of the loaded views, routines and triggers use it.");
        foreach (var u in usages)
            yield return new UsageRow(UsageRowKind.Entry, u.LineText, $"{u.Module.Schema}.{u.Module.Name} · line {u.Line}", u.Module.Type, () => open(u));
        if (truncated) yield return new UsageRow(UsageRowKind.Note, "Only the first places are listed.");
    }

    public static IEnumerable<UsageRow> KeyRows(IReadOnlyList<DbForeignKey> keys)
    {
        if (keys.Count == 0) yield break;
        yield return new UsageRow(UsageRowKind.Header, $"Foreign keys · {keys.Count:N0}");
        foreach (var k in keys.OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase))
            yield return new UsageRow(UsageRowKind.Entry,
                $"{k.Schema}.{k.Table} ({k.Columns}) → {k.ReferencedSchema}.{k.ReferencedTable} ({k.ReferencedColumns})" +
                (k.IsVirtual ? "  · virtual" : k.IsDisabled ? "  · disabled" : ""),
                k.Name, "Foreign key");
    }

    public static (int Line, string Text) LineOf(string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        var line = 1;
        for (var i = 0; i < offset; i++) if (text[i] == '\n') line++;
        var start = offset == 0 ? 0 : text.LastIndexOf('\n', offset - 1) + 1;
        var end = text.IndexOf('\n', offset);
        if (end < 0) end = text.Length;
        var lineText = text[start..end].Trim();
        return (line, lineText.Length > 240 ? lineText[..240] + "…" : lineText);
    }
}
