using DbExplorer.Core.Abstractions;

namespace DbExplorer.Application.Security;

/// <summary>Runs a security script in one transaction, so a failing statement (a name taken, a role still owning
/// objects) leaves nothing half done. Both engines roll back role, user and permission changes with the transaction.</summary>
public static class SecurityScriptRunner
{
    public static async Task RunAsync(IDatabaseProvider provider, string? database, SecurityScript script, int timeoutSeconds, CancellationToken ct = default)
    {
        if (script.StatementCount == 0) return;
        await using var run = await provider.BeginScriptSessionAsync(string.IsNullOrWhiteSpace(database) ? null : database, transactional: true, ct);
        await run.ExecuteAsync(script.Executable, timeoutSeconds, ct);
        await run.CommitAsync(ct);
    }
}
