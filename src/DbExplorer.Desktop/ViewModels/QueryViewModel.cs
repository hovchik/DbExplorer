using System.Collections.ObjectModel;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Export;
using DbExplorer.Application.Query;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Connections;
using DbExplorer.Desktop.Services;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>A database offered as a multi-database run target.</summary>
public sealed partial class DatabaseChoice(string name) : ObservableObject
{
    public string Name { get; } = name;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// One query tab of the Query workspace: its editor document (text + undo history), results and messages.
/// </summary>
public partial class QueryViewModel : ViewModelBase, ISessionAware
{
    private const int TimeoutSeconds = 60;
    private readonly QueryExecutionService queryService;
    private readonly MultiDatabaseQueryService multiQuery;
    private readonly ScriptStore scripts;
    private readonly IDialogService dialogs;
    private DatabaseSession? _session;
    private SqlCompletionEngine? _completion;
    private CancellationTokenSource? _runCts;
    private bool _syncingDocument;

    public QueryViewModel(QueryExecutionService queryService, MultiDatabaseQueryService multiQuery, ScriptStore scripts, IDialogService dialogs)
    {
        this.queryService = queryService;
        this.multiQuery = multiQuery;
        this.scripts = scripts;
        this.dialogs = dialogs;
        Document.TextChanged += (_, _) =>
        {
            if (_syncingDocument) return;
            _syncingDocument = true;
            Sql = Document.Text;
            IsDirty = true;
            _syncingDocument = false;
        };
    }

    /// <summary>The editor's text and undo history; kept here so switching tabs keeps each tab's state.</summary>
    public TextDocument Document { get; } = new();

    [ObservableProperty] private string _title = "Query";
    [ObservableProperty] private string? _filePath;
    [ObservableProperty] private bool _isDirty;

    public string Header => (IsDirty && FilePath is not null ? "● " : "") + Title;

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(Header));
    partial void OnIsDirtyChanged(bool value) => OnPropertyChanged(nameof(Header));

    /// <summary>Provider of the connected session ("SqlServer"/"Postgres"), for dialect-aware editor features.</summary>
    public string? ProviderKey => _session?.Provider.ProviderKey;

    [ObservableProperty] private string _sql = "";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private IReadOnlyList<ResultSetView> _resultSets = [];

    [ObservableProperty] private bool _runOnMultipleDatabases;
    [ObservableProperty] private string _databaseFilter = "";
    [ObservableProperty] private IReadOnlyList<DatabaseChoice> _visibleDatabases = [];
    [ObservableProperty] private string _databaseListStatus = "";
    [ObservableProperty] private decimal _parallelism = 4;
    private IReadOnlyList<DatabaseChoice> _allDatabases = [];

    public ObservableCollection<string> Messages { get; } = [];

    public ObservableCollection<CompletionItem> Suggestions { get; } = [];

    public string TargetSummary
    {
        get
        {
            var n = _allDatabases.Count(d => d.IsSelected);
            return n == 0 ? "Pick databases…" : n == 1 ? "1 database" : $"{n} databases";
        }
    }

    public void Attach(DatabaseSession? session)
    {
        if (_session is not null) _session.SnapshotChanged -= OnSnapshotChanged;
        _session = session;
        if (_session is not null) _session.SnapshotChanged += OnSnapshotChanged;
        ResultSets = [];
        Messages.Clear();
        Status = "";
        RunOnMultipleDatabases = false;
        SetDatabases([]);
        ExecuteCommand.NotifyCanExecuteChanged();
        RebuildCompletion();
    }

    private void OnSnapshotChanged(object? sender, EventArgs e) => RebuildCompletion();

    private void RebuildCompletion()
    {
        _completion = _session is null ? null : new SqlCompletionEngine(_session.Snapshot, _session.Provider.QuoteIdentifier, _session.Provider.ProviderKey);
        OnPropertyChanged(nameof(ProviderKey));
    }

    /// <summary>Completion for the caret position; see <see cref="SqlCompletionEngine"/>.</summary>
    public CompletionResult GetCompletions(string text, int caret, bool explicitRequest) =>
        _completion?.Complete(text, caret, explicitRequest, max: 200) ?? CompletionResult.Empty;

    /// <summary>Hover text for the identifier at <paramref name="offset"/>.</summary>
    public string? Describe(string text, int offset) => _completion?.Describe(text, offset);

    /// <summary>Replaces the whole text (open file, history) as one undoable edit.</summary>
    public void SetText(string text, bool markClean)
    {
        Document.Text = text;
        IsDirty = !markClean;
    }

    private bool CanExecute => _session is not null && !IsRunning && !string.IsNullOrWhiteSpace(Sql) &&
                               (!RunOnMultipleDatabases || _allDatabases.Any(d => d.IsSelected));

    partial void OnSqlChanged(string value)
    {
        if (!_syncingDocument && Document.Text != value)
        {
            _syncingDocument = true;
            Document.Text = value;
            _syncingDocument = false;
        }
        ExecuteCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsRunningChanged(bool value)
    {
        ExecuteCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    partial void OnRunOnMultipleDatabasesChanged(bool value)
    {
        ExecuteCommand.NotifyCanExecuteChanged();
        if (value && _allDatabases.Count == 0) _ = LoadDatabasesAsync();
    }

    partial void OnDatabaseFilterChanged(string value) => ApplyDatabaseFilter();

    [RelayCommand]
    private async Task LoadDatabasesAsync()
    {
        if (_session is not { } session) return;
        DatabaseListStatus = "Loading databases…";
        try
        {
            var names = await session.Factory.ListDatabasesAsync(session.Profile);
            if (!ReferenceEquals(session, _session)) return;
            var current = session.Profile.Database;
            SetDatabases(names.Select(n => new DatabaseChoice(n)
            {
                IsSelected = string.Equals(n, current, StringComparison.OrdinalIgnoreCase)
            }).ToList());
            DatabaseListStatus = $"{names.Count:N0} database(s) on the server";
        }
        catch (Exception ex)
        {
            DatabaseListStatus = "Could not list databases: " + ex.Message;
        }
    }

    [RelayCommand]
    private void SelectAllDatabases(bool select)
    {
        foreach (var d in VisibleDatabases) d.IsSelected = select;
    }

    private void SetDatabases(IReadOnlyList<DatabaseChoice> databases)
    {
        foreach (var d in _allDatabases) d.PropertyChanged -= OnChoiceChanged;
        _allDatabases = databases;
        foreach (var d in _allDatabases) d.PropertyChanged += OnChoiceChanged;
        ApplyDatabaseFilter();
        OnPropertyChanged(nameof(TargetSummary));
        ExecuteCommand.NotifyCanExecuteChanged();
    }

    private void OnChoiceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(TargetSummary));
        ExecuteCommand.NotifyCanExecuteChanged();
    }

    private void ApplyDatabaseFilter()
    {
        var f = DatabaseFilter.Trim();
        VisibleDatabases = f.Length == 0
            ? _allDatabases
            : _allDatabases.Where(d => d.Name.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _runCts?.Cancel();

    /// <summary>Runs <paramref name="part"/> (a selection or the statement at the caret) or, when null/blank, the whole script.</summary>
    [RelayCommand(CanExecute = nameof(CanExecute))]
    private async Task ExecuteAsync(string? part)
    {
        if (_session is not { } session) return;

        var targets = RunOnMultipleDatabases ? _allDatabases.Where(d => d.IsSelected).Select(d => d.Name).ToList() : [];
        var sql = string.IsNullOrWhiteSpace(part) ? Sql : part;
        if (string.IsNullOrWhiteSpace(sql)) return;

        if (queryService.IsPotentiallyDestructive(sql) &&
            !await ConfirmDestructiveAsync(session.Profile, Math.Max(1, targets.Count)))
            return;

        _runCts?.Dispose();
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;
        IsRunning = true;
        Messages.Clear();
        try
        {
            if (RunOnMultipleDatabases)
                await RunOnDatabasesAsync(session, sql, targets, ct);
            else
                await RunOnceAsync(session, sql, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Status = "Cancelled.";
        }
        catch (Exception ex)
        {
            Status = "Error: " + ex.Message;
            await SafeAppendHistoryAsync(sql, succeeded: false, error: ex.Message);
        }
        finally
        {
            IsRunning = false;
        }
    }

    private async Task RunOnceAsync(DatabaseSession session, string sql, CancellationToken ct)
    {
        Status = "Running…";
        var result = await queryService.ExecuteScriptAsync(session, sql, database: null, TimeoutSeconds, ct);

        ResultSets = result.ResultSets
            .Select((rs, i) => ResultSetView.From($"Result set {i + 1}", rs, session))
            .ToList();

        foreach (var m in result.Messages) Messages.Add(m);

        Status = $"Completed in {result.Elapsed.TotalMilliseconds:N0} ms" +
                 (result.RowsAffected > 0 ? $" · {result.RowsAffected} row(s) affected" : "") +
                 (result.ResultSets.Count > 0 ? $" · {result.ResultSets.Count} result set(s)" : "");

        await SafeAppendHistoryAsync(sql, succeeded: true, error: null);
    }

    private async Task RunOnDatabasesAsync(DatabaseSession session, string sql, IReadOnlyList<string> databases, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        Status = $"Running on {databases.Count} database(s)…";
        var progress = new Progress<int>(n => Status = $"Running… {n}/{databases.Count} database(s) done");

        var results = await multiQuery.RunAsync(session, sql, databases, TimeoutSeconds, (int)Math.Clamp(Parallelism, 1, 16), progress, ct);

        var merged = MultiDatabaseQueryService.Merge(results);
        ResultSets = merged
            .Select(m => new ResultSetView(m.Title, m.Columns, m.Rows.Select(r => new ResultRow(r)).ToList())
            {
                Dialect = ResultExporter.DialectFor(session.Provider.ProviderKey),
                Quote = session.Provider.QuoteIdentifier
            })
            .ToList();

        var failed = results.Where(r => !r.Succeeded).ToList();
        foreach (var r in results)
        {
            if (r.Error is not null) Messages.Add($"✗ {r.Database}: {r.Error}");
            else
            {
                var rows = r.Result!.ResultSets.Sum(s => s.Rows.Count);
                Messages.Add($"✓ {r.Database}: {rows:N0} row(s)" +
                             (r.Result.RowsAffected > 0 ? $", {r.Result.RowsAffected:N0} affected" : "") +
                             $" in {r.Result.Elapsed.TotalMilliseconds:N0} ms");
                foreach (var m in r.Result.Messages) Messages.Add($"    {r.Database}: {m}");
            }
        }

        Status = $"Ran on {results.Count - failed.Count} of {databases.Count} database(s) in {(DateTime.UtcNow - started).TotalMilliseconds:N0} ms" +
                 (failed.Count > 0 ? $" · {failed.Count} failed (see messages)" : "") +
                 $" · {merged.Count} merged result set(s)";

        await SafeAppendHistoryAsync(sql, succeeded: failed.Count == 0,
            error: failed.Count == 0 ? null : string.Join("; ", failed.Select(f => $"{f.Database}: {f.Error}")));
    }

    private async Task SafeAppendHistoryAsync(string sql, bool succeeded, string? error)
    {
        try
        {
            await scripts.AppendHistoryAsync(new ScriptHistoryEntry
            {
                RanAt = DateTimeOffset.Now,
                Sql = sql,
                Succeeded = succeeded,
                Error = error
            });
        }
        catch
        {
            // History persistence is best-effort; failures here should not surface to the user.
        }
    }

    private Task<bool> ConfirmDestructiveAsync(ConnectionProfile profile, int databaseCount)
    {
        var scope = databaseCount > 1 ? $" on {databaseCount} databases" : "";
        const string what = "This script may modify data or schema (INSERT/UPDATE/DELETE/DROP/ALTER/EXEC/...).";
        return profile.IsProduction
            ? dialogs.ConfirmAsync(
                $"{what} You are connected to a PRODUCTION environment. Run it{scope} anyway?",
                "Run on production", requiredText: "PRODUCTION", banner: $"PRODUCTION · {profile.DisplayName}")
            : dialogs.ConfirmAsync($"{what} Run it{scope} anyway?", "Run anyway");
    }
}
