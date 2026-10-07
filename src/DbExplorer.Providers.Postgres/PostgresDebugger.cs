using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using DbExplorer.Core.Connections;
using DbExplorer.Core.Debugging;
using DbExplorer.Core.Models;
using Npgsql;
using NpgsqlTypes;

namespace DbExplorer.Providers.Postgres;

/// <summary>
/// PL/pgSQL debugging through the pldebugger extension (pldbgapi), the API pgAdmin uses. "Direct" debugging: the call
/// runs on a target connection that first asks to stop on entry (pldbg_oid_debug); when the routine starts, the server
/// sends a PLDBGBREAK notice with a port, and a second (proxy) connection attaches to it and drives the routine.
/// Both connections bypass the pool: a backend that ran pldbg_oid_debug must never be handed out again.
/// </summary>
public static class PostgresDebugger
{
    public const string Extension = "pldbgapi";
    public const string Library = "plugin_debugger";

    internal const string SupportSql = """
        SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'pldbgapi') AS installed,
               EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'pldbgapi') AS available,
               (SELECT setting FROM pg_settings WHERE name = 'shared_preload_libraries') AS preload,
               current_setting('server_version_num')::int AS version;
        """;

    internal const string TargetsSql = """
        SELECT p.oid::bigint, n.nspname, p.proname, p.oid::regprocedure::text, l.lanname, p.prokind::text, p.prosrc,
               ARRAY(SELECT format_type(t, NULL)
                     FROM unnest(COALESCE(p.proallargtypes, p.proargtypes::oid[])) WITH ORDINALITY u(t, i) ORDER BY i),
               p.proargmodes::text[], p.proargnames, p.pronargdefaults
        FROM pg_proc p
        JOIN pg_namespace n ON n.oid = p.pronamespace
        JOIN pg_language l ON l.oid = p.prolang
        WHERE n.nspname = @schema AND p.proname = @name
        ORDER BY p.oid;
        """;

    private static readonly Regex BreakNotice = new(@"^PLDBGBREAK:(\d+)$", RegexOptions.Compiled);

    /// <summary>The port in the notice the server sends when a routine stops on entry, or null for any other notice.</summary>
    public static int? ParseBreakNotice(string message)
    {
        var m = BreakNotice.Match(message.Trim());
        return m.Success && int.TryParse(m.Groups[1].Value, out var port) ? port : null;
    }

    /// <summary>
    /// What to tell the user given what the server reports. <paramref name="preload"/> is null when the setting is hidden
    /// from this login (not a superuser): then the debugger is tried and the server's own error explains a missing library.
    /// </summary>
    public static DebugSupport Support(bool installed, bool available, string? preload)
    {
        var preloaded = preload is null || preload.Split(',').Select(l => l.Trim().Trim('"')).Any(l =>
            l.Equals(Library, StringComparison.OrdinalIgnoreCase) || l.EndsWith("/" + Library, StringComparison.OrdinalIgnoreCase));
        if (installed && preloaded) return DebugSupport.Available;

        var steps = new List<string>();
        if (!installed && !available)
            steps.Add("Install the pldebugger package on the database server (Debian/Ubuntu: postgresql-NN-pldebugger; " +
                      "RHEL: pldebugger_NN; Windows: the EDB installer's StackBuilder, \"pgAdmin/pldebugger\").");
        if (!preloaded)
            steps.Add($"Add {Library} to shared_preload_libraries in postgresql.conf " +
                      $"(shared_preload_libraries = '{Library}'), then restart the server.");
        if (!installed)
            steps.Add($"In this database, run: CREATE EXTENSION {Extension};");

        var problem = !installed && !available ? "The PL/pgSQL debugger (pldebugger) is not installed on this server."
            : !installed ? $"The {Extension} extension is not created in this database."
            : $"The server does not preload {Library}, which the debugger needs.";
        var setup = string.Join("\n", steps.Select((s, i) => $"{i + 1}. {s}")) +
                    "\nThese steps need a superuser and (for the first two) access to the server itself.";
        return new DebugSupport(false, problem, setup);
    }

    /// <summary>Why a routine cannot be stepped through, or null when it can.</summary>
    public static string? ProblemOf(string language, string kind, string signature) =>
        kind is "a" or "w" ? $"{signature} is an aggregate; only functions and procedures can be debugged."
        : !language.Equals("plpgsql", StringComparison.OrdinalIgnoreCase)
            ? $"{signature} is written in {language}. The debugger steps through PL/pgSQL routines only; " +
              "statements inside a routine in another language run without stopping."
        : null;

    public static IReadOnlyList<DebugParameter> ParametersOf(
        IReadOnlyList<string> types, IReadOnlyList<string>? modes, IReadOnlyList<string?>? names, int defaultCount)
    {
        var list = new List<DebugParameter>();
        for (var i = 0; i < types.Count; i++)
        {
            var mode = (modes is null || i >= modes.Count ? "i" : modes[i]) switch
            {
                "o" or "t" => DebugParameterMode.Out,
                "b" => DebugParameterMode.InOut,
                "v" => DebugParameterMode.Variadic,
                _ => DebugParameterMode.In
            };
            var name = names is not null && i < names.Count ? names[i] ?? "" : "";
            list.Add(new DebugParameter(name, types[i], mode, false));
        }

        // pronargdefaults counts the last input parameters.
        var inputs = list.Select((p, i) => (p, i)).Where(x => x.p.TakesValue).Select(x => x.i).ToList();
        foreach (var i in inputs.Skip(Math.Max(0, inputs.Count - defaultCount)))
            list[i] = list[i] with { HasDefault = true };
        return list;
    }

    /// <summary>
    /// The statement that calls the routine and the text value of each placeholder ($1, $2…). Arguments are passed as
    /// untyped text and cast to the declared type, so any literal psql would accept works. Named notation is used when
    /// every parameter has a name, which lets any parameter with a default be left out.
    /// </summary>
    public static (string Sql, IReadOnlyList<string?> Values) BuildCall(DebugTarget target, IReadOnlyList<string?> arguments)
    {
        var inputs = target.Parameters.Where(p => p.TakesValue).ToList();
        if (arguments.Count != inputs.Count)
            throw new ArgumentException($"{target.Signature} takes {inputs.Count} argument(s); {arguments.Count} given.");

        var named = target.Parameters.All(p => p.Name.Length > 0);
        var values = new List<string?>();
        var parts = new List<string>();
        var input = 0;
        var skipped = false;
        foreach (var p in target.Parameters)
        {
            string expression;
            if (p.Mode == DebugParameterMode.Out)
            {
                // Functions return OUT parameters; procedures list them in CALL (a NULL placeholder).
                if (!target.IsProcedure) continue;
                expression = $"NULL::{p.DataType}";
            }
            else
            {
                var value = arguments[input++];
                if (value == DebugArguments.DefaultArgument)
                {
                    if (!p.HasDefault) throw new ArgumentException($"{Display(p)} has no default; give it a value or NULL.");
                    skipped = true;
                    continue;
                }
                if (skipped && !named)
                    throw new ArgumentException($"{Display(p)} follows a parameter left at its default; give the earlier one a value.");
                if (value is null) expression = $"NULL::{p.DataType}";
                else
                {
                    values.Add(value);
                    expression = $"${values.Count}::{p.DataType}";
                }
            }
            var arg = named ? $"{PostgresSql.Quote(p.Name)} => {expression}" : expression;
            parts.Add(p.Mode == DebugParameterMode.Variadic ? "VARIADIC " + arg : arg);
        }

        var name = PostgresSql.QuoteFullName(target.Schema, target.Name);
        var sql = target.IsProcedure
            ? $"CALL {name}({string.Join(", ", parts)})"
            : $"SELECT * FROM {name}({string.Join(", ", parts)})";
        return (sql, values);
    }

    private static string Display(DebugParameter p) => p.Name.Length > 0 ? p.Name : "an unnamed parameter";

    internal static async Task<DebugSupport> GetSupportAsync(ConnectionProfile profile, string? database, CancellationToken ct)
    {
        await using var cn = new NpgsqlConnection(PostgresSql.BuildConnectionString(profile, database));
        await cn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(SupportSql, cn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return Support(r.GetBoolean(0), r.GetBoolean(1), r.IsDBNull(2) ? null : r.GetString(2));
    }

    internal static async Task<IReadOnlyList<DebugTarget>> GetTargetsAsync(ConnectionProfile profile, DbObject routine, CancellationToken ct)
    {
        var database = string.IsNullOrEmpty(routine.Database) ? profile.Database : routine.Database;
        await using var cn = new NpgsqlConnection(PostgresSql.BuildConnectionString(profile, database));
        await cn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(TargetsSql, cn);
        cmd.Parameters.AddWithValue("schema", routine.Schema);
        cmd.Parameters.AddWithValue("name", routine.Name);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<DebugTarget>();
        while (await r.ReadAsync(ct))
        {
            var signature = r.GetString(3);
            var language = r.GetString(4);
            var kind = r.GetString(5);
            list.Add(new DebugTarget
            {
                Id = r.GetInt64(0),
                Database = database ?? "",
                Schema = r.GetString(1),
                Name = r.GetString(2),
                Signature = signature,
                Language = language,
                IsProcedure = kind == "p",
                Source = r.IsDBNull(6) ? "" : r.GetString(6),
                Parameters = ParametersOf(
                    r.GetFieldValue<string[]>(7),
                    r.IsDBNull(8) ? null : r.GetFieldValue<string[]>(8),
                    r.IsDBNull(9) ? null : r.GetFieldValue<string?[]>(9),
                    r.GetInt16(10)),
                Problem = ProblemOf(language, kind, signature)
            });
        }
        return list;
    }

    /// <summary>Connections of a debug session: never pooled, named so they are easy to spot in the server's activity.</summary>
    internal static string ConnectionString(ConnectionProfile profile, string? database, string role) =>
        new NpgsqlConnectionStringBuilder(PostgresSql.BuildConnectionString(profile, database))
        {
            Pooling = false,
            ApplicationName = $"DbExplorer debugger ({role})",
            CommandTimeout = 0
        }.ConnectionString;
}

/// <summary>One routine call under pldebugger; see <see cref="PostgresDebugger"/>.</summary>
internal sealed class PostgresDebugSession : IRoutineDebugSession, IDebugPrimitives
{
    /// <summary>How long the routine may take to reach its first statement (e.g. waiting for a lock in a default expression).</summary>
    private static readonly TimeSpan AttachTimeout = TimeSpan.FromSeconds(60);

    private readonly NpgsqlConnection _target;
    private readonly NpgsqlTransaction _transaction;
    private readonly NpgsqlConnection _proxy;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<DebugBreakpoint> _breakpoints = [];
    private readonly List<string> _messages = [];
    private readonly bool _commit;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Task<(List<QueryResultSet> Sets, string? Error)> _call = null!;
    private Task<DebugOutcome> _outcome = null!;
    private readonly string _connectionString;
    private readonly int _targetPid;
    private int _session;
    private bool _stopped;

    /// <summary>The call has returned (set before <see cref="Outcome"/> completes).</summary>
    private volatile bool _ended;

    public DebugTarget Target { get; }
    public DebugPosition Position { get; private set; } = DebugPosition.Finished;
    public IReadOnlyCollection<DebugBreakpoint> Breakpoints => _breakpoints;
    public Task<DebugOutcome> Outcome => _outcome;
    public event EventHandler<string>? MessageReceived;

    private PostgresDebugSession(DebugTarget target, NpgsqlConnection targetConnection, NpgsqlTransaction transaction,
        NpgsqlConnection proxy, bool commit, string connectionString)
    {
        _connectionString = connectionString;
        _targetPid = targetConnection.ProcessID;
        Target = target;
        _target = targetConnection;
        _transaction = transaction;
        _proxy = proxy;
        _commit = commit;
    }

    public static async Task<IRoutineDebugSession> StartAsync(
        ConnectionProfile profile, DebugTarget target, IReadOnlyList<string?> arguments,
        IReadOnlyCollection<DebugBreakpoint> breakpoints, bool commit, CancellationToken ct)
    {
        if (target.Problem is not null) throw new InvalidOperationException(target.Problem);
        if (profile.ReadOnly)
            throw new InvalidOperationException($"{profile.QualifiedName} is a read-only connection; routines are not debugged on it.");
        var (sql, values) = PostgresDebugger.BuildCall(target, arguments);
        var database = string.IsNullOrEmpty(target.Database) ? null : target.Database;

        var targetConnection = new NpgsqlConnection(PostgresDebugger.ConnectionString(profile, database, "target"));
        var proxyConnectionString = PostgresDebugger.ConnectionString(profile, database, "control");
        var proxy = new NpgsqlConnection(proxyConnectionString);
        PostgresDebugSession? session = null;
        try
        {
            await targetConnection.OpenAsync(ct);
            await proxy.OpenAsync(ct);
            var transaction = await targetConnection.BeginTransactionAsync(ct);
            session = new PostgresDebugSession(target, targetConnection, transaction, proxy, commit, proxyConnectionString);

            var port = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            targetConnection.Notice += (_, e) =>
            {
                if (PostgresDebugger.ParseBreakNotice(e.Notice.MessageText) is { } p) port.TrySetResult(p);
                else session.OnMessage(e.Notice.Severity + ": " + e.Notice.MessageText);
            };

            await using (var cmd = new NpgsqlCommand("SELECT pldbg_oid_debug(@oid::oid)", targetConnection, transaction))
            {
                cmd.Parameters.AddWithValue("oid", target.Id);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            session._call = session.RunCallAsync(sql, values);
            session._outcome = session.FinishAsync();

            var first = await Task.WhenAny(port.Task, session._call, Task.Delay(AttachTimeout, ct));
            ct.ThrowIfCancellationRequested();
            if (first != port.Task)
            {
                if (first == session._call)
                {
                    var (_, error) = await session._call;
                    throw new InvalidOperationException(error is null
                        ? $"{target.Signature} finished without stopping; the debugger could not attach (is {PostgresDebugger.Library} preloaded?)."
                        : "The call failed before its first statement: " + error);
                }
                throw new TimeoutException($"{target.Signature} did not reach its first statement within {AttachTimeout.TotalSeconds:N0} s.");
            }

            session._session = await session.ScalarAsync<int>("SELECT pldbg_attach_to_port(@a)", ct, port.Task.Result);
            await session.ScalarAsync<object?>("SELECT func FROM pldbg_wait_for_breakpoint(@s)", ct);
            foreach (var b in breakpoints) await session.AddBreakpointAsync(b, ct);
            session.Position = await session.ReadStackAsync(ct);
            return session;
        }
        catch (Exception ex)
        {
            if (session is not null) await session.DisposeAsync();
            else
            {
                await targetConnection.DisposeAsync();
                await proxy.DisposeAsync();
            }
            if (ex is PostgresException { SqlState: "08006" })
                throw new InvalidOperationException(
                    "The debugger lost its link to the routine while starting (" + ex.Message + "); nothing was changed. Start again.", ex);
            throw;
        }
    }

    private void OnMessage(string message)
    {
        lock (_messages) _messages.Add(message);
        MessageReceived?.Invoke(this, message);
    }

    private async Task<(List<QueryResultSet>, string?)> RunCallAsync(string sql, IReadOnlyList<string?> values)
    {
        var sets = new List<QueryResultSet>();
        try
        {
            await using var cmd = new NpgsqlCommand(sql, _target, _transaction) { CommandTimeout = 0 };
            for (var i = 0; i < values.Count; i++)
                cmd.Parameters.Add(new NpgsqlParameter { Value = values[i], NpgsqlDbType = NpgsqlDbType.Unknown });
            await using var reader = await cmd.ExecuteReaderAsync();
            do
            {
                if (reader.FieldCount == 0) continue;
                var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
                var rows = new List<IReadOnlyList<object?>>();
                while (await reader.ReadAsync())
                {
                    var row = new object?[reader.FieldCount];
                    for (var i = 0; i < row.Length; i++)
                    {
                        try { row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i); }
                        catch (InvalidCastException) { row[i] = reader.GetProviderSpecificValue(i)?.ToString(); }
                    }
                    rows.Add(row);
                }
                sets.Add(new QueryResultSet { Columns = columns, Rows = rows, TotalRowCount = rows.Count });
            } while (await reader.NextResultAsync());
            return (sets, null);
        }
        catch (PostgresException ex)
        {
            return (sets, ex.MessageText + (string.IsNullOrEmpty(ex.Where) ? "" : $" ({ex.Where.Split('\n')[0]})"));
        }
        catch (Exception ex)
        {
            return (sets, ex.Message);
        }
    }

    /// <summary>Once the call returns: commit when asked and it worked, otherwise roll back.</summary>
    private async Task<DebugOutcome> FinishAsync()
    {
        var (sets, error) = await _call;
        if (_stopped) error = "Stopped in the debugger.";
        var committed = false;
        try
        {
            if (error is null && _commit)
            {
                await _transaction.CommitAsync();
                committed = true;
            }
            else await _transaction.RollbackAsync();
        }
        catch (Exception ex)
        {
            error ??= (committed ? "" : "Commit failed: ") + ex.Message;
        }
        // Closing the (unpooled) target ends its backend, which drops the debugger link at once; otherwise the move
        // waiting on the control connection only notices after the server's own 10 s wait.
        try { await _target.CloseAsync(); }
        catch { /* already broken */ }
        _ended = true;
        Position = DebugPosition.Finished;
        string[] messages;
        lock (_messages) messages = [.. _messages];
        return new DebugOutcome { ResultSets = sets, Messages = messages, Error = error, Committed = committed, Elapsed = _clock.Elapsed };
    }

    public Task<DebugPosition> StepIntoAsync(CancellationToken ct = default) => Locked(() => MoveAsync("pldbg_step_into", ct), ct);

    public Task<DebugPosition> StepOverAsync(CancellationToken ct = default) =>
        Locked(() => DebugStepper.StepOverAsync(Primitives, Position, IsBreakpoint, ct), ct);

    public Task<DebugPosition> StepOutAsync(CancellationToken ct = default) =>
        Locked(() => DebugStepper.StepOutAsync(Primitives, Position, IsBreakpoint, ct), ct);

    public Task<DebugPosition> ContinueAsync(CancellationToken ct = default) => Locked(() => MoveAsync("pldbg_continue", ct), ct);

    // The primitives run inside the lock already taken by the public moves.
    private IDebugPrimitives Primitives => this;
    Task<DebugPosition> IDebugPrimitives.StepIntoAsync(CancellationToken ct) => MoveAsync("pldbg_step_into", ct);
    Task<DebugPosition> IDebugPrimitives.StepOverAsync(CancellationToken ct) => MoveAsync("pldbg_step_over", ct);
    Task<DebugPosition> IDebugPrimitives.ContinueAsync(CancellationToken ct) => MoveAsync("pldbg_continue", ct);

    private bool IsBreakpoint(DebugFrame frame) => _breakpoints.Contains(new DebugBreakpoint(frame.RoutineId, frame.Line));

    private async Task<T> Locked<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await action(); }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// One move; it returns when the routine pauses again. When the call ends, the server closes the debugger link and
    /// the move fails with "debugger connection terminated": that is the normal end, told apart by the call completing.
    /// </summary>
    private async Task<DebugPosition> MoveAsync(string function, CancellationToken ct)
    {
        if (_ended) return Position = DebugPosition.Finished;
        try
        {
            await ScalarAsync<object?>($"SELECT func FROM {function}(@s)", ct);
            Position = await ReadStackAsync(ct);
            return Position;
        }
        catch (Exception ex) when (ex is PostgresException or NpgsqlException)
        {
            if (await Task.WhenAny(_outcome, Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None)) == _outcome)
            {
                await _outcome;
                return Position = DebugPosition.Finished;
            }
            // The routine is still running but the link to it broke (pldebugger gives up when a server signal, e.g.
            // from a DROP DATABASE elsewhere, interrupts its wait). Nothing can resynchronise the link: end the call.
            _stopped = true;
            await TerminateTargetAsync();
            throw new InvalidOperationException(
                "The debugger lost its link to the routine (" + ex.Message + "). The call was stopped and its changes rolled back; start again.", ex);
        }
    }

    /// <summary>
    /// Ends the target backend from a connection of its own; its open transaction is rolled back by the server. A target
    /// that stopped at a breakpoint with no debugger attached waits in accept() and ignores pg_terminate_backend (it would
    /// hold its locks, and block DROP DATABASE anywhere on the server, forever), so then a fresh control connection
    /// attaches to it and aborts it.
    /// </summary>
    private async Task TerminateTargetAsync()
    {
        await RunRescueAsync("SELECT pg_terminate_backend(@pid)");
        if (await Task.WhenAny(_outcome, Task.Delay(TimeSpan.FromSeconds(2))) == _outcome) return;
        await RunRescueAsync("""
            SELECT pldbg_abort_target(pldbg_attach_to_port(id))
            FROM pg_stat_get_backend_idset() id
            WHERE pg_stat_get_backend_pid(id) = @pid
            """);
        await Task.WhenAny(_outcome, Task.Delay(TimeSpan.FromSeconds(5)));
    }

    private async Task RunRescueAsync(string sql)
    {
        try
        {
            await using var cn = new NpgsqlConnection(_connectionString);
            await cn.OpenAsync();
            await using var cmd = new NpgsqlCommand(sql, cn) { CommandTimeout = 10 };
            cmd.Parameters.AddWithValue("pid", _targetPid);
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // Best effort: closing the target connection is the last resort.
        }
    }

    private async Task<DebugPosition> ReadStackAsync(CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT level, targetname, func::bigint, linenumber, args FROM pldbg_get_stack(@s) ORDER BY level", _proxy);
        cmd.Parameters.AddWithValue("s", _session);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var frames = new List<DebugFrame>();
        while (await r.ReadAsync(ct))
            frames.Add(new DebugFrame(r.GetInt32(0), r.GetInt64(2), r.IsDBNull(1) ? "" : r.GetString(1), r.GetInt32(3),
                r.IsDBNull(4) ? "" : r.GetString(4)));
        return new DebugPosition(frames);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_ended) return;
        _stopped = true;
        // Not behind the lock: a long-running move (continue into a slow statement) is exactly what Stop interrupts…
        // but the proxy connection is busy then, so abort through a connection of its own.
        if (_gate.CurrentCount == 0)
        {
            await using var cancel = new NpgsqlConnection(_connectionString);
            await cancel.OpenAsync(ct);
            await using var kill = new NpgsqlCommand("SELECT pg_cancel_backend(@pid)", cancel);
            kill.Parameters.AddWithValue("pid", _targetPid);
            await kill.ExecuteScalarAsync(ct);
        }
        else
        {
            await Locked(async () =>
            {
                try { await ScalarAsync<object?>("SELECT pldbg_abort_target(@s)", ct); }
                catch (PostgresException) { /* already gone */ }
                return true;
            }, ct);
        }
        await Task.WhenAny(_outcome, Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None));
    }

    public Task<IReadOnlyList<DebugVariable>> GetVariablesAsync(int frameLevel, CancellationToken ct = default) =>
        Locked<IReadOnlyList<DebugVariable>>(async () =>
        {
            if (_ended) return [];
            if (frameLevel != 0) await ScalarAsync<object?>("SELECT func FROM pldbg_select_frame(@s, @a)", ct, frameLevel);
            try
            {
                await using var cmd = new NpgsqlCommand(
                    "SELECT name, varclass::text, linenumber, isconst, isnotnull, format_type(dtype, NULL), value FROM pldbg_get_variables(@s)", _proxy);
                cmd.Parameters.AddWithValue("s", _session);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                var list = new List<DebugVariable>();
                while (await r.ReadAsync(ct))
                {
                    var kind = r.IsDBNull(1) ? "" : r.GetString(1);
                    list.Add(new DebugVariable(
                        r.GetString(0),
                        kind == "A" ? DebugVariableKind.Argument : kind == "L" ? DebugVariableKind.Local : DebugVariableKind.Other,
                        r.IsDBNull(5) ? "" : r.GetString(5),
                        r.IsDBNull(6) ? null : r.GetString(6),
                        r.IsDBNull(2) ? 0 : r.GetInt32(2),
                        !r.IsDBNull(3) && r.GetBoolean(3),
                        !r.IsDBNull(4) && r.GetBoolean(4)));
                }
                return list;
            }
            finally
            {
                if (frameLevel != 0) await ScalarAsync<object?>("SELECT func FROM pldbg_select_frame(@s, 0)", ct);
            }
        }, ct);

    public Task<bool> SetVariableAsync(string name, string value, CancellationToken ct = default) =>
        Locked(async () => !_ended &&
                           await ScalarAsync<bool>("SELECT pldbg_deposit_value(@s, @a, -1, @b)", ct, name, value), ct);

    public Task<string> GetSourceAsync(long routineId, CancellationToken ct = default) =>
        Locked(async () => routineId == Target.Id
            ? Target.Source
            : await ScalarAsync<string?>("SELECT pldbg_get_source(@s, @a::oid)", ct, routineId) ?? "", ct);

    public Task<bool> AddBreakpointAsync(DebugBreakpoint breakpoint, CancellationToken ct = default) =>
        Locked(async () =>
        {
            if (_ended) return false;
            var ok = await ScalarAsync<bool>("SELECT pldbg_set_breakpoint(@s, @a::oid, @b)", ct, breakpoint.RoutineId, breakpoint.Line);
            if (ok) _breakpoints.Add(breakpoint);
            return ok;
        }, ct);

    public Task RemoveBreakpointAsync(DebugBreakpoint breakpoint, CancellationToken ct = default) =>
        Locked(async () =>
        {
            if (!_breakpoints.Remove(breakpoint) || _ended) return false;
            return await ScalarAsync<bool>("SELECT pldbg_drop_breakpoint(@s, @a::oid, @b)", ct, breakpoint.RoutineId, breakpoint.Line);
        }, ct);

    private async Task<T> ScalarAsync<T>(string sql, CancellationToken ct, object? a = null, object? b = null)
    {
        await using var cmd = new NpgsqlCommand(sql, _proxy);
        cmd.Parameters.AddWithValue("s", _session);
        if (a is not null) cmd.Parameters.AddWithValue("a", a);
        if (b is not null) cmd.Parameters.AddWithValue("b", b);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? default! : (T)result;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_ended && _outcome is not null)
        {
            try { await StopAsync(); }
            catch { /* ended below */ }
        }
        try { await _proxy.DisposeAsync(); }
        catch { /* a broken link */ }
        if (_outcome is not null)
        {
            await Task.WhenAny(_outcome, Task.Delay(TimeSpan.FromSeconds(5)));
            if (!_ended) await TerminateTargetAsync();
        }
        // Unpooled: closing ends the backend, so an unfinished transaction is rolled back by the server.
        try { await _target.DisposeAsync(); }
        catch { /* already terminated */ }
        _gate.Dispose();
    }
}
