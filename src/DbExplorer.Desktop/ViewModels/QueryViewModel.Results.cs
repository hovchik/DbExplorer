using DbExplorer.Application.Export;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.Query;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Models;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>Editing results in place and following foreign key values to the rows they reference.</summary>
public partial class QueryViewModel
{
    /// <summary>The catalog the editor suggests from (the current database's); also tells where result columns come from.</summary>
    private MetadataSnapshot? _completionSnapshot;

    /// <summary>Asks the workspace to open SQL in a new tab and run it there: (title, sql, database).</summary>
    public event Action<string, string, string?>? OpenAndRunRequested;

    /// <summary>One source per result set, from the script and the cached catalog; never fails the run.</summary>
    private IReadOnlyList<ResultSource?> ResolveSources(DatabaseSession session, string sql, QueryExecutionResult result)
    {
        try
        {
            return ResultSourceResolver.ResolveScript(sql, result.ResultSets.Select(r => r.Columns).ToList(),
                _completionSnapshot ?? session.Snapshot, session.Provider.ProviderKey, TargetDatabase ?? NullIfEmpty(session.Profile.Database));
        }
        catch
        {
            return result.ResultSets.Select(_ => (ResultSource?)null).ToList();
        }
    }

    /// <param name="session">The connection the rows were read from: edits are saved there or not at all.</param>
    private ResultSetView WithSource(DatabaseSession session, ResultSetView view, ResultSource? source) =>
        source is null ? view : view with
        {
            Source = source,
            CommitEdits = (v, rows) => CommitEditsAsync(session, v, rows),
            OpenReference = OpenReference
        };

    private async Task<string> CommitEditsAsync(DatabaseSession session, ResultSetView view, IReadOnlyList<ResultRow> rows)
    {
        if (!ReferenceEquals(session, _session) || view.Source is not { } source)
            throw new InvalidOperationException("The tab is no longer connected to the connection these rows were read from.");
        if (IsRunning || _executing) throw new InvalidOperationException("Wait for the running statement to finish.");

        IsRunning = true;
        try
        {
            var status = await ResultEditing.CommitAsync(session, source, rows, _transaction, dialogs);
            if (status is null) return "Not committed.";
            Status = status;
            return status;
        }
        finally
        {
            IsRunning = false;
        }
    }

    private void OpenReference(ResultSetView view, ResultReference reference, ResultRow row)
    {
        if (_session is not { } session || view.Source is not { } source) return;
        var sql = ResultEditSql.ReferenceSelect(source, reference, row.Values,
            ResultExporter.DialectFor(session.Provider.ProviderKey), session.Provider.QuoteIdentifier);
        if (sql is null) return;
        OpenAndRunRequested?.Invoke("→ " + reference.ForeignKey.ReferencedTable, sql, source.Database);
    }

    /// <summary>A run replaces the results: uncommitted edits are lost, so ask first.</summary>
    private async Task<bool> ConfirmDiscardEditsAsync()
    {
        if (!ResultEditing.HasEdits(ResultSets)) return true;
        return await dialogs.ConfirmAsync(
            "The results have changes that are not committed yet (edited, new or deleted rows). Run anyway and discard them?", "Discard and run");
    }
}
