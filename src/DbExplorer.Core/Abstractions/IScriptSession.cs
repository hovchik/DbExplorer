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

    Task CommitAsync(CancellationToken ct = default);
}
