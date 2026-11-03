using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.Search;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Models;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>A run of text in a result row; <see cref="IsMatch"/> marks what the search found.</summary>
public sealed record HighlightSegment(string Text, bool IsMatch);

public enum MetadataSearchRowKind
{
    /// <summary>The object: type, schema.name (highlighted when the name matched) and how many hits it has.</summary>
    Object,

    /// <summary>A column of the object whose name matched.</summary>
    Column,

    /// <summary>A line of the object's source code that matched.</summary>
    Code
}

/// <summary>One line of the results list: an object, or one of its column or code hits under it.</summary>
public sealed partial class MetadataSearchRow : ObservableObject
{
    public MetadataSearchRow(MetadataSearchRowKind kind, MetadataSearchGroup group, IReadOnlyList<HighlightSegment> segments,
        string label = "", string hint = "", MetadataSearchResult? hit = null)
    {
        Kind = kind;
        Group = group;
        Segments = segments;
        Label = label;
        Hint = hint;
        Hit = hit;
    }

    public MetadataSearchRowKind Kind { get; }
    public MetadataSearchGroup Group { get; }
    public MetadataSearchResult? Hit { get; }

    /// <summary>The highlighted text: schema.name for an object, the column name, or the code line.</summary>
    public IReadOnlyList<HighlightSegment> Segments { get; }

    /// <summary>"line 12" or "column"; empty on an object row.</summary>
    public string Label { get; }

    /// <summary>The column's data type, or on an object row what it matched in ("name · 2 columns · 3 lines").</summary>
    public string Hint { get; }

    public bool IsObject => Kind == MetadataSearchRowKind.Object;
    public bool IsHit => Kind != MetadataSearchRowKind.Object;
    public bool IsCode => Kind == MetadataSearchRowKind.Code;
    public DbObjectType Type => Group.ObjectType;
    public bool HasChildren => Group.Columns.Count + Group.CodeLines.Count > 0;

    [ObservableProperty] private bool _isExpanded = true;

    /// <summary>"▾" or "▸" on an object row with hits under it.</summary>
    public string Chevron => !HasChildren ? "" : IsExpanded ? "▾" : "▸";

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(Chevron));

    public string Text => string.Concat(Segments.Select(s => s.Text));

    /// <summary>What a screen reader reads for the row.</summary>
    public string AccessibleText => Kind switch
    {
        MetadataSearchRowKind.Object => $"{Group.ObjectType} {Group.FullName}, {Hint}",
        MetadataSearchRowKind.Column => $"Column {Text} {Hint}",
        _ => $"{Label}: {Text}"
    };
}

/// <summary>One choice of the type or schema filter, with how many objects it would show.</summary>
public sealed record MetadataSearchFilterOption(string Label, MetadataTypeFilter Type = MetadataTypeFilter.All, string? Schema = null)
{
    public override string ToString() => Label;
}

/// <summary>
/// The "Search names &amp; code" tab: searches object names, column names and source code in the local metadata
/// snapshot as you type (no load on the server). Results are grouped by object, best match first, with a preview of
/// the object's source on the right that has every hit highlighted.
/// </summary>
public partial class MetadataSearchViewModel(MetadataSearchService service, DefinitionService definitions) : ViewModelBase, ISessionAware
{
    private const int MaxResults = 5000;

    /// <summary>Typing waits this long for the next key before searching.</summary>
    public static TimeSpan TypingDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    private DatabaseSession? _session;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _previewCts;
    private MetadataSearchQuery? _lastQuery;
    private Regex? _lastRegex;
    private IReadOnlyList<MetadataSearchGroup> _groups = [];
    private readonly HashSet<(string, string, string, DbObjectType)> _collapsed = [];
    private (string, string, string, DbObjectType)? _previewKey;
    private bool _rebuilding;

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private bool _matchCase;
    [ObservableProperty] private bool _wholeWord;
    [ObservableProperty] private bool _useRegex;

    // What to show; the search always covers all three so these filter without searching again.
    [ObservableProperty] private bool _showNames = true;
    [ObservableProperty] private bool _showColumns = true;
    [ObservableProperty] private bool _showCode = true;

    [ObservableProperty] private int _nameCount;
    [ObservableProperty] private int _columnCount;
    [ObservableProperty] private int _codeCount;

    [ObservableProperty] private IReadOnlyList<MetadataSearchFilterOption> _typeOptions = [new(MetadataTypeFilter.All.Label())];
    [ObservableProperty] private MetadataSearchFilterOption? _selectedTypeOption;
    [ObservableProperty] private IReadOnlyList<MetadataSearchFilterOption> _schemaOptions = [new("All schemas")];
    [ObservableProperty] private MetadataSearchFilterOption? _selectedSchemaOption;

    [ObservableProperty] private IReadOnlyList<MetadataSearchRow> _rows = [];
    [ObservableProperty] private MetadataSearchRow? _selectedRow;

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isSearching;

    /// <summary>Set once a search ran; the empty-state help shows until then.</summary>
    [ObservableProperty] private bool _hasSearched;

    /// <summary>"Nothing matches" help, when a search found nothing to show.</summary>
    [ObservableProperty] private string _emptyMessage = "";

    // ----- Preview (right pane) -----

    [ObservableProperty] private string _previewTitle = "";
    [ObservableProperty] private DbObjectType? _previewType;
    [ObservableProperty] private string _previewText = "";
    [ObservableProperty] private string _previewNote = "";

    /// <summary>Every hit in <see cref="PreviewText"/>, to highlight.</summary>
    [ObservableProperty] private IReadOnlyList<(int Start, int Length)> _previewMatches = [];

    /// <summary>"12 lines · 4 highlighted".</summary>
    [ObservableProperty] private string _previewSummary = "";

    /// <summary>The 1-based line of the selected hit in <see cref="PreviewText"/>, to scroll to and mark.</summary>
    [ObservableProperty] private int _previewLine;

    /// <summary>The view scrolls to <see cref="PreviewLine"/> on this, also when the line number stays the same.</summary>
    public event Action? PreviewLineRequested;

    public bool HasPreview => PreviewType is not null;
    public bool ShowEmptyHelp => !HasSearched;
    public bool ShowNoResults => HasSearched && Rows.Count == 0 && !IsSearching;
    public bool HasResults => Rows.Count > 0;
    public bool IsTableLikePreview => PreviewType is DbObjectType.Table or DbObjectType.View
        or DbObjectType.MaterializedView or DbObjectType.ForeignTable;

    /// <summary>The navigator / Objects tab should show this object.</summary>
    public event Action<DbObject>? ShowObjectRequested;

    /// <summary>Open (sql, database, title, caret offset) in a new Query tab.</summary>
    public event Action<string, string?, string, int?>? OpenSqlRequested;

    public event Action<string>? CopyRequested;

    /// <summary>The view puts the caret in the search box (Ctrl+Shift+N).</summary>
    public event Action? FocusRequested;

    public void RequestFocus() => FocusRequested?.Invoke();

    public void Attach(DatabaseSession? session)
    {
        _searchCts?.Cancel();
        _previewCts?.Cancel();
        _session = session;
        _groups = [];
        _collapsed.Clear();
        HasSearched = false;
        NameCount = ColumnCount = CodeCount = 0;
        TypeOptions = [new(MetadataTypeFilter.All.Label())];
        SchemaOptions = [new("All schemas")];
        Rows = [];
        ClearPreview();
        Status = IdleStatus();
        // A search typed before connecting runs on the new snapshot.
        if (session is not null && Query.Trim().Length > 0) _ = SearchAsync();
    }

    private string IdleStatus() =>
        _session is { } s ? $"{s.Snapshot.Objects.Count:N0} objects · metadata from {s.Snapshot.RefreshedAt:g}" : "";

    // ----- Searching -----

    partial void OnQueryChanged(string value) => ScheduleSearch();
    partial void OnMatchCaseChanged(bool value) => ScheduleSearch(immediate: true);
    partial void OnWholeWordChanged(bool value) => ScheduleSearch(immediate: true);
    partial void OnUseRegexChanged(bool value) => ScheduleSearch(immediate: true);

    private void ScheduleSearch(bool immediate = false)
    {
        _searchCts?.Cancel();
        if (Query.Trim().Length == 0)
        {
            _groups = [];
            _lastQuery = null;
            HasSearched = false;
            NameCount = ColumnCount = CodeCount = 0;
            Rows = [];
            ClearPreview();
            Status = IdleStatus();
            return;
        }
        // One letter matches nearly everything; it waits for Enter.
        if (!immediate && Query.Trim().Length < 2) return;
        _ = SearchAsync(immediate ? TimeSpan.Zero : TypingDelay);
    }

    /// <summary>Searches now (Enter, the Search button).</summary>
    [RelayCommand]
    private Task SearchAsync() => SearchAsync(TimeSpan.Zero);

    private async Task SearchAsync(TimeSpan delay)
    {
        if (_session is null || string.IsNullOrWhiteSpace(Query)) return;

        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        var ct = cts.Token;
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);

            var query = new MetadataSearchQuery(Query.Trim(), MetadataSearchScope.All, MatchCase, WholeWord, UseRegex, MaxResults);
            var snapshot = _session.Snapshot;
            var sw = Stopwatch.StartNew();
            IsSearching = true;

            var (results, groups, regex) = await Task.Run(() =>
            {
                var found = service.Search(snapshot, query, ct);
                return (found, MetadataSearchGrouping.Group(found, query), SearchTextMatcher.BuildRegex(query));
            }, ct);
            if (ct.IsCancellationRequested) return;

            _lastQuery = query;
            _lastRegex = regex;
            _groups = groups;
            _collapsed.Clear();
            NameCount = groups.Count(g => g.NameMatch is not null);
            ColumnCount = groups.Sum(g => g.Columns.Count);
            CodeCount = groups.Sum(g => g.CodeLines.Count);
            BuildFilterOptions();
            HasSearched = true;
            RebuildRows(selectFirst: true);

            var objects = groups.Count == 1 ? "1 object" : $"{groups.Count:N0} objects";
            var matches = results.Count == 1 ? "1 match" : $"{results.Count:N0} matches";
            Status = $"{matches} in {objects} · {sw.ElapsedMilliseconds:N0} ms" +
                     (results.Count >= MaxResults ? " · limit reached, type more to narrow it" : "");
            EmptyMessage = results.Count == 0
                ? $"Nothing is called “{query.Text}”, has such a column or mentions it in code."
                : "Nothing left after the filters above. Turn on more of Names, Columns and Code, or pick All types / All schemas.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (ArgumentException ex)
        {
            // An unfinished regular expression while typing: keep the last results, say what is wrong.
            Status = "Not a valid regular expression yet: " + ex.Message;
        }
        catch (Exception ex)
        {
            Status = "Search failed: " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts))
            {
                IsSearching = false;
                OnPropertyChanged(nameof(ShowNoResults));
            }
        }
    }

    private void BuildFilterOptions()
    {
        var previousType = SelectedTypeOption?.Type ?? MetadataTypeFilter.All;
        var previousSchema = SelectedSchemaOption?.Schema;

        var types = new List<MetadataSearchFilterOption> { new($"{MetadataTypeFilter.All.Label()} · {_groups.Count:N0}") };
        foreach (var filter in Enum.GetValues<MetadataTypeFilter>().Skip(1))
        {
            var count = _groups.Count(g => filter.Matches(g.ObjectType));
            if (count > 0) types.Add(new($"{filter.Label()} · {count:N0}", filter));
        }

        var schemas = new List<MetadataSearchFilterOption> { new("All schemas") };
        schemas.AddRange(_groups.GroupBy(g => g.Schema, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new MetadataSearchFilterOption($"{g.Key} · {g.Count():N0}", Schema: g.Key)));

        _rebuilding = true;
        try
        {
            TypeOptions = types;
            SelectedTypeOption = types.FirstOrDefault(t => t.Type == previousType) ?? types[0];
            SchemaOptions = schemas;
            SelectedSchemaOption = schemas.FirstOrDefault(s => previousSchema is not null && string.Equals(s.Schema, previousSchema, StringComparison.OrdinalIgnoreCase)) ?? schemas[0];
        }
        finally
        {
            _rebuilding = false;
        }
    }

    partial void OnShowNamesChanged(bool value) => RebuildRows();
    partial void OnShowColumnsChanged(bool value) => RebuildRows();
    partial void OnShowCodeChanged(bool value) => RebuildRows();

    partial void OnSelectedTypeOptionChanged(MetadataSearchFilterOption? value)
    {
        if (!_rebuilding) RebuildRows();
    }

    partial void OnSelectedSchemaOptionChanged(MetadataSearchFilterOption? value)
    {
        if (!_rebuilding) RebuildRows();
    }

    /// <summary>The visible rows: each object that passes the filters, then (unless collapsed) its column and code hits.</summary>
    private void RebuildRows(bool selectFirst = false)
    {
        var regex = _lastRegex;
        var type = SelectedTypeOption?.Type ?? MetadataTypeFilter.All;
        var schema = SelectedSchemaOption?.Schema;
        var multipleDatabases = _groups.Select(g => g.Database).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any();
        var previous = SelectedRow;

        var rows = new List<MetadataSearchRow>();
        foreach (var g in _groups)
        {
            if (!type.Matches(g.ObjectType)) continue;
            if (schema is not null && !string.Equals(g.Schema, schema, StringComparison.OrdinalIgnoreCase)) continue;

            var name = ShowNames && g.NameMatch is not null;
            var columns = ShowColumns ? g.Columns : [];
            var code = ShowCode ? g.CodeLines : [];
            if (!name && columns.Count == 0 && code.Count == 0) continue;

            var key = Key(g);
            var hint = Describe(name, columns.Count, code.Count);
            if (multipleDatabases && g.Database.Length > 0) hint = $"{g.Database} · {hint}";
            var nameSegments = new List<HighlightSegment> { new(g.Schema + ".", false) };
            nameSegments.AddRange(name && regex is not null ? Highlight(regex, g.ObjectName) : [new(g.ObjectName, false)]);
            var header = new MetadataSearchRow(MetadataSearchRowKind.Object, g, nameSegments, hint: hint)
            {
                IsExpanded = !_collapsed.Contains(key)
            };
            rows.Add(header);
            if (!header.IsExpanded) continue;

            foreach (var c in columns)
            {
                var columnName = c.Column ?? c.Detail;
                var dataType = c.Column is not null && c.Detail.Length > columnName.Length ? c.Detail[columnName.Length..].Trim() : "";
                rows.Add(new MetadataSearchRow(MetadataSearchRowKind.Column, g,
                    regex is not null ? Highlight(regex, columnName) : [new(columnName, false)], "column", dataType, c));
            }

            foreach (var line in code)
                rows.Add(new MetadataSearchRow(MetadataSearchRowKind.Code, g,
                    regex is not null ? Highlight(regex, line.Detail) : [new(line.Detail, false)], $"line {line.Line}", hit: line));
        }

        Rows = rows;

        // Keep the selection on the same place when it is still shown, otherwise the first row.
        var keep = previous is null ? null : rows.FirstOrDefault(r =>
            Key(r.Group) == Key(previous.Group) && r.Kind == previous.Kind && Equals(r.Hit, previous.Hit))
            ?? rows.FirstOrDefault(r => r.IsObject && Key(r.Group) == Key(previous.Group));
        SelectedRow = selectFirst || keep is null ? rows.FirstOrDefault() : keep;
        if (SelectedRow is null) ClearPreview();
    }

    private static string Describe(bool name, int columns, int lines)
    {
        var parts = new List<string>();
        if (name) parts.Add("name");
        if (columns > 0) parts.Add(columns == 1 ? "1 column" : $"{columns:N0} columns");
        if (lines > 0) parts.Add(lines == 1 ? "1 line" : $"{lines:N0} lines");
        return string.Join(" · ", parts);
    }

    private static IReadOnlyList<HighlightSegment> Highlight(Regex regex, string text) =>
        SearchTextMatcher.Segments(regex, text).Select(s => new HighlightSegment(s.Text, s.IsMatch)).ToList();

    private static (string, string, string, DbObjectType) Key(MetadataSearchGroup g) => (g.Database, g.Schema, g.ObjectName, g.ObjectType);

    /// <summary>Collapses or expands an object's hits (the chevron, ← and →).</summary>
    public void SetExpanded(MetadataSearchRow row, bool expanded)
    {
        if (!row.IsObject || !row.HasChildren || row.IsExpanded == expanded) return;
        if (expanded) _collapsed.Remove(Key(row.Group));
        else _collapsed.Add(Key(row.Group));
        SelectedRow = row;
        RebuildRows();
        SelectedRow = Rows.FirstOrDefault(r => r.IsObject && Key(r.Group) == Key(row.Group));
    }

    [RelayCommand]
    private void ToggleExpanded(MetadataSearchRow? row)
    {
        if (row is not null) SetExpanded(row, !row.IsExpanded);
    }

    [RelayCommand]
    private void CollapseAll()
    {
        foreach (var g in _groups) _collapsed.Add(Key(g));
        RebuildRows();
    }

    [RelayCommand]
    private void ExpandAll()
    {
        _collapsed.Clear();
        RebuildRows();
    }

    /// <summary>Esc in the search box.</summary>
    [RelayCommand]
    private void Clear() => Query = "";

    // ----- Preview -----

    partial void OnSelectedRowChanged(MetadataSearchRow? value)
    {
        if (value is not null) _ = LoadPreviewAsync(value);
    }

    partial void OnPreviewTypeChanged(DbObjectType? value)
    {
        OnPropertyChanged(nameof(HasPreview));
        OnPropertyChanged(nameof(IsTableLikePreview));
        OpenInQueryCommand.NotifyCanExecuteChanged();
        ShowInObjectsCommand.NotifyCanExecuteChanged();
        SelectRowsCommand.NotifyCanExecuteChanged();
        CopyNameCommand.NotifyCanExecuteChanged();
    }

    partial void OnRowsChanged(IReadOnlyList<MetadataSearchRow> value)
    {
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(ShowNoResults));
    }

    partial void OnHasSearchedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowEmptyHelp));
        OnPropertyChanged(nameof(ShowNoResults));
    }

    private void ClearPreview()
    {
        _previewCts?.Cancel();
        _previewKey = null;
        PreviewType = null;
        PreviewTitle = "";
        PreviewText = "";
        PreviewNote = "";
        PreviewSummary = "";
        PreviewMatches = [];
        PreviewLine = 0;
    }

    private async Task LoadPreviewAsync(MetadataSearchRow row)
    {
        if (_session is not { } session) return;
        var g = row.Group;
        var key = Key(g);

        if (_previewKey != key)
        {
            _previewCts?.Cancel();
            var cts = _previewCts = new CancellationTokenSource();
            _previewKey = key;
            PreviewTitle = g.FullName;
            PreviewType = g.ObjectType;
            PreviewNote = "";

            var text = MetadataSearchGrouping.ModuleText(session.Snapshot, g.Database, g.Schema, g.ObjectName, g.ObjectType);
            if (text is null)
            {
                try
                {
                    text = (await definitions.GetDefinitionAsync(session, g.ToObject(), cts.Token))?.Replace("\r\n", "\n");
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    PreviewNote = "Could not load the definition: " + ex.Message;
                }
                if (cts.IsCancellationRequested) return;
            }

            if (string.IsNullOrEmpty(text) && PreviewNote.Length == 0)
                PreviewNote = "The server does not show this object's source (encrypted, or no permission).";
            PreviewText = text ?? "";
            PreviewMatches = _lastRegex is { } regex && PreviewText.Length > 0 ? SearchTextMatcher.Spans(regex, PreviewText) : [];
            var lineCount = PreviewText.Length == 0 ? 0 : PreviewText.Count(ch => ch == '\n') + 1;
            PreviewSummary = lineCount == 0 ? ""
                : $"{(lineCount == 1 ? "1 line" : $"{lineCount:N0} lines")} · {(PreviewMatches.Count == 1 ? "1 hit" : $"{PreviewMatches.Count:N0} hits")}";
        }

        PreviewLine = LineOf(row, PreviewText);
        PreviewLineRequested?.Invoke();
    }

    /// <summary>The line to show for a row: the code hit's own line, the column's line in a table script, or the
    /// first highlighted line for an object.</summary>
    private int LineOf(MetadataSearchRow row, string text)
    {
        if (text.Length == 0) return 0;
        if (row.Hit is { Line: { } line }) return line;
        if (row.Kind == MetadataSearchRowKind.Column && row.Hit?.Column is { } column)
        {
            var pattern = new Regex($@"(?<![\w$#@]){Regex.Escape(column)}(?![\w$#@])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var lines = text.Split('\n');
            // Skip the CREATE line, where a column named like the table would match the table's name.
            for (var i = 1; i < lines.Length; i++)
                if (pattern.IsMatch(lines[i])) return i + 1;
        }
        if (PreviewMatches.Count > 0) return LineAt(text, PreviewMatches[0].Start);
        return 1;
    }

    private static int LineAt(string text, int offset)
    {
        var line = 1;
        for (var i = 0; i < offset && i < text.Length; i++)
            if (text[i] == '\n') line++;
        return line;
    }

    private static int OffsetOfLine(string text, int line)
    {
        var offset = 0;
        for (var current = 1; current < line; current++)
        {
            var next = text.IndexOf('\n', offset);
            if (next < 0) break;
            offset = next + 1;
        }
        return offset;
    }

    // ----- Actions -----

    private DbObject? SelectedObject => SelectedRow?.Group.ToObject();

    private bool CanAct() => SelectedRow is not null && HasPreview;

    /// <summary>Double-click or Enter on a row: a code line opens the source there, anything else the object.</summary>
    public void Activate(MetadataSearchRow? row)
    {
        if (row is null) return;
        if (!ReferenceEquals(SelectedRow, row)) SelectedRow = row;
        if (row.IsCode) OpenAtLine(row);
        else if (SelectedObject is { } obj) ShowObjectRequested?.Invoke(obj);
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private void ShowInObjects()
    {
        if (SelectedObject is { } obj) ShowObjectRequested?.Invoke(obj);
    }

    /// <summary>The source (or a table's CREATE script) in a new Query tab, caret on the selected hit.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private void OpenInQuery()
    {
        if (SelectedRow is { } row) OpenAtLine(row);
    }

    private void OpenAtLine(MetadataSearchRow row)
    {
        if (PreviewText.Length == 0) return;
        var offset = PreviewLine > 0 ? OffsetOfLine(PreviewText, PreviewLine) : 0;
        OpenSqlRequested?.Invoke(PreviewText, NullIfEmpty(row.Group.Database), row.Group.ObjectName, offset);
    }

    /// <summary>A SELECT of the table or view's first rows in a new Query tab.</summary>
    [RelayCommand(CanExecute = nameof(CanSelectRows))]
    private void SelectRows()
    {
        if (_session is not { } session || SelectedRow?.Group is not { } g) return;
        var obj = g.ToObject();
        var sql = ObjectScripts.SelectScript(session.Provider.ProviderKey, obj, session.Snapshot.ColumnsOf(g.Database, g.Schema, g.ObjectName));
        OpenSqlRequested?.Invoke(sql, NullIfEmpty(g.Database), g.ObjectName, null);
    }

    private bool CanSelectRows() => CanAct() && IsTableLikePreview;

    [RelayCommand(CanExecute = nameof(CanAct))]
    private void CopyName()
    {
        if (SelectedRow is not { } row) return;
        CopyRequested?.Invoke(row.Kind == MetadataSearchRowKind.Column && row.Hit?.Column is { } column ? column : row.Group.FullName);
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(SelectedRow))
        {
            OpenInQueryCommand.NotifyCanExecuteChanged();
            ShowInObjectsCommand.NotifyCanExecuteChanged();
            SelectRowsCommand.NotifyCanExecuteChanged();
            CopyNameCommand.NotifyCanExecuteChanged();
        }
        else if (e.PropertyName == nameof(IsSearching)) OnPropertyChanged(nameof(ShowNoResults));
    }
}
