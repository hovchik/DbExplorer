namespace DbExplorer.Core.Abstractions;

/// <summary>
/// One open connection that runs several scripts in sequence, optionally inside a single transaction,
/// so a long operation (e.g. a streamed copy) is still all-or-nothing and keeps session settings such as
/// SQL Server's IDENTITY_INSERT. Disposing without <see cref="CommitAsync"/> rolls the transaction back.
/// </summary>
public interface IScriptSession : IAsyncDisposable
{
    /// <summary>Runs a (possibly multi-statement) script and returns the rows it affected.</summary>
    Task<int> ExecuteAsync(string sql, int timeoutSeconds, CancellationToken ct = default);

    /// <summary>Runs a script and returns its result sets and messages (e.g. a query inside a manual transaction).</summary>
    Task<Models.QueryExecutionResult> QueryAsync(string sql, int timeoutSeconds, int maxRows = int.MaxValue, CancellationToken ct = default);

    Task CommitAsync(CancellationToken ct = default);

    /// <summary>Undoes the transaction's changes; the session is then only good for disposing.</summary>
    Task RollbackAsync(CancellationToken ct = default);
}
