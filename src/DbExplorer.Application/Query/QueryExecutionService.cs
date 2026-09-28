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

    public Task<QueryExecutionResult> ExecuteScriptAsync(
        DatabaseSession session, string sql, string? database, int timeoutSeconds, CancellationToken ct = default,
        int maxRows = int.MaxValue) =>
        session.Provider.ExecuteScriptAsync(sql, database, timeoutSeconds, ct, maxRows);

    public Task<IReadOnlyList<DbRoutineParameter>> GetRoutineParametersAsync(
        DatabaseSession session, DbObject routine, CancellationToken ct = default) =>
        session.Provider.GetRoutineParametersAsync(routine, ct);

    public Task<QueryExecutionResult> ExecuteRoutineAsync(
        DatabaseSession session, DbObject routine, IReadOnlyList<DbRoutineParameter> parameters,
        IReadOnlyDictionary<string, object?> arguments, int timeoutSeconds, CancellationToken ct = default) =>
        session.Provider.ExecuteRoutineAsync(routine, parameters, arguments, timeoutSeconds, ct);
}
