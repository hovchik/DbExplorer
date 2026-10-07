using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Debugging;
using DbExplorer.Core.Models;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>The value typed for one parameter before a debug run.</summary>
public sealed partial class DebugParameterInput : ObservableObject
{
    public required DebugParameter Parameter { get; init; }
    public string Name => Parameter.Name.Length > 0 ? Parameter.Name : "(unnamed)";
    public string DataType => Parameter.DataType + Parameter.Mode switch
    {
        DebugParameterMode.InOut => " · in/out",
        DebugParameterMode.Variadic => " · variadic",
        _ => ""
    };
    public bool HasDefault => Parameter.HasDefault;

    [ObservableProperty] private string _value = "";

    /// <summary>Leave the parameter out of the call so its declared default applies.</summary>
    [ObservableProperty] private bool _useDefault;

    /// <summary>Pass NULL. An empty box is an empty string for text types, so NULL is explicit.</summary>
    [ObservableProperty] private bool _isNull;

    public string? Argument => UseDefault ? DebugArguments.DefaultArgument : IsNull ? null : Value;
}

/// <summary>One line of the call stack list.</summary>
public sealed record DebugFrameItem(DebugFrame Frame)
{
    public string Text => $"{Frame.Routine}  line {Frame.Line}" + (Frame.Arguments.Length > 0 ? $"   ({Frame.Arguments})" : "");
}

/// <summary>
/// Steps through a PL/pgSQL function or procedure: parameter values before the run, breakpoints in the body, step
/// over/into/out and continue, the call stack and each frame's variables. The call runs in a transaction rolled back at
/// the end unless "Commit when finished" is on.
/// </summary>
public partial class RoutineDebuggerViewModel : ViewModelBase, IAsyncDisposable
{
    private DatabaseSession? _session;
    private DbObject? _routine;
    private IRoutineDebugProvider? _engine;
    private IRoutineDebugSession? _debug;
    private readonly Dictionary<long, string> _sources = [];
    private readonly HashSet<DebugBreakpoint> _breakpoints = [];
    private readonly StringBuilder _output = new();

    [ObservableProperty] private string _title = "Debug";
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "";

    /// <summary>Why debugging cannot start here, with what to do about it; empty when it can.</summary>
    [ObservableProperty] private string _problem = "";
    [ObservableProperty] private string _setup = "";

    [ObservableProperty] private IReadOnlyList<DebugTarget> _targets = [];
    [ObservableProperty] private DebugTarget? _selectedTarget;
    [ObservableProperty] private bool _commitWhenFinished;

    [ObservableProperty] private DebugPosition _position = DebugPosition.Finished;
    [ObservableProperty] private IReadOnlyList<DebugFrameItem> _stack = [];
    [ObservableProperty] private DebugFrameItem? _selectedFrame;
    [ObservableProperty] private IReadOnlyList<DebugVariable> _variables = [];

    /// <summary>The routine whose body is shown (the selected frame's, or the target before a run).</summary>
    [ObservableProperty] private long _sourceRoutineId;
    [ObservableProperty] private string _sourceTitle = "";
    [ObservableProperty] private string _sourceText = "";

    /// <summary>Line the shown routine is paused on (1-based), or 0.</summary>
    [ObservableProperty] private int _currentLine;

    /// <summary>The shown frame is the innermost one (where execution continues), not a caller.</summary>
    [ObservableProperty] private bool _isCurrentFrame = true;

    [ObservableProperty] private string _outputText = "";
    [ObservableProperty] private IReadOnlyList<ResultSetView> _resultSets = [];

    public ObservableCollection<DebugParameterInput> Parameters { get; } = [];

    /// <summary>Asked before a run starts (set on production connections).</summary>
    public Func<bool, Task<bool>>? ConfirmStart { get; set; }

    /// <summary>Raised when the set of breakpoints changes, so the editor margin redraws.</summary>
    public event EventHandler? BreakpointsChanged;

    public bool IsRunning => _debug is not null && !Position.IsFinished;

    /// <summary>The engine has a debugger at all (false on SQL Server: only the explanation is shown).</summary>
    public bool HasDebugger => _engine is not null;

    public bool HasOverloads => Targets.Count > 1;

    partial void OnTargetsChanged(IReadOnlyList<DebugTarget> value) => OnPropertyChanged(nameof(HasOverloads));
    public bool CanEditParameters => !IsRunning && !IsBusy;

    public string Hint =>
        "Click in the margin (or F9) to set a breakpoint. F5 start/continue · F10 step over · F11 step into · Shift+F11 step out · Shift+F5 stop. " +
        "The run is one transaction, rolled back at the end unless \"Commit when finished\" is on.";

    public async Task InitializeAsync(DatabaseSession session, DbObject routine, CancellationToken ct = default)
    {
        _session = session;
        _routine = routine;
        Title = $"Debug {routine.Schema}.{routine.Name}";
        try
        {
            if (session.Provider is not IRoutineDebugProvider engine)
            {
                (Problem, Setup) = Unsupported(session.Provider.ProviderKey);
                return;
            }
            _engine = engine;
            OnPropertyChanged(nameof(HasDebugger));
            if (session.Profile.ReadOnly)
            {
                Problem = $"{session.Profile.QualifiedName} is a read-only connection, so routines are not debugged on it.";
                Setup = "A debug run executes the routine, which can change data. Turn off \"Read-only\" in the connection's settings to debug here.";
            }

            var database = string.IsNullOrEmpty(routine.Database) ? null : routine.Database;
            var support = await engine.GetDebugSupportAsync(database, ct);
            if (!support.IsAvailable && Problem.Length == 0)
            {
                Problem = support.Problem ?? "Debugging is not available on this server.";
                Setup = support.Setup ?? "";
            }

            Targets = await engine.GetDebugTargetsAsync(routine, ct);
            SelectedTarget = Targets.FirstOrDefault(t => t.Problem is null) ?? Targets.FirstOrDefault();
            if (Targets.Count == 0) Status = "The routine was not found; refresh the catalog.";
        }
        catch (Exception ex)
        {
            Problem = "Could not check the debugger: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
            NotifyCommands();
        }
    }

    /// <summary>What to say on an engine without a debugger.</summary>
    public static (string Problem, string Setup) Unsupported(string providerKey) =>
        providerKey.Contains("SqlServer", StringComparison.OrdinalIgnoreCase)
            ? ("SQL Server has no supported debugger for stored procedures.",
               "Microsoft removed the T-SQL debugger from SSMS 18 and has no API for stepping through a procedure on the " +
               "server, so DbExplorer does not pretend to. What works instead:\n" +
               "• Execute… runs the procedure with parameters and shows every result set and output value.\n" +
               "• Copy the body into a Query tab, declare the parameters as variables, and run it statement by statement " +
               "(select a statement and press F5); add SELECT or PRINT lines to see intermediate values.\n" +
               "• Wrap that in BEGIN TRAN … ROLLBACK to keep the data unchanged.\n" +
               "PostgreSQL PL/pgSQL functions and procedures can be stepped through here.")
            : ($"Debugging is not available for {providerKey} connections.", "PostgreSQL PL/pgSQL functions and procedures can be stepped through.");

    partial void OnSelectedTargetChanged(DebugTarget? value)
    {
        Parameters.Clear();
        if (value is null) return;
        foreach (var p in value.Parameters.Where(p => p.TakesValue))
            Parameters.Add(new DebugParameterInput { Parameter = p, UseDefault = p.HasDefault });
        if (!IsRunning) ShowSource(value.Id, value.Signature, value.Source, 0, current: true);
        Status = value.Problem ?? (Problem.Length > 0 ? "" : "Set breakpoints if you like, fill in the parameters and press Start (F5).");
        NotifyCommands();
    }

    partial void OnIsBusyChanged(bool value) => NotifyCommands();

    partial void OnPositionChanged(DebugPosition value) => NotifyCommands();

    private void NotifyCommands()
    {
        StartOrContinueCommand.NotifyCanExecuteChanged();
        StepOverCommand.NotifyCanExecuteChanged();
        StepIntoCommand.NotifyCanExecuteChanged();
        StepOutCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(CanEditParameters));
        OnPropertyChanged(nameof(StartText));
    }

    public string StartText => IsRunning ? "Continue" : "Start";

    private bool CanStartOrContinue => !IsBusy && (IsRunning || CanStart);
    private bool CanStart => !IsLoading && Problem.Length == 0 && SelectedTarget is { Problem: null } && _engine is not null;
    private bool CanStep => !IsBusy && IsRunning;
    private bool CanStop => IsRunning;

    [RelayCommand(CanExecute = nameof(CanStartOrContinue))]
    private Task StartOrContinueAsync() => IsRunning ? MoveAsync(s => s.ContinueAsync()) : StartAsync();

    [RelayCommand(CanExecute = nameof(CanStep))]
    private Task StepOverAsync() => MoveAsync(s => s.StepOverAsync());

    [RelayCommand(CanExecute = nameof(CanStep))]
    private Task StepIntoAsync() => MoveAsync(s => s.StepIntoAsync());

    [RelayCommand(CanExecute = nameof(CanStep))]
    private Task StepOutAsync() => MoveAsync(s => s.StepOutAsync());

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        if (_debug is null) return;
        Status = "Stopping…";
        try
        {
            await _debug.StopAsync();
        }
        catch (Exception ex)
        {
            Status = "Stop failed: " + ex.Message;
        }
    }

    private async Task StartAsync()
    {
        if (_engine is null || SelectedTarget is not { } target || _session is null) return;
        if (ConfirmStart is not null && !await ConfirmStart(CommitWhenFinished)) return;

        IsBusy = true;
        ResultSets = [];
        _output.Clear();
        OutputText = "";
        Status = "Starting…";
        try
        {
            var arguments = Parameters.Select(p => p.Argument).ToList();
            await DisposeSessionAsync();
            var debug = await Task.Run(() => _engine.StartDebugAsync(target, arguments, _breakpoints.ToList(), CommitWhenFinished));
            _debug = debug;
            debug.MessageReceived += (_, m) => Dispatcher.UIThread.Post(() => AppendOutput(m));
            _ = WatchOutcomeAsync(debug);
            AppendOutput($"Started {target.Signature}" + (CommitWhenFinished ? " (commits when it finishes)." : " (rolled back when it finishes)."));
            await ShowPositionAsync(debug.Position);
        }
        catch (Exception ex)
        {
            Status = "Could not start: " + ex.Message;
            AppendOutput(Status);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task MoveAsync(Func<IRoutineDebugSession, Task<DebugPosition>> move)
    {
        if (_debug is not { } debug) return;
        IsBusy = true;
        Status = "Running…";
        try
        {
            var position = await Task.Run(() => move(debug));
            await ShowPositionAsync(position);
        }
        catch (Exception ex)
        {
            if (debug.Outcome.IsCompleted)
            {
                await ShowPositionAsync(DebugPosition.Finished);
                AppendOutput(ex.Message);
            }
            Status = "Error: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ShowPositionAsync(DebugPosition position)
    {
        Position = position;
        Stack = position.Stack.Select(f => new DebugFrameItem(f)).ToList();
        if (position.IsFinished)
        {
            Variables = [];
            CurrentLine = 0;
            if (_debug is not null) await ShowOutcomeAsync(await _debug.Outcome);
            return;
        }
        Status = $"Paused at line {position.Top!.Line} of {position.Top.Routine}" +
                 (_breakpoints.Contains(new DebugBreakpoint(position.Top.RoutineId, position.Top.Line)) ? " (breakpoint)." : ".");
        SelectedFrame = Stack[0];
    }

    partial void OnSelectedFrameChanged(DebugFrameItem? value)
    {
        if (value is not null) _ = ShowFrameAsync(value.Frame);
    }

    private async Task ShowFrameAsync(DebugFrame frame)
    {
        if (_debug is not { } debug) return;
        try
        {
            if (!_sources.TryGetValue(frame.RoutineId, out var source))
                _sources[frame.RoutineId] = source = await Task.Run(() => debug.GetSourceAsync(frame.RoutineId));
            ShowSource(frame.RoutineId, frame.Routine, source, frame.Line, current: frame.Level == 0);
            Variables = await Task.Run(() => debug.GetVariablesAsync(frame.Level));
        }
        catch (Exception ex)
        {
            Status = "Could not read the frame: " + ex.Message;
        }
    }

    private void ShowSource(long routineId, string title, string source, int line, bool current)
    {
        SourceRoutineId = routineId;
        SourceTitle = title;
        SourceText = source;
        IsCurrentFrame = current;
        CurrentLine = line;
    }

    private async Task WatchOutcomeAsync(IRoutineDebugSession debug)
    {
        // A stop while a move is waiting ends the move too; this only covers the result arriving on its own.
        try { await debug.Outcome; }
        catch { return; }
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (_debug == debug && !Position.IsFinished && !IsBusy) await ShowPositionAsync(DebugPosition.Finished);
        });
    }

    private bool _outcomeShown;

    private Task ShowOutcomeAsync(DebugOutcome outcome)
    {
        if (_outcomeShown || _session is null) return Task.CompletedTask;
        _outcomeShown = true;
        ResultSets = outcome.ResultSets.Select((rs, i) => ResultSetView.From($"Result {i + 1}", rs, _session)).ToList();
        Status = outcome.Error is not null
            ? $"Ended with an error after {outcome.Elapsed.TotalSeconds:N1} s: {outcome.Error} Changes were rolled back."
            : $"Finished in {outcome.Elapsed.TotalSeconds:N1} s. " + (outcome.Committed ? "Changes were committed." : "Changes were rolled back.");
        AppendOutput(Status);
        NotifyCommands();
        return Task.CompletedTask;
    }

    private void AppendOutput(string line)
    {
        _output.AppendLine(line);
        OutputText = _output.ToString();
    }

    /// <summary>Sets or clears the breakpoint on a line of the routine shown in the editor.</summary>
    public async Task ToggleBreakpointAsync(int line)
    {
        if (line < 1 || SourceRoutineId == 0 || IsBusy) return;
        var breakpoint = new DebugBreakpoint(SourceRoutineId, line);
        var add = !_breakpoints.Contains(breakpoint);
        if (add) _breakpoints.Add(breakpoint);
        else _breakpoints.Remove(breakpoint);
        BreakpointsChanged?.Invoke(this, EventArgs.Empty);

        if (_debug is not { } debug || !IsRunning) return;
        try
        {
            if (add && !await Task.Run(() => debug.AddBreakpointAsync(breakpoint)))
            {
                _breakpoints.Remove(breakpoint);
                BreakpointsChanged?.Invoke(this, EventArgs.Empty);
                Status = $"No breakpoint can be set on line {line}.";
            }
            else if (!add) await Task.Run(() => debug.RemoveBreakpointAsync(breakpoint));
        }
        catch (Exception ex)
        {
            Status = "Breakpoint not changed: " + ex.Message;
        }
    }

    public bool HasBreakpoint(int line) => _breakpoints.Contains(new DebugBreakpoint(SourceRoutineId, line));

    /// <summary>Changes a variable of the innermost frame while paused.</summary>
    public async Task SetVariableAsync(DebugVariable variable, string value)
    {
        if (_debug is not { } debug || !IsRunning || IsBusy || SelectedFrame?.Frame.Level != 0) return;
        try
        {
            var ok = await Task.Run(() => debug.SetVariableAsync(variable.Name, value));
            Status = ok ? $"{variable.Name} set to {value}." : $"{value} is not a valid {variable.DataType}; {variable.Name} is unchanged.";
            Variables = await Task.Run(() => debug.GetVariablesAsync(0));
        }
        catch (Exception ex)
        {
            Status = "Could not set the variable: " + ex.Message;
        }
    }

    private async Task DisposeSessionAsync()
    {
        _outcomeShown = false;
        if (_debug is null) return;
        var debug = _debug;
        _debug = null;
        await debug.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        try { await DisposeSessionAsync(); }
        catch { /* the window is closing; the server rolls back when the connections drop */ }
    }
}
