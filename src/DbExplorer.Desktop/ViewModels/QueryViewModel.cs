using DbExplorer.Application.Connections;
using System.Collections.ObjectModel;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application;
using DbExplorer.Application.Assistant;
using DbExplorer.Application.Export;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.Query;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Abstractions;
using DbExplorer.Core.Connections;
using DbExplorer.Core.Models;
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
    private readonly DefinitionService definitions;
    private readonly SessionService sessions;
    private readonly SqlAssistant assistant;
    private DatabaseSession? _session;
    private SqlCompletionEngine? _completion;
    private CancellationTokenSource? _runCts;
    private bool _syncingDocument;

    public QueryViewModel(
        QueryExecutionService queryService, MultiDatabaseQueryService multiQuery, ScriptStore scripts, IDialogService dialogs,
        DefinitionService definitions, SessionService sessions, AppSettingsService settings, SnippetLibrary snippets,
        AssistantSettings assistantSettings, SqlAssistant assistant)
    {
        Settings = settings;
        AssistantSettings = assistantSettings;
        this.assistant = assistant;
        Snippets = snippets;
        this.definitions = definitions;
        this.sessions = sessions;
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

    /// <summary>The tab is renamed after the main table of each query it runs. Off for tabs of a file or with a given name.</summary>
    public bool AutoTitle { get; set; }

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

    /// <summary>Connects the tab to <paramref name="session"/>. It starts in the database it last used on that connection
    /// (see <see cref="Databases"/>), otherwise in the connection's database. Detached, it keeps showing its database.</summary>
    public void Attach(DatabaseSession? session)
    {
        var remembered = false;
        var preferredDatabase = session is null ? null : Databases.Resolve(session.Profile.Id, out remembered);
        // An open manual transaction belongs to the old connection: roll it back rather than leave it hanging.
        if (_transaction is not null && !ReferenceEquals(session, _session)) _ = EndTransactionAsync(commit: false, reason: "connection changed");
        if (_session is not null) _session.SnapshotChanged -= OnSnapshotChanged;
        _session = session;
        if (_session is not null) _session.SnapshotChanged += OnSnapshotChanged;
        ResultSets = [];
        Messages.Clear();
        Status = "";
        AutoRefresh = false;
        _lastRun = null;
        ResetExperiments();
        RunOnMultipleDatabases = false;
        SetDatabases([]);
        ExecuteCommand.NotifyCanExecuteChanged();
        NotifyLabCommands();

        _attaching = true;
        DatabaseWarning = "";
        if (session is null)
        {
            // Disconnected (or the app is closing): the tab still shows, and saves, the database it was in.
            AvailableDatabases = MergeNames([CurrentDatabase]);
        }
        else
        {
            AvailableDatabases = MergeNames(session.Snapshot.Databases, [session.Profile.Database, preferredDatabase]);
            CurrentDatabase = NullIfEmpty(preferredDatabase) ?? NullIfEmpty(session.Profile.Database);
            OnAvailableDatabasesChanged(AvailableDatabases);
            if (preferredDatabase is not null) Databases.Remember(session.Profile.Id, CurrentDatabase);
        }
        _attaching = false;
        OnPropertyChanged(nameof(CanChangeDatabase));
        RebuildCompletion();
        if (session is not null) _ = LoadServerDatabasesAsync(session, preferredDatabase, remembered);
    }

    private void OnSnapshotChanged(object? sender, EventArgs e)
    {
        if (_session is { } session) AvailableDatabases = MergeNames(AvailableDatabases, session.Snapshot.Databases, [CurrentDatabase]);
        RebuildCompletion();
    }

    // ----- Current database: where runs go and what the editor suggests -----

    private bool _attaching;
    private int _completionVersion;

    /// <summary>The database this tab's statements run in; the editor suggests only its tables, views, routines and
    /// columns. Null runs in the connection's default database with suggestions from everything loaded.</summary>
    [ObservableProperty] private string? _currentDatabase;

    /// <summary>Databases on the server, for the database picker.</summary>
    [ObservableProperty] private IReadOnlyList<string> _availableDatabases = [];

    /// <summary>What the suggestions are based on ("Suggestions: 1,234 objects in Sales"), shown next to the picker.</summary>
    [ObservableProperty] private string _completionInfo = "";

    /// <summary>The database picked on each connection, saved with the tab and applied when that connection is attached.</summary>
    public TabDatabaseMemory Databases { get; set; } = new();

    /// <summary>Shown next to the picker when the database the tab remembers is not on the server any more.</summary>
    [ObservableProperty] private string _databaseWarning = "";

    public bool CanChangeDatabase => _session is not null && !HasOpenTransaction && !RunOnMultipleDatabases;

    /// <summary>The database runs target: the picked one, or the connection's default.</summary>
    private string? TargetDatabase => NullIfEmpty(CurrentDatabase);

    partial void OnCurrentDatabaseChanged(string? oldValue, string? newValue)
    {
        if (_attaching) return;
        if (HasOpenTransaction && !string.Equals(oldValue, newValue, StringComparison.OrdinalIgnoreCase))
        {
            // The transaction's connection is bound to the old database; switching would run the next statement elsewhere.
            Status = "Commit or Rollback the open transaction before switching the database.";
            Avalonia.Threading.Dispatcher.UIThread.Post(() => CurrentDatabase = oldValue);
            return;
        }
        if (_session is { } session)
        {
            Databases.Remember(session.Profile.Id, newValue);
            DatabaseWarning = "";
        }
        RebuildCompletion();
    }

    /// <summary>The picker matches its selection exactly, so the current name takes the list's spelling ("sales" → "Sales").</summary>
    partial void OnAvailableDatabasesChanged(IReadOnlyList<string> value)
    {
        if (CurrentDatabase is { } current && !value.Contains(current, StringComparer.Ordinal) &&
            value.FirstOrDefault(n => string.Equals(n, current, StringComparison.OrdinalIgnoreCase)) is { } listed)
            CurrentDatabase = listed;
    }

    /// <param name="remembered">Whether <paramref name="preferredDatabase"/> was picked on this connection before.</param>
    private async Task LoadServerDatabasesAsync(DatabaseSession session, string? preferredDatabase, bool remembered)
    {
        try
        {
            var names = await session.Factory.ListDatabasesAsync(session.Profile);
            if (!ReferenceEquals(session, _session)) return;
            var missing = preferredDatabase is { Length: > 0 } && string.Equals(CurrentDatabase, preferredDatabase, StringComparison.OrdinalIgnoreCase) &&
                          !names.Contains(preferredDatabase, StringComparer.OrdinalIgnoreCase);
            if (missing && remembered)
            {
                // Picked on this connection before but gone now (dropped, renamed, no access): keep it and say so,
                // rather than quietly running the tab somewhere else.
                AvailableDatabases = MergeNames(names, session.Snapshot.Databases, [session.Profile.Database, CurrentDatabase]);
                DatabaseWarning = $"Database \"{preferredDatabase}\" was not found on this server. Pick another database to run this tab.";
                return;
            }
            AvailableDatabases = MergeNames(names, session.Snapshot.Databases, [session.Profile.Database]);
            if (missing)
            {
                // A database carried over from another tab or an older tabs file, not from this connection:
                // start in the connection's database instead.
                _attaching = true;
                CurrentDatabase = NullIfEmpty(session.Profile.Database);
                _attaching = false;
                Databases.Remember(session.Profile.Id, null);
                RebuildCompletion();
            }
        }
        catch
        {
            // Without the server list the picker still offers the databases already in the catalog.
        }
    }

    private static IReadOnlyList<string> MergeNames(params IEnumerable<string?>[] lists) =>
        lists.SelectMany(l => l).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>
    /// Builds the completion engine over the current database's catalog. Until a database is picked, a server-level
    /// connection suggests from every database; once it is, only that database's objects are offered, loading its
    /// catalog first when the connection did not read it.
    /// </summary>
    private async void RebuildCompletion()
    {
        OnPropertyChanged(nameof(ProviderKey));
        var version = ++_completionVersion;
        if (_session is not { } session)
        {
            _completion = null;
            _completionSnapshot = null;
            SetInspector(null);
            CompletionInfo = "";
            return;
        }

        var database = TargetDatabase;
        if (database is null)
        {
            var whole = session.Snapshot;
            await SetCompletionAsync(whole, session, () => whole.Databases.Count > 1 ? "every database (pick one to narrow)" : null, version);
            return;
        }

        CompletionInfo = $"Loading the objects of {database}…";
        try
        {
            var snapshot = await sessions.GetDatabaseSnapshotAsync(session, database);
            if (version != _completionVersion) return;
            await SetCompletionAsync(snapshot, session, () => database, version);
        }
        catch (Exception ex)
        {
            if (version != _completionVersion) return;
            await SetCompletionAsync(session.Snapshot, session, () => null, version);
            if (version != _completionVersion) return;
            CompletionInfo = $"Could not read the objects of {database}: {ex.Message}";
        }
    }

    /// <summary>Indexes the catalog for suggestions on the thread pool (sorting every column name of a large server
    /// is too slow for the UI thread), then swaps it in unless a newer rebuild started meanwhile.</summary>
    private async Task SetCompletionAsync(MetadataSnapshot snapshot, DatabaseSession session, Func<string?> scope, int version)
    {
        var values = ValueCacheFor(session);
        var (engine, inspector, objects, scopeText) = await Task.Run(() =>
        {
            var built = new SqlCompletionEngine(snapshot, session.Provider.QuoteIdentifier, session.Provider.ProviderKey)
            {
                ValueSource = values is null ? null : values.TryGet,
                UserSnippets = () => [.. Snippets.Items, .. Snippets.TeamItems()]
            };
            var checks = new SqlInspector(snapshot, session.Provider.QuoteIdentifier, session.Provider.ProviderKey);
            return (built, checks, snapshot.Objects.Count(o => o.Type is not DbObjectType.Trigger), scope());
        });
        if (version != _completionVersion) return;
        _completionSnapshot = snapshot;
        _completion = engine;
        SetInspector(inspector);
        CompletionInfo = $"Suggestions: {objects:N0} objects" + (scopeText is null ? "" : $" in {scopeText}");
    }

    /// <summary>Completion for the caret position; see <see cref="SqlCompletionEngine"/>. The list is not capped
    /// tightly: it filters itself as typing continues, so anything left out here could never be found.</summary>
    public CompletionResult GetCompletions(string text, int caret, bool explicitRequest) =>
        _completion?.Complete(text, caret, explicitRequest, max: 5000) ?? CompletionResult.Empty;

    private SqlInspector? _inspector;

    /// <summary>Checks the editor's SQL against the current catalog; null until a catalog is loaded.</summary>
    public SqlInspector? Inspector => _inspector;

    /// <summary>Raised when the catalog behind <see cref="Inspector"/> changed, so the warnings are recomputed.</summary>
    public event Action? InspectorChanged;

    private void SetInspector(SqlInspector? inspector)
    {
        if (ReferenceEquals(_inspector, inspector)) return;
        _inspector = inspector;
        InspectorChanged?.Invoke();
    }

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
        ExplainCommand.NotifyCanExecuteChanged();
        ExplainAnalyzeCommand.NotifyCanExecuteChanged();
        NotifyLabCommands();
    }

    partial void OnIsRunningChanged(bool value)
    {
        ExecuteCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        ExplainCommand.NotifyCanExecuteChanged();
        ExplainAnalyzeCommand.NotifyCanExecuteChanged();
        CommitCommand.NotifyCanExecuteChanged();
        RollbackCommand.NotifyCanExecuteChanged();
        NotifyLabCommands();
        OnPropertyChanged(nameof(AutoRefreshInfo));
    }

    partial void OnRunOnMultipleDatabasesChanged(bool value)
    {
        OnPropertyChanged(nameof(CanChangeDatabase));
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
            var current = TargetDatabase ?? session.Profile.Database;
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

    /// <summary>
    /// Runs <paramref name="run"/> (a selection or the statement at the caret, with its offset in the editor) or, when
    /// null, the whole script: asks for undeclared parameters, warns about UPDATE/DELETE without WHERE and other
    /// destructive statements, then runs in auto-commit or inside the tab's manual transaction.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExecute))]
    private async Task ExecuteAsync(QueryRun? run)
    {
        if (_session is not { } session) return;

        var targets = RunOnMultipleDatabases ? _allDatabases.Where(d => d.IsSelected).Select(d => d.Name).ToList() : [];
        var sql = run is { Sql: { Length: > 0 } part } && !string.IsNullOrWhiteSpace(part) ? part : Sql;
        var startOffset = run is { Sql.Length: > 0 } ? run.StartOffset : 0;
        if (string.IsNullOrWhiteSpace(sql)) return;
        if (!await ConfirmDiscardEditsAsync()) return;
        ErrorCleared?.Invoke();

        sql = await FillParametersAsync(sql, session.Provider.ProviderKey);
        if (sql is null) { Status = "Not run (parameters were not given)."; return; }

        if (session.Profile.ReadOnly && ReadOnlyGuard.MayWrite(sql))
        {
            Status = ReadOnlyGuard.Refusal(session.Profile, "The script");
            return;
        }
        if (!await ConfirmRiskyAsync(session.Profile, sql, Math.Max(1, targets.Count))) return;

        _lastRun = new RepeatableRun(sql, startOffset, targets);
        await RunAsync(session, sql, startOffset, targets);
    }

    /// <summary>Runs <paramref name="sql"/> with nothing left to ask; false when it failed or was cancelled.</summary>
    private async Task<bool> RunAsync(DatabaseSession session, string sql, int startOffset, IReadOnlyList<string> targets)
    {
        _runCts?.Dispose();
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;
        IsRunning = true;
        StartElapsedClock();
        Messages.Clear();
        var outcome = RunOutcome.Failed;
        try
        {
            var succeeded = true;
            if (targets.Count > 0)
                succeeded = await RunOnDatabasesAsync(session, sql, targets, ct);
            else if (!AutoCommit)
                await RunInTransactionAsync(session, sql, ct);
            else
                await RunOnceAsync(session, sql, ct);
            if (AutoTitle && QueryTabNamer.Suggest(sql) is { } name) Title = name;
            if (succeeded) AfterRun(session, sql);
            if (succeeded) outcome = RunOutcome.Succeeded;
            RememberError(sql, succeeded ? null : NullIfEmpty(string.Join("\n", Messages.Where(m => m.StartsWith('✗')))));
            return succeeded;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            outcome = RunOutcome.Cancelled;
            Status = "Cancelled." + (HasOpenTransaction ? " The transaction is still open: Commit or Rollback." : "");
            return false;
        }
        catch (Exception ex)
        {
            Status = "Error: " + ex.Message + (HasOpenTransaction ? " · transaction still open" : "");
            Messages.Add(ex.Message);
            RememberError(sql, ex.Message);
            if (ex is SqlExecutionException { Line: { } line } located) ErrorLocated?.Invoke(startOffset, line, located.Column);
            await SafeAppendHistoryAsync(sql, succeeded: false, error: ex.Message);
            return false;
        }
        finally
        {
            IsRunning = false;
            var elapsed = _runClock.Elapsed;
            StopElapsedClock();
            if (!_isRefreshRun) RunFinished?.Invoke(this, elapsed, outcome);
        }
    }

    /// <summary>A run of this tab ended (not auto refresh): the workspace may tell the user if they looked away.</summary>
    public event Action<QueryViewModel, TimeSpan, RunOutcome>? RunFinished;

    private int RowLimit => MaxRows <= 0 ? int.MaxValue : (int)Math.Min(MaxRows, int.MaxValue);

    private async Task RunOnceAsync(DatabaseSession session, string sql, CancellationToken ct)
    {
        Status = "Running…";
        var result = await queryService.ExecuteScriptAsync(session, sql, TargetDatabase, TimeoutSeconds, ct, RowLimit);
        ShowResult(session, sql, result);
        await SafeAppendHistoryAsync(sql, succeeded: true, error: null);
    }

    private void ShowResult(DatabaseSession session, string sql, QueryExecutionResult result, string prefix = "")
    {
        var sources = ResolveSources(session, sql, result);
        ResultSets = result.ResultSets
            .Select((rs, i) => WithSource(ResultSetView.From($"Result {i + 1}", rs, session), sources[i]))
            .Select(v => v.IsTruncated ? v with { Title = $"{v.Title} (first {v.Rows.Count:N0} of {v.TotalRowsText})" } : v)
            .ToList();

        foreach (var m in result.Messages) Messages.Add(m);
        var truncated = result.ResultSets.Where(r => r.IsTruncated).ToList();
        Status = prefix + $"Completed in {result.Elapsed.TotalMilliseconds:N0} ms" +
                 (result.RowsAffected > 0 ? $" · {result.RowsAffected:N0} row(s) affected" : "") +
                 (result.ResultSets.Count > 0 ? $" · {result.ResultSets.Count} result set(s)" : "") +
                 (truncated.Count == 0 ? "" :
                  truncated.All(r => !r.TotalRowCountIsExact)
                      ? $" · stopped at the row limit ({RowLimit:N0}) without reading the rest (raise the limit to see more)"
                      : $" · showing the first {RowLimit:N0} rows (raise the row limit to see more)");
    }

    private async Task<bool> RunOnDatabasesAsync(DatabaseSession session, string sql, IReadOnlyList<string> databases, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        Status = $"Running on {databases.Count} database(s)…";
        var progress = new Progress<int>(n => Status = $"Running… {n}/{databases.Count} database(s) done");

        var results = await multiQuery.RunAsync(session, sql, databases, TimeoutSeconds, (int)Math.Clamp(Parallelism, 1, 16), progress, ct, RowLimit);

        var merged = MultiDatabaseQueryService.Merge(results);
        ResultSets = merged
            .Select(m => new ResultSetView(m.Title, m.Columns, m.Rows.Select(r => new ResultRow(r)).ToList())
            {
                Connection = ResultSetView.DescribeConnection(session),
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
                             (r.Result.ResultSets.Any(s => s.IsTruncated) ? $" (row limit {RowLimit:N0} reached)" : "") +
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
        return failed.Count == 0;
    }

    private async Task SafeAppendHistoryAsync(string sql, bool succeeded, string? error)
    {
        if (_isRefreshRun) return; // a timer run repeats the entry already there
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

    /// <summary>Asks before scripts that change data or schema; lists statements that touch every row or drop objects.</summary>
    private Task<bool> ConfirmRiskyAsync(ConnectionProfile profile, string sql, int databaseCount)
    {
        var unsafeStatements = SqlEditorAnalysis.FindUnsafeStatements(sql);
        if (!queryService.IsPotentiallyDestructive(sql) && unsafeStatements.Count == 0) return Task.FromResult(true);
        if (!AutoCommit && !profile.IsProduction && unsafeStatements.Count == 0) return Task.FromResult(true); // reversible: Rollback

        var scope = databaseCount > 1 ? $" on {databaseCount} databases" : "";
        var what = unsafeStatements.Count > 0
            ? "Careful:\n" + string.Join("\n", unsafeStatements.Take(8).Select(u => $"  • line {u.Line}: {u.Description}")) + "\n"
            : "This script may modify data or schema (INSERT/UPDATE/DELETE/DROP/ALTER/EXEC/...).";
        var tx = AutoCommit ? "" : "\nIt runs inside the open transaction, so you can still Rollback.";
        return profile.IsProduction
            ? dialogs.ConfirmAsync(
                $"{what} You are connected to a PRODUCTION environment. Run it{scope} anyway?{tx}",
                "Run on production", requiredText: "PRODUCTION", banner: $"PRODUCTION · {profile.DisplayName}")
            : dialogs.ConfirmAsync($"{what} Run it{scope} anyway?{tx}", "Run anyway");
    }
}
