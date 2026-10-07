namespace DbExplorer.Core.Debugging;

/// <summary>Whether routines of a connection can be stepped through, and if not, what the user can do about it.</summary>
public sealed record DebugSupport(bool IsAvailable, string? Problem = null, string? Setup = null)
{
    public static DebugSupport Available { get; } = new(true);
}

public enum DebugParameterMode { In, Out, InOut, Variadic }

/// <summary>One declared parameter of a routine to debug, in call order.</summary>
public sealed record DebugParameter(string Name, string DataType, DebugParameterMode Mode, bool HasDefault)
{
    /// <summary>Whether the caller passes a value (OUT parameters are only returned).</summary>
    public bool TakesValue => Mode != DebugParameterMode.Out;
}

/// <summary>One overload of a routine, as the debugger addresses it.</summary>
public sealed record DebugTarget
{
    /// <summary>The engine's id of the routine (PostgreSQL: pg_proc.oid).</summary>
    public long Id { get; init; }
    public string Database { get; init; } = "";
    public string Schema { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>"sales.add_order(integer, text)": tells overloads apart.</summary>
    public string Signature { get; init; } = "";
    public string Language { get; init; } = "";
    public bool IsProcedure { get; init; }
    public IReadOnlyList<DebugParameter> Parameters { get; init; } = [];

    /// <summary>The body the debugger's line numbers refer to (line 1 is the first line of the body).</summary>
    public string Source { get; init; } = "";

    /// <summary>Null when the routine can be debugged; otherwise why not (e.g. it is written in SQL, not PL/pgSQL).</summary>
    public string? Problem { get; init; }
}

/// <summary>One routine on the call stack. Level 0 is the innermost (the one about to run <see cref="Line"/>).</summary>
public sealed record DebugFrame(int Level, long RoutineId, string Routine, int Line, string Arguments);

public enum DebugVariableKind { Argument, Local, Other }

public sealed record DebugVariable(string Name, DebugVariableKind Kind, string DataType, string? Value, int DeclaredLine, bool IsConstant, bool IsNotNull)
{
    public string KindText => Kind switch { DebugVariableKind.Argument => "arg", DebugVariableKind.Local => "local", _ => "" };
    public string ValueText => Value ?? "NULL";
}

public sealed record DebugBreakpoint(long RoutineId, int Line);

/// <summary>Where the routine is paused, or that it has finished.</summary>
public sealed record DebugPosition(IReadOnlyList<DebugFrame> Stack)
{
    public static DebugPosition Finished { get; } = new([]);

    public bool IsFinished => Stack.Count == 0;
    public DebugFrame? Top => Stack.Count == 0 ? null : Stack[0];
    public int Depth => Stack.Count;
}

/// <summary>How the debugged call ended.</summary>
public sealed record DebugOutcome
{
    public IReadOnlyList<Models.QueryResultSet> ResultSets { get; init; } = [];
    public IReadOnlyList<string> Messages { get; init; } = [];

    /// <summary>The call failed (or was stopped); its changes were rolled back.</summary>
    public string? Error { get; init; }

    /// <summary>The call's changes were committed (only when asked for and the call succeeded).</summary>
    public bool Committed { get; init; }
    public TimeSpan Elapsed { get; init; }
}

/// <summary>
/// A routine paused under the debugger. The call runs on its own connection inside a transaction that is rolled back
/// at the end unless the session was started with commit on; a second connection drives it. Stepping methods wait
/// until the routine pauses again or finishes, and return where it is.
/// </summary>
public interface IRoutineDebugSession : IAsyncDisposable
{
    DebugTarget Target { get; }
    DebugPosition Position { get; }
    IReadOnlyCollection<DebugBreakpoint> Breakpoints { get; }

    Task<DebugPosition> StepIntoAsync(CancellationToken ct = default);
    Task<DebugPosition> StepOverAsync(CancellationToken ct = default);
    Task<DebugPosition> StepOutAsync(CancellationToken ct = default);
    Task<DebugPosition> ContinueAsync(CancellationToken ct = default);

    /// <summary>Stops the call; it ends with an error and its changes are rolled back.</summary>
    Task StopAsync(CancellationToken ct = default);

    Task<IReadOnlyList<DebugVariable>> GetVariablesAsync(int frameLevel, CancellationToken ct = default);

    /// <summary>Changes a variable of the innermost frame. Returns false when the engine refused the value.</summary>
    Task<bool> SetVariableAsync(string name, string value, CancellationToken ct = default);

    /// <summary>The body of any routine on the stack (or reachable from it), numbered like <see cref="DebugFrame.Line"/>.</summary>
    Task<string> GetSourceAsync(long routineId, CancellationToken ct = default);

    Task<bool> AddBreakpointAsync(DebugBreakpoint breakpoint, CancellationToken ct = default);
    Task RemoveBreakpointAsync(DebugBreakpoint breakpoint, CancellationToken ct = default);

    /// <summary>RAISE NOTICE and other server messages of the call, as they arrive (on a background thread).</summary>
    event EventHandler<string>? MessageReceived;

    /// <summary>Completes when the call has finished (after <see cref="Position"/> turns finished).</summary>
    Task<DebugOutcome> Outcome { get; }
}

/// <summary>Implemented by providers whose engine can pause a stored routine and step through it.</summary>
public interface IRoutineDebugProvider
{
    /// <summary>Checks the server side (extension installed, library preloaded) for the database.</summary>
    Task<DebugSupport> GetDebugSupportAsync(string? database, CancellationToken ct = default);

    /// <summary>Every overload of the routine with its parameters and body.</summary>
    Task<IReadOnlyList<DebugTarget>> GetDebugTargetsAsync(Models.DbObject routine, CancellationToken ct = default);

    /// <summary>
    /// Calls the routine with <paramref name="arguments"/> (text, in <see cref="DebugTarget.Parameters"/> order of the
    /// parameters that take a value; null is NULL, <see cref="DebugArguments.DefaultArgument"/> leaves a parameter with a default out)
    /// and returns once it is paused on its first statement, with <paramref name="breakpoints"/> set.
    /// </summary>
    Task<IRoutineDebugSession> StartDebugAsync(
        DebugTarget target, IReadOnlyList<string?> arguments, IReadOnlyCollection<DebugBreakpoint> breakpoints,
        bool commit, CancellationToken ct = default);
}

public static class DebugArguments
{
    /// <summary>Passed as an argument to leave a parameter that has a default out of the call.</summary>
    public const string DefaultArgument = "\u0000DEFAULT";
}
