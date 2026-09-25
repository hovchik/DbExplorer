using CommunityToolkit.Mvvm.ComponentModel;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.Search;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Models;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>A run of text within a highlighted line; <see cref="IsMatch"/> marks a search hit.</summary>
public sealed record HighlightSegment(string Text, bool IsMatch);

/// <summary>One line of the displayed text, split into highlighted/plain segments.</summary>
public sealed record HighlightLine(int Number, IReadOnlyList<HighlightSegment> Segments);

public partial class SearchResultDetailViewModel : ViewModelBase
{
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private IReadOnlyList<HighlightLine> _lines = [];
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private int _matchCount;

    public async Task LoadAsync(
        DefinitionService definitions, DatabaseSession session,
        MetadataSearchResult result, MetadataSearchQuery query, CancellationToken ct = default)
    {
        Title = $"{result.Schema}.{result.ObjectName} ({result.ObjectType})";

        var obj = session.Snapshot.Objects.FirstOrDefault(o =>
            o.Database == result.Database && o.Schema == result.Schema && o.Name == result.ObjectName)
            ?? new DbObject { Database = result.Database, Schema = result.Schema, Name = result.ObjectName, Type = result.ObjectType };

        string? text;
        try
        {
            text = await definitions.GetDefinitionAsync(session, obj, ct);
        }
        catch (Exception ex)
        {
            Status = "Error: " + ex.Message;
            return;
        }

        if (string.IsNullOrEmpty(text))
        {
            Status = "Definition is not available (encrypted object or insufficient permissions).";
            return;
        }

        var regex = SearchTextMatcher.BuildRegex(query);
        var rawLines = text.Replace("\r\n", "\n").Split('\n');
        var lines = new List<HighlightLine>(rawLines.Length);
        var matches = 0;

        for (var i = 0; i < rawLines.Length; i++)
        {
            var line = rawLines[i];
            var segments = new List<HighlightSegment>();
            var lastEnd = 0;

            foreach (System.Text.RegularExpressions.Match m in regex.Matches(line))
            {
                if (m.Length == 0) continue;
                if (m.Index > lastEnd) segments.Add(new HighlightSegment(line[lastEnd..m.Index], false));
                segments.Add(new HighlightSegment(line[m.Index..(m.Index + m.Length)], true));
                lastEnd = m.Index + m.Length;
                matches++;
            }

            if (lastEnd < line.Length) segments.Add(new HighlightSegment(line[lastEnd..], false));
            if (segments.Count == 0) segments.Add(new HighlightSegment("", false));

            lines.Add(new HighlightLine(i + 1, segments));
        }

        Lines = lines;
        MatchCount = matches;
        Status = matches > 0 ? $"{matches:N0} match(es) for \"{query.Text}\"" : $"No occurrences of \"{query.Text}\" found in this text.";

        if (result.Line is { } targetLine) ScrollToLine?.Invoke(targetLine);
    }

    /// <summary>Raised once loading is done so the view can scroll to the originating match line.</summary>
    public event Action<int>? ScrollToLine;
}
