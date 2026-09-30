using System.Text.RegularExpressions;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Query;

public sealed class QueryExecutionService
{
    private static readonly Regex DestructiveKeyword = new(
        @"\b(INSERT|UPDATE|DELETE|DROP|ALTER|TRUNCATE|CREATE|MERGE|EXEC|EXECUTE|CALL|GRANT|REVOKE)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(2));

    /// <summary>True when the script looks like it could modify data or schema (used to prompt for confirmation).</summary>
    public bool IsPotentiallyDestructive(string sql) => DestructiveKeyword.IsMatch(sql);

    /// <summary>
    /// Runs a script. With a row limit, a script that only reads (<see cref="ReadOnlyScriptAnalyzer"/>) stops each
    /// result set on the server just past the limit, so a SELECT over a huge table costs no more than the rows shown;
    /// any other script keeps running to the end, reading and discarding the rows beyond the limit.
    /// </summary>
    public Task<QueryExecutionResult> ExecuteScriptAsync(
        DatabaseSession session, string sql, string? database, int timeoutSeconds, CancellationToken ct = default,
        int maxRows = int.MaxValue) =>
        session.Provider.ExecuteScriptAsync(sql, database, timeoutSeconds, ct, maxRows, ReadOnlyFor(sql, maxRows));

    /// <summary>The read-only statements to hand a provider along with <paramref name="maxRows"/>; null when there is no limit.</summary>
    public static ReadOnlyScript? ReadOnlyFor(string sql, int maxRows) =>
        maxRows is > 0 and < int.MaxValue ? ReadOnlyScriptAnalyzer.Analyze(sql) : null;

    public Task<IReadOnlyList<DbRoutineParameter>> GetRoutineParametersAsync(
        DatabaseSession session, DbObject routine, CancellationToken ct = default) =>
        session.Provider.GetRoutineParametersAsync(routine, ct);

    public Task<QueryExecutionResult> ExecuteRoutineAsync(
        DatabaseSession session, DbObject routine, IReadOnlyList<DbRoutineParameter> parameters,
        IReadOnlyDictionary<string, object?> arguments, int timeoutSeconds, CancellationToken ct = default) =>
        session.Provider.ExecuteRoutineAsync(routine, parameters, arguments, timeoutSeconds, ct);
}
