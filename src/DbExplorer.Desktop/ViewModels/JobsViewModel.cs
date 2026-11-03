using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Connections;
using DbExplorer.Application.Jobs;
using DbExplorer.Application.Sessions;
using DbExplorer.Desktop.Services;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>
/// The Jobs tab: the server's scheduled jobs (SQL Server Agent, pg_cron, MySQL events) with their schedule, last and
/// next run, steps and run history. Run now, stop, enable, disable, delete, create and edit build the engine's own
/// script, which is shown and confirmed (typing PRODUCTION on production connections) before it runs; read-only
/// connections only show and copy.
/// </summary>
public partial class JobsViewModel : ViewModelBase, ISessionAware
{
    private const int TimeoutSeconds = 60;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private readonly IDialogService _dialogs;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(10) };
    private DatabaseSession? _session;
    private JobCatalog? _catalog;
    private int _loadVersion;
    private int _historyVersion;
    private ScheduledJob? _editing;
    private bool _loadingEditor;

    public JobsViewModel(IDialogService dialogs)
    {
        _dialogs = dialogs;
        _timer.Tick += async (_, _) =>
        {
            if (!IsBusy && !IsEditing) await LoadAsync(quiet: true);
        };
    }

    public event Action<string, string?>? OpenSqlRequested;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _source = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSetup))] private string? _setup;
    [ObservableProperty] private bool _canStartScheduler;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasWarnings))] private string _warnings = "";
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private bool _autoRefresh;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasNoJobs))] [NotifyPropertyChangedFor(nameof(ShowEmptyHint))] private IReadOnlyList<JobItem> _jobs = [];
    [ObservableProperty] private JobItem? _selectedJob;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasHistory))] private IReadOnlyList<JobRunItem> _history = [];
    [ObservableProperty] private string _historyStatus = "";

    /// <summary>SQL Server: the job outcome only says which step failed; this is that step's own error.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasStepError))] private string _stepError = "";
    public bool HasStepError => StepError.Length > 0;
    [ObservableProperty] private bool _onlyJobOutcomes = true;
    [ObservableProperty] private IReadOnlyList<string> _databases = [];

    public bool HasSetup => !string.IsNullOrEmpty(Setup);

    /// <summary>For the view: the lists are often arrays, which have no Count for a binding to read.</summary>
    public bool HasNoJobs => Jobs.Count == 0;
    public bool HasHistory => History.Count > 0;
    public bool HasWarnings => Warnings.Length > 0;
    public bool HasSelection => SelectedJob is not null && !IsEditing;
    public bool ShowEmptyHint => SelectedJob is null && !IsEditing && Jobs.Count > 0;
    public bool IsAvailable => _catalog?.Available == true;
    public bool IsReadOnly => _session?.Profile.ReadOnly == true;
    public bool IsSqlServer => _catalog?.Engine == JobEngine.SqlServerAgent;
    public bool CanRunNow => SelectedJob is { Job.IsRunning: false } && _catalog?.Capabilities.CanRunNow == true;
    public bool CanStop => SelectedJob is { Job.IsRunning: true } && _catalog?.Capabilities.CanStop == true;
    public bool ShowHistory => _catalog?.Capabilities.HasHistory == true;
    public bool ShowNoHistoryNote => _catalog?.Engine == JobEngine.MySqlEvents;
    public bool ShowSteps => SelectedJob?.Job.Steps.Count > 1 || SelectedJob?.Job.Steps is [{ IsTransactSql: false }];
    public bool ShowCommand => !ShowSteps;
    public bool CanCreate => IsAvailable && !IsBusy;
    public string ToggleLabel => SelectedJob?.Job.Enabled == true ? "Disable" : "Enable";
    public string NewJobLabel => _catalog?.Engine == JobEngine.MySqlEvents ? "＋ New event…" : "＋ New job…";
    public string EmptyHint => _catalog is null ? "" : !_catalog.Available ? "" :
        Jobs.Count == 0 && _catalog.Jobs.Count > 0 ? "No job matches the filter." :
        _catalog.Jobs.Count == 0 ? $"No {(IsMySql ? "events" : "jobs")} yet. {NewJobLabel.TrimStart('＋', ' ').TrimEnd('…')} creates one." : "Pick a job to see its schedule, command and runs.";
    private bool IsMySql => _catalog?.Engine == JobEngine.MySqlEvents;

    // ----- details of the selected job -----

    public string DetailTitle => SelectedJob?.Name ?? "";
    public string DetailSchedule => SelectedJob?.Job.ScheduleDescription ?? "";
    public string DetailScheduleText => SelectedJob?.Job is { } j && j.Engine != JobEngine.SqlServerAgent ? j.ScheduleText : "";
    public string DetailFacts => SelectedJob?.Facts ?? "";
    public string DetailRuns => SelectedJob?.RunsText ?? "";
    public string? DetailMessage => SelectedJob?.Job.LastMessage;
    public bool HasDetailMessage => !string.IsNullOrWhiteSpace(DetailMessage);
    public string DetailCommand => SelectedJob?.Job.Command ?? "";
    public IReadOnlyList<JobStep> DetailSteps => SelectedJob?.Job.Steps ?? [];
    public string? DetailDescription => SelectedJob?.Job.Description;
    public bool HasDetailDescription => !string.IsNullOrWhiteSpace(DetailDescription);

    partial void OnSelectedJobChanged(JobItem? value)
    {
        foreach (var name in new[]
                 {
                     nameof(HasSelection), nameof(ShowEmptyHint), nameof(CanRunNow), nameof(CanStop), nameof(ShowSteps), nameof(ShowCommand),
                     nameof(ToggleLabel), nameof(DetailTitle), nameof(DetailSchedule), nameof(DetailScheduleText), nameof(DetailFacts),
                     nameof(DetailRuns), nameof(DetailMessage), nameof(HasDetailMessage), nameof(DetailCommand), nameof(DetailSteps),
                     nameof(DetailDescription), nameof(HasDetailDescription)
                 })
            OnPropertyChanged(name);
        NotifyCommands();
        _ = LoadHistoryAsync();
    }

    partial void OnFilterChanged(string value) => RebuildList();

    partial void OnOnlyJobOutcomesChanged(bool value) => _ = LoadHistoryAsync();

    partial void OnAutoRefreshChanged(bool value)
    {
        if (value && _session is not null) _timer.Start();
        else _timer.Stop();
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCreate));
        NotifyCommands();
    }

    public void Attach(DatabaseSession? session)
    {
        if (_session is not null) _session.DatabasesChanged -= OnDatabasesChanged;
        _session = session;
        if (_session is not null) _session.DatabasesChanged += OnDatabasesChanged;
        AutoRefresh = false;
        _catalog = null;
        _editing = null;
        IsEditing = false;
        Jobs = [];
        SelectedJob = null;
        History = [];
        Setup = null;
        Warnings = "";
        Source = "";
        CanStartScheduler = false;
        Status = "";
        IsBusy = false;
        NotifyEngine();
        if (session is null)
        {
            Databases = [];
            return;
        }
        Databases = Merge(session.Snapshot.Databases, [session.Profile.Database]);
        _ = LoadServerDatabasesAsync(session);
        _ = LoadAsync();
    }

    private void OnDatabasesChanged(object? sender, EventArgs e)
    {
        if (_session is { } session) _ = LoadServerDatabasesAsync(session);
    }

    private async Task LoadServerDatabasesAsync(DatabaseSession session)
    {
        try
        {
            var names = await session.GetServerDatabasesAsync();
            if (ReferenceEquals(session, _session)) Databases = Merge(names, session.Snapshot.Databases, [session.Profile.Database]);
        }
        catch
        {
            // The snapshot's databases stay.
        }
    }

    private static IReadOnlyList<string> Merge(params IEnumerable<string?>[] lists) =>
        lists.SelectMany(l => l).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToList();

    private bool CanRefresh => _session is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => LoadAsync();

    private async Task LoadAsync(bool quiet = false)
    {
        if (_session is not { } session) return;
        var version = ++_loadVersion;
        if (!quiet)
        {
            IsBusy = true;
            Status = "Reading scheduled jobs…";
        }
        try
        {
            var catalog = await Task.Run(() => JobCatalogLoader.LoadAsync(session.Provider, session.Profile.Database));
            if (version != _loadVersion || !ReferenceEquals(session, _session)) return;
            _catalog = catalog;
            Source = catalog.Source;
            Setup = catalog.Setup;
            CanStartScheduler = catalog.SchedulerCanBeStarted;
            Warnings = string.Join("\n", catalog.Warnings);
            NotifyEngine();
            RebuildList();
            var running = catalog.Jobs.Count(j => j.IsRunning);
            var failed = catalog.Jobs.Count(j => j.LastOutcome == JobOutcome.Failed && !j.IsRunning);
            var noun = catalog.Engine == JobEngine.MySqlEvents ? "event" : "job";
            Status = !catalog.Available ? "" :
                $"{catalog.Jobs.Count:N0} {noun}(s), {catalog.Jobs.Count(j => !j.Enabled):N0} disabled" +
                (running > 0 ? $", {running} running" : "") + (failed > 0 ? $", {failed} failed last time" : "") +
                $" · {DateTime.Now:T}" + (IsReadOnly ? " · read-only connection: scripts can be copied, not run" : "");
            if (quiet) await LoadHistoryAsync();
        }
        catch (Exception ex)
        {
            if (version != _loadVersion) return;
            Status = "Could not read jobs: " + ex.Message;
            AutoRefresh = false;
        }
        finally
        {
            if (version == _loadVersion && !quiet) IsBusy = false;
        }
    }

    private void NotifyEngine()
    {
        foreach (var name in new[]
                 {
                     nameof(IsAvailable), nameof(IsReadOnly), nameof(IsSqlServer), nameof(ShowHistory), nameof(ShowNoHistoryNote), nameof(CanCreate),
                     nameof(NewJobLabel), nameof(ScheduleKinds), nameof(CanEditDescription), nameof(DatabaseLabel), nameof(ShowPreserve), nameof(EmptyHint)
                 })
            OnPropertyChanged(name);
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        NewJobCommand.NotifyCanExecuteChanged();
        EditCommand.NotifyCanExecuteChanged();
        RunNowCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        ToggleEnabledCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        OpenInQueryCommand.NotifyCanExecuteChanged();
        ScriptJobCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanRunNow));
        OnPropertyChanged(nameof(CanStop));
    }

    private void RebuildList(string? select = null)
    {
        var catalog = _catalog;
        var keep = select ?? SelectedJob?.Job.Id;
        var now = DateTime.Now;
        var serverNow = catalog?.ServerNow ?? now;
        var items = (catalog?.Jobs ?? [])
            .Where(j => Filter.Length == 0 || j.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase) ||
                        (j.Database ?? "").Contains(Filter, StringComparison.OrdinalIgnoreCase) ||
                        j.Command.Contains(Filter, StringComparison.OrdinalIgnoreCase))
            .Select(j => new JobItem(j, catalog!.Engine == JobEngine.PgCron ? now : serverNow))
            .ToList();
        Jobs = items;
        SelectedJob = items.FirstOrDefault(i => i.Job.Id == keep) ?? (select is not null ? items.FirstOrDefault(i => i.Name == select) : null);
        OnPropertyChanged(nameof(EmptyHint));
    }

    private async Task LoadHistoryAsync()
    {
        var version = ++_historyVersion;
        StepError = "";
        if (_session is not { } session || _catalog is not { } catalog || SelectedJob?.Job is not { } job || !catalog.Capabilities.HasHistory)
        {
            History = [];
            HistoryStatus = "";
            return;
        }
        HistoryStatus = "Reading runs…";
        try
        {
            var runs = await Task.Run(() => JobCatalogLoader.LoadHistoryAsync(session.Provider, catalog, job));
            if (version != _historyVersion) return;
            var shown = OnlyJobOutcomes && catalog.Engine == JobEngine.SqlServerAgent ? runs.Where(r => r.StepId == 0).ToList() : runs;
            // Rows are newest first: the step rows just before the latest job outcome belong to that run.
            var lastRun = runs.SkipWhile(r => r.StepId != 0).Skip(1).TakeWhile(r => r.StepId != 0).ToList();
            StepError = job.LastOutcome == JobOutcome.Failed && lastRun.FirstOrDefault(r => r.Outcome == JobOutcome.Failed) is { } failed
                ? $"Step {failed.StepId} ({failed.StepName}): {failed.Message}"
                : "";
            History = shown.Select(r => new JobRunItem(r, catalog.Engine == JobEngine.SqlServerAgent)).ToList();
            HistoryStatus = runs.Count == 0
                ? catalog.Engine == JobEngine.PgCron ? "No runs recorded (cron.log_run must be on to keep them)." : "No runs in the history yet."
                : $"Latest {shown.Count:N0} run(s), newest first.";
        }
        catch (Exception ex)
        {
            if (version == _historyVersion)
            {
                History = [];
                HistoryStatus = "Could not read the history: " + ex.Message;
            }
        }
    }

    // ----- actions on the selected job -----

    private bool HasJob => SelectedJob is not null && !IsBusy && !IsEditing && IsAvailable;

    private bool CanRunSelected => HasJob && CanRunNow;

    private bool CanStopSelected => HasJob && CanStop;

    [RelayCommand(CanExecute = nameof(CanRunSelected))]
    private Task RunNowAsync() => SelectedJob is { } item
        ? ExecuteAsync(JobScriptBuilder.RunNow(item.Job), $"Start {item.Name} now? SQL Server Agent runs it in the background from its first step.", "Run now", item.Name)
        : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanStopSelected))]
    private Task StopAsync() => SelectedJob is { } item
        ? ExecuteAsync(JobScriptBuilder.Stop(item.Job), $"Stop {item.Name}? The step that is running is cancelled.", "Stop job", item.Name)
        : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(HasJob))]
    private Task ToggleEnabledAsync()
    {
        if (SelectedJob is not { } item || _catalog is not { } catalog) return Task.CompletedTask;
        var enable = !item.Job.Enabled;
        return ExecuteAsync(JobScriptBuilder.SetEnabled(catalog, item.Job, enable),
            enable ? $"Enable {item.Name}? It runs on its schedule again." : $"Disable {item.Name}? It stays, but does not run on its schedule until enabled.",
            enable ? "Enable" : "Disable", item.Name);
    }

    [RelayCommand(CanExecute = nameof(HasJob))]
    private Task DeleteAsync()
    {
        if (SelectedJob is not { } item || _catalog is not { } catalog) return Task.CompletedTask;
        var history = catalog.Engine == JobEngine.SqlServerAgent ? " Its history goes with it." : "";
        return ExecuteAsync(JobScriptBuilder.Delete(catalog, item.Job), $"Delete {item.Name}? This cannot be undone.{history} Script job first keeps a copy.",
            "Delete", null);
    }

    [RelayCommand(CanExecute = nameof(HasJob))]
    private void OpenInQuery()
    {
        if (SelectedJob?.Job is not { } job) return;
        OpenSqlRequested?.Invoke(JobScriptBuilder.CommandForQuery(job), job.Engine == JobEngine.SqlServerAgent && job.Steps.Count > 1 ? null : job.Database);
    }

    /// <summary>Opens a script that creates the job again (to keep, or to copy to another server) in a Query tab.</summary>
    [RelayCommand(CanExecute = nameof(HasJob))]
    private void ScriptJob()
    {
        if (SelectedJob?.Job is not { } job || _catalog is not { } catalog) return;
        OpenSqlRequested?.Invoke(JobScriptBuilder.ScriptAsCreate(catalog, job, catalog.ServerNow ?? DateTime.Now),
            catalog.Engine == JobEngine.PgCron ? catalog.HomeDatabase : catalog.Engine == JobEngine.MySqlEvents ? job.Database : null);
    }

    [RelayCommand]
    private Task StartSchedulerAsync() =>
        ExecuteAsync(JobScriptBuilder.StartEventScheduler(),
            "Turn the event scheduler on? Events start running on their schedules. It lasts until the server restarts; add event_scheduler=ON to my.cnf to keep it.",
            "Turn on", SelectedJob?.Name);

    /// <summary>Shows the script, asks (typing PRODUCTION on a production connection), runs it and reads the jobs again.</summary>
    private async Task<bool> ExecuteAsync(JobScript script, string message, string confirmText, string? selectAfter)
    {
        if (_session is not { } session || IsBusy || script.IsEmpty) return false;
        if (session.Profile.ReadOnly)
        {
            Status = ReadOnlyGuard.Refusal(session.Profile, "This job script") + " Script job and Open in Query tab still hand it over.";
            return false;
        }
        var production = session.Profile.IsProduction;
        var ok = await _dialogs.ConfirmAsync(
            $"{message}\n\nOn {session.Profile.DisplayName}" + (script.Transactional ? ", in one transaction:" : ":"),
            confirmText, production ? "PRODUCTION" : null, production ? $"PRODUCTION · {session.Profile.DisplayName}" : null, script.Sql);
        if (!ok) return false;

        IsBusy = true;
        Status = script.Summary + "…";
        try
        {
            await Task.Run(() => JobScriptBuilder.RunAsync(session.Provider, script, TimeoutSeconds));
        }
        catch (Exception ex)
        {
            IsBusy = false;
            Status = $"{script.Summary} failed: {ex.Message}";
            return false;
        }
        IsBusy = false;
        // Agent writes the run to its tables a moment after sp_start_job returns.
        if (script.Summary.StartsWith("Run ", StringComparison.Ordinal)) await Task.Delay(500);
        await LoadAsync();
        if (selectAfter is not null) RebuildList(Jobs.FirstOrDefault(j => j.Name == selectAfter)?.Job.Id ?? selectAfter);
        Status = $"{script.Summary}: done. " + Status;
        return true;
    }

    // ----- create / edit -----

    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editorTitle = "";
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string? _editDatabase;
    [ObservableProperty] private bool _editEnabled = true;
    [ObservableProperty] private string _editDescription = "";
    [ObservableProperty] private string _editSql = "";
    [ObservableProperty] private bool _editPreserve = true;
    [ObservableProperty] private ScheduleChoice? _editKind;
    [ObservableProperty] private decimal? _editInterval = 15;
    [ObservableProperty] private string _editTime = "03:00";
    [ObservableProperty] private decimal? _editDayOfMonth = 1;
    [ObservableProperty] private string _editOnceAt = "";
    [ObservableProperty] private string _editCron = "0 3 * * *";
    [ObservableProperty] private bool _editMonday = true;
    [ObservableProperty] private bool _editTuesday;
    [ObservableProperty] private bool _editWednesday;
    [ObservableProperty] private bool _editThursday;
    [ObservableProperty] private bool _editFriday;
    [ObservableProperty] private bool _editSaturday;
    [ObservableProperty] private bool _editSunday;
    [ObservableProperty] private bool _keepSchedule;
    [ObservableProperty] private string _schedulePreview = "";
    [ObservableProperty] private string _editorProblem = "";
    [ObservableProperty] private string _editorScript = "";

    public IReadOnlyList<ScheduleChoice> ScheduleKinds => (_catalog?.Engine ?? JobEngine.PgCron) switch
    {
        JobEngine.PgCron => ScheduleChoice.All.Where(c => c.Kind != ScheduleKind.Once).ToList(),
        _ => ScheduleChoice.All.Where(c => c.Kind != ScheduleKind.Cron).ToList()
    };

    public bool CanEditName => _editing is null || _catalog?.Capabilities.CanRename == true;
    public bool CanEditDescription => _catalog?.Capabilities.HasDescription == true;
    public bool CanEditCommand => _editing?.CommandEditable != false;
    public bool CommandLockedNote => !CanEditCommand;
    public bool ShowPreserve => IsMySql;
    public bool CanKeepSchedule => _editing is not null;
    public string KeepScheduleLabel => _editing is null ? "" : "Keep the current schedule: " + _editing.ScheduleDescription;
    public bool ShowScheduleForm => !KeepSchedule;
    public string DatabaseLabel => (_catalog?.Engine) switch
    {
        JobEngine.MySqlEvents => "Database",
        JobEngine.PgCron => "Runs in database",
        _ => "Step database"
    };
    public bool HasEditorProblem => EditorProblem.Length > 0;
    public bool ShowInterval => EditKind?.Kind is ScheduleKind.EveryMinutes or ScheduleKind.EveryHours;
    public string IntervalUnit => EditKind?.Kind == ScheduleKind.EveryHours ? "hours" : "minutes";
    public bool ShowTime => EditKind?.Kind is ScheduleKind.Daily or ScheduleKind.Weekly or ScheduleKind.Monthly or ScheduleKind.EveryHours;
    public string TimeLabel => EditKind?.Kind == ScheduleKind.EveryHours ? "at minute (:mm)" : "at (HH:mm)";
    public bool ShowDays => EditKind?.Kind == ScheduleKind.Weekly;
    public bool ShowDayOfMonth => EditKind?.Kind == ScheduleKind.Monthly;
    public bool ShowOnceAt => EditKind?.Kind == ScheduleKind.Once;
    public bool ShowCron => EditKind?.Kind == ScheduleKind.Cron;
    public string SaveLabel => _editing is null ? (IsMySql ? "Create event…" : "Create job…") : "Save changes…";

    partial void OnEditKindChanged(ScheduleChoice? value)
    {
        foreach (var name in new[] { nameof(ShowInterval), nameof(IntervalUnit), nameof(ShowTime), nameof(TimeLabel), nameof(ShowDays), nameof(ShowDayOfMonth), nameof(ShowOnceAt), nameof(ShowCron) })
            OnPropertyChanged(name);
        if (value?.Kind == ScheduleKind.EveryHours && TimeOnly.TryParse(EditTime, Invariant, out var t) && t.Hour != 0) EditTime = $"00:{t.Minute:00}";
        Recompute();
    }

    partial void OnKeepScheduleChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowScheduleForm));
        Recompute();
    }

    partial void OnIsEditingChanged(bool value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(ShowEmptyHint));
        NotifyCommands();
    }

    partial void OnEditorProblemChanged(string value) => OnPropertyChanged(nameof(HasEditorProblem));

    partial void OnEditNameChanged(string value) => Recompute();
    partial void OnEditDatabaseChanged(string? value) => Recompute();
    partial void OnEditEnabledChanged(bool value) => Recompute();
    partial void OnEditDescriptionChanged(string value) => Recompute();
    partial void OnEditSqlChanged(string value) => Recompute();
    partial void OnEditPreserveChanged(bool value) => Recompute();
    partial void OnEditIntervalChanged(decimal? value) => Recompute();
    partial void OnEditTimeChanged(string value) => Recompute();
    partial void OnEditDayOfMonthChanged(decimal? value) => Recompute();
    partial void OnEditOnceAtChanged(string value) => Recompute();
    partial void OnEditCronChanged(string value) => Recompute();
    partial void OnEditMondayChanged(bool value) => Recompute();
    partial void OnEditTuesdayChanged(bool value) => Recompute();
    partial void OnEditWednesdayChanged(bool value) => Recompute();
    partial void OnEditThursdayChanged(bool value) => Recompute();
    partial void OnEditFridayChanged(bool value) => Recompute();
    partial void OnEditSaturdayChanged(bool value) => Recompute();
    partial void OnEditSundayChanged(bool value) => Recompute();

    private bool CanNew => IsAvailable && !IsBusy && !IsEditing;

    [RelayCommand(CanExecute = nameof(CanNew))]
    private void NewJob()
    {
        if (_catalog is not { } catalog || _session is not { } session) return;
        _editing = null;
        var spec = new JobScheduleSpec { Kind = ScheduleKind.Daily, At = new TimeOnly(3, 0) };
        var database = catalog.Engine switch
        {
            JobEngine.PgCron => session.Profile.Database is { Length: > 0 } db ? db : catalog.HomeDatabase,
            _ => session.Profile.Database is { Length: > 0 } d ? d : catalog.Engine == JobEngine.SqlServerAgent ? "master" : Databases.FirstOrDefault()
        };
        LoadEditor(new JobDraft { Name = "", Database = database, Command = "", Schedule = spec, Enabled = true },
            catalog.Engine == JobEngine.MySqlEvents ? "New event" : "New job");
    }

    [RelayCommand(CanExecute = nameof(HasJob))]
    private void Edit()
    {
        if (SelectedJob?.Job is not { } job) return;
        _editing = job;
        LoadEditor(JobScriptBuilder.DraftOf(job), "Edit " + job.Name);
    }

    private void LoadEditor(JobDraft draft, string title)
    {
        _loadingEditor = true;
        try
        {
            EditorTitle = title;
            EditName = draft.Name;
            EditDatabase = draft.Database;
            EditEnabled = draft.Enabled;
            EditDescription = draft.Description ?? "";
            EditSql = draft.Command;
            EditPreserve = draft.Preserve;
            KeepSchedule = _editing is not null && draft.Schedule is null;
            var spec = draft.Schedule ?? new JobScheduleSpec();
            EditKind = ScheduleKinds.FirstOrDefault(k => k.Kind == spec.Kind) ?? ScheduleKinds.First(k => k.Kind == ScheduleKind.Daily);
            EditInterval = spec.Kind is ScheduleKind.EveryMinutes or ScheduleKind.EveryHours ? spec.Interval : 15;
            EditTime = spec.Kind == ScheduleKind.EveryHours ? $"00:{spec.At.Minute:00}" : spec.At.ToString("HH:mm", Invariant);
            EditDayOfMonth = spec.DayOfMonth;
            EditOnceAt = (spec.OnceAt ?? (_catalog?.ServerNow ?? DateTime.Now).Date.AddDays(1).AddHours(3)).ToString("yyyy-MM-dd HH:mm", Invariant);
            EditCron = spec.Cron ?? (_editing?.Engine == JobEngine.PgCron ? _editing.ScheduleText : "0 3 * * *");
            var days = spec.Days.ToHashSet();
            EditMonday = days.Contains(DayOfWeek.Monday);
            EditTuesday = days.Contains(DayOfWeek.Tuesday);
            EditWednesday = days.Contains(DayOfWeek.Wednesday);
            EditThursday = days.Contains(DayOfWeek.Thursday);
            EditFriday = days.Contains(DayOfWeek.Friday);
            EditSaturday = days.Contains(DayOfWeek.Saturday);
            EditSunday = days.Contains(DayOfWeek.Sunday);
        }
        finally
        {
            _loadingEditor = false;
        }
        foreach (var name in new[] { nameof(CanEditName), nameof(CanEditCommand), nameof(CommandLockedNote), nameof(CanKeepSchedule), nameof(KeepScheduleLabel), nameof(SaveLabel), nameof(ShowPreserve), nameof(DatabaseLabel) })
            OnPropertyChanged(name);
        IsEditing = true;
        Recompute();
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
        _editing = null;
    }

    /// <summary>The form as a draft; the problem when a field cannot be read.</summary>
    private (JobDraft Draft, string? Problem) ReadEditor()
    {
        string? problem = null;
        JobScheduleSpec? spec = null;
        if (!KeepSchedule && EditKind is { } kind)
        {
            var interval = (int)(EditInterval ?? 0);
            TimeOnly at = default;
            if (kind.Kind is ScheduleKind.Daily or ScheduleKind.Weekly or ScheduleKind.Monthly or ScheduleKind.EveryHours &&
                !TimeOnly.TryParseExact(EditTime.Trim(), ["H:mm", "HH:mm", "H:mm:ss", "HH:mm:ss", "%m", ":mm", "mm"], Invariant, DateTimeStyles.None, out at))
                problem = kind.Kind == ScheduleKind.EveryHours ? "Type the minute past the hour, such as 00:15." : "Type the time as HH:mm, such as 03:00.";
            DateTime? once = null;
            if (kind.Kind == ScheduleKind.Once)
            {
                if (DateTime.TryParseExact(EditOnceAt.Trim(), ["yyyy-MM-dd HH:mm", "yyyy-MM-dd H:mm", "yyyy-MM-dd HH:mm:ss"], Invariant, DateTimeStyles.None, out var parsed))
                    once = parsed;
                else problem = "Type the date and time as yyyy-MM-dd HH:mm.";
            }
            var days = new List<DayOfWeek>();
            if (EditMonday) days.Add(DayOfWeek.Monday);
            if (EditTuesday) days.Add(DayOfWeek.Tuesday);
            if (EditWednesday) days.Add(DayOfWeek.Wednesday);
            if (EditThursday) days.Add(DayOfWeek.Thursday);
            if (EditFriday) days.Add(DayOfWeek.Friday);
            if (EditSaturday) days.Add(DayOfWeek.Saturday);
            if (EditSunday) days.Add(DayOfWeek.Sunday);
            spec = new JobScheduleSpec
            {
                Kind = kind.Kind, Interval = interval, At = at, Days = days, DayOfMonth = (int)(EditDayOfMonth ?? 0), OnceAt = once, Cron = EditCron
            };
        }
        var draft = new JobDraft
        {
            Name = EditName.Trim(),
            Database = string.IsNullOrWhiteSpace(EditDatabase) ? null : EditDatabase.Trim(),
            Enabled = EditEnabled,
            Description = string.IsNullOrWhiteSpace(EditDescription) ? null : EditDescription,
            Command = CanEditCommand ? EditSql : _editing?.Command ?? "",
            Schedule = spec,
            Preserve = EditPreserve
        };
        return (draft, problem);
    }

    private void Recompute()
    {
        if (_loadingEditor || !IsEditing || _catalog is not { } catalog) return;
        var (draft, problem) = ReadEditor();
        problem ??= JobScriptBuilder.Problem(catalog, draft, _editing);
        SchedulePreview = PreviewOf(catalog, draft.Schedule);
        if (problem is not null)
        {
            EditorProblem = problem;
            EditorScript = "";
            SaveCommand.NotifyCanExecuteChanged();
            return;
        }
        try
        {
            var now = catalog.ServerNow ?? DateTime.Now;
            var script = _editing is null ? JobScriptBuilder.Create(catalog, draft, now) : JobScriptBuilder.Update(catalog, _editing, draft, now);
            EditorProblem = script.IsEmpty ? "Nothing changed yet." : "";
            EditorScript = script.Sql;
        }
        catch (ArgumentException ex)
        {
            EditorProblem = ex.Message;
            EditorScript = "";
        }
        SaveCommand.NotifyCanExecuteChanged();
    }

    private static string PreviewOf(JobCatalog catalog, JobScheduleSpec? spec)
    {
        if (spec is null) return "";
        if (spec.Problem(catalog.Engine) is not null) return "";
        var text = spec.Describe();
        if (catalog.Engine == JobEngine.PgCron)
        {
            try
            {
                if (CronSchedule.TryParse(JobScheduleText.ToCron(spec), out var cron, out _) && cron!.Seconds is null)
                {
                    var runs = JobCatalogLoader.NextCronRuns(cron, catalog.TimeZone, DateTime.UtcNow, 3);
                    if (runs.Count > 0) text += " · next " + string.Join(", ", runs.Select(r => r.ToString("ddd yyyy-MM-dd HH:mm", Invariant))) + " (your time)";
                }
            }
            catch (ArgumentException)
            {
                // Shown as the problem instead.
            }
        }
        return text;
    }

    private bool CanSave => IsEditing && !IsBusy && EditorProblem.Length == 0 && EditorScript.Length > 0;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (_catalog is not { } catalog) return;
        var (draft, _) = ReadEditor();
        var now = catalog.ServerNow ?? DateTime.Now;
        JobScript script;
        try
        {
            script = _editing is null ? JobScriptBuilder.Create(catalog, draft, now) : JobScriptBuilder.Update(catalog, _editing, draft, now);
        }
        catch (ArgumentException ex)
        {
            EditorProblem = ex.Message;
            return;
        }
        var message = _editing is null ? $"Create {draft.Name} ({draft.Schedule?.Describe()})?" : $"Save the changes to {_editing.Name}?";
        if (await ExecuteAsync(script, message, _editing is null ? "Create" : "Save", draft.Name))
        {
            IsEditing = false;
            _editing = null;
            EditorTitle = "";
        }
    }

    [RelayCommand]
    private void OpenEditorScript()
    {
        if (EditorScript.Length == 0 || _catalog is not { } catalog) return;
        OpenSqlRequested?.Invoke(EditorScript, catalog.Engine switch
        {
            JobEngine.PgCron => catalog.HomeDatabase,
            JobEngine.MySqlEvents => EditDatabase,
            _ => null
        });
    }

    [RelayCommand]
    private async Task CopyCommandAsync()
    {
        if (SelectedJob?.Job is { } job) await _dialogs.CopyTextAsync(JobScriptBuilder.CommandForQuery(job));
    }
}

public sealed record ScheduleChoice(ScheduleKind Kind, string Label)
{
    public static readonly IReadOnlyList<ScheduleChoice> All =
    [
        new(ScheduleKind.EveryMinutes, "Every N minutes"),
        new(ScheduleKind.EveryHours, "Every N hours"),
        new(ScheduleKind.Daily, "Daily"),
        new(ScheduleKind.Weekly, "Weekly"),
        new(ScheduleKind.Monthly, "Monthly"),
        new(ScheduleKind.Once, "Once"),
        new(ScheduleKind.Cron, "Cron expression")
    ];

    public override string ToString() => Label;
}

/// <summary>One job in the list.</summary>
public sealed class JobItem(ScheduledJob job, DateTime now)
{
    public ScheduledJob Job { get; } = job;
    public string Name => Job.Name;
    public string Schedule => Job.Enabled ? Job.ScheduleDescription : "Disabled · " + Job.ScheduleDescription;
    public bool IsRunning => Job.IsRunning;
    public bool IsFailed => !Job.IsRunning && Job.LastOutcome == JobOutcome.Failed;
    public bool IsDisabled => !Job.Enabled && !Job.IsRunning;

    /// <summary>Enabled, and not known to have succeeded: never run, or (MySQL) ran with no outcome kept.</summary>
    public bool IsIdle => !IsRunning && !IsFailed && !IsDisabled && Job.LastOutcome != JobOutcome.Succeeded;

    public string Badge => IsRunning ? "▶" : IsFailed ? "!" : IsDisabled ? "‖" : IsIdle ? "•" : "✓";

    public string StateTip => IsRunning ? "Running now" : IsFailed ? "The last run failed" : IsDisabled ? "Disabled" : Job.LastOutcome is null ? "Not run yet" : "Enabled";

    /// <summary>Second line: last run and next run.</summary>
    public string RunsText
    {
        get
        {
            var parts = new List<string>();
            if (Job.IsRunning) parts.Add("running" + (Job.RunningSince is { } since ? " since " + JobTimeText.When(since, now) : ""));
            else if (Job.LastRunAt is { } last)
                parts.Add(JobTimeText.Outcome(Job.LastOutcome) + " " + JobTimeText.When(last, now) + (Job.LastDuration is { } d ? " (" + JobTimeText.Duration(d) + ")" : ""));
            else parts.Add("not run yet");
            if (Job.NextRunAt is { } next && Job.Enabled) parts.Add("next " + JobTimeText.When(next, now));
            return string.Join(" · ", parts);
        }
    }

    public string Facts
    {
        get
        {
            var facts = new List<string>();
            if (Job.Database is { Length: > 0 } db) facts.Add((Job.Engine == JobEngine.MySqlEvents ? "database " : "runs in ") + db);
            if (Job.Owner is { Length: > 0 } owner) facts.Add((Job.Engine == JobEngine.MySqlEvents ? "definer " : "owner ") + owner);
            if (Job.Category is { Length: > 0 } category && category != "[Uncategorized (Local)]") facts.Add(category);
            if (Job.Steps.Count > 1) facts.Add($"{Job.Steps.Count} steps");
            if (Job.Engine == JobEngine.PgCron) facts.Add("job id " + Job.Id);
            if (Job.Engine == JobEngine.MySqlEvents && Job.ScheduleText.StartsWith("AT", StringComparison.Ordinal))
                facts.Add(Job.Preserve ? "kept after it runs" : "dropped after it runs");
            facts.Add(Job.Enabled ? "enabled" : "disabled");
            return string.Join(" · ", facts);
        }
    }
}

/// <summary>One row of a job's run history.</summary>
public sealed class JobRunItem(JobRun run, bool showStep)
{
    public JobRun Run { get; } = run;
    public string Started => Run.StartedAt?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "";
    public string Duration => Run.Duration is { } d ? JobTimeText.Duration(d) : "";
    public string Outcome => JobTimeText.Outcome(Run.Outcome);
    public bool IsFailed => Run.Outcome is JobOutcome.Failed or JobOutcome.Cancelled;
    public bool IsSucceeded => Run.Outcome == JobOutcome.Succeeded;
    public string Step => showStep ? Run.StepId == 0 ? "Job" : $"{Run.StepId}. {Run.StepName}" : "";
    public string Message => (Run.Message ?? "").ReplaceLineEndings(" ").Trim();
}

public static class JobTimeText
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string When(DateTime t, DateTime now)
    {
        var time = t.ToString("HH:mm", Invariant);
        if (t.Date == now.Date) return "today " + time;
        if (t.Date == now.Date.AddDays(-1)) return "yesterday " + time;
        if (t.Date == now.Date.AddDays(1)) return "tomorrow " + time;
        if (Math.Abs((t.Date - now.Date).TotalDays) < 7) return t.ToString("ddd HH:mm", Invariant);
        return t.ToString("yyyy-MM-dd HH:mm", Invariant);
    }

    public static string Duration(TimeSpan d) => d.TotalSeconds < 1 ? "<1 s" :
        d.TotalMinutes < 1 ? $"{(int)d.TotalSeconds} s" :
        d.TotalHours < 1 ? $"{(int)d.TotalMinutes} min {d.Seconds} s" :
        $"{(int)d.TotalHours} h {d.Minutes} min";

    public static string Outcome(JobOutcome? outcome) => outcome switch
    {
        JobOutcome.Succeeded => "succeeded",
        JobOutcome.Failed => "failed",
        JobOutcome.Running => "running",
        JobOutcome.Cancelled => "cancelled",
        JobOutcome.Retrying => "retrying",
        JobOutcome.Unknown => "ran",
        _ => "not run"
    };
}
