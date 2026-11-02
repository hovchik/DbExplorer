using DbExplorer.Application.Export;
using DbExplorer.Application.Query;
using DbExplorer.Application.Sessions;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>
/// Results past the row limit: exporting a whole result straight from the database to a file, and fetching more rows
/// into the grid. Both run the query again (the reader is closed once the limit is reached), so they are offered only
/// for scripts that read.
/// </summary>
public partial class QueryViewModel
{
    /// <summary>Rows a Fetch more run keeps per result set instead of the tab's limit.</summary>
    private int? _rowLimitOverride;

    /// <summary>True during a Fetch more run: the selected result tab stays where it is.</summary>
    private bool _isFetchMoreRun;

    /// <summary>Above this many rows (or when the size is unknown) Fetch all asks first.</summary>
    private const long FetchAllWithoutAsking = 500_000;

    private string MoreHint(string sql) => QueryAutoRefresh.WhyNotRepeatable(sql) is null
        ? " (Fetch more or Export full result above the grid)"
        : " (raise the row limit to see more)";

    /// <summary>Lets a cut-off result be read again in full, when the script only reads.</summary>
    private ResultSetView WithRerun(DatabaseSession session, string sql, ResultSetView view, int index)
    {
        if (!view.IsTruncated) return view;
        if (QueryAutoRefresh.WhyNotRepeatable(sql) is not null)
            return view with
            {
                RerunUnavailable = "The script may change data, so it is not run again for you. Raise the row limit and run it to see more."
            };

        return view with
        {
            Rerun = new ResultRerun
            {
                ResultSetIndex = index,
                Step = TabRowLimit,
                ExportAsync = (work, ct) => ExportFullResultAsync(session, sql, work, ct),
                FetchAsync = limit => FetchMoreAsync(session, sql, view, limit)
            }
        };
    }

    private async Task<FullExportResult> ExportFullResultAsync(
        DatabaseSession session, string sql, Func<ResultStreamer, CancellationToken, Task<FullExportResult>> work, CancellationToken ct)
    {
        if (!ReferenceEquals(session, _session))
            throw new InvalidOperationException("The tab is on another connection now. Run the query again, then export.");
        if (IsRunning || _executing) throw new InvalidOperationException("Wait for the running statement to finish, then export.");

        // The transaction the result was read in, so the export sees its uncommitted changes too.
        var transaction = _transaction;
        var database = TargetDatabase;
        ResultStreamer streamer = transaction is not null
            ? (stream, token) => transaction.StreamAsync(sql, stream, token)
            : (stream, token) => session.Provider.StreamScriptAsync(sql, database, stream, token);

        _runCts?.Dispose();
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _runCts.Token;
        IsRunning = true;
        Status = transaction is null ? "Exporting the full result…" : "Exporting the full result (in the open transaction)…";
        try
        {
            var result = await Task.Run(() => work(streamer, token), token);
            Status = $"Exported {result.RowsWritten:N0} row(s) in {result.Elapsed.TotalSeconds:N1} s";
            return result;
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            // Drivers report a cancelled read in their own ways (SqlException, …): it is a cancellation all the same.
            Status = "Export cancelled.";
            throw new OperationCanceledException(token);
        }
        catch (Exception ex)
        {
            Status = "Export failed: " + ex.Message;
            throw;
        }
        finally
        {
            IsRunning = false;
        }
    }

    private async Task FetchMoreAsync(DatabaseSession session, string sql, ResultSetView view, int limit)
    {
        if (!ReferenceEquals(session, _session) || _executing || IsRunning) return;
        if (limit == int.MaxValue && (!view.TotalRowCountIsExact || view.TotalRowCount > FetchAllWithoutAsking) &&
            !await dialogs.ConfirmAsync(
                (view.TotalRowCountIsExact ? $"This result has {view.TotalRowCount:N0} rows." : "The size of this result is not known.") +
                " Fetching all of them keeps every row in memory, which can make the app slow or run out of memory." +
                "\n\nTo save every row to a file, use Export full result instead: it writes the rows as they arrive.",
                "Fetch all"))
            return;
        if (!await ConfirmDiscardEditsAsync()) return;
        if (!ReferenceEquals(session, _session) || _executing || IsRunning) return;

        _executing = true;
        _rowLimitOverride = limit;
        _isFetchMoreRun = true;
        try
        {
            var startOffset = _lastRun is { } last && last.Sql == sql ? last.StartOffset : 0;
            await RunAsync(session, sql, startOffset, []);
        }
        finally
        {
            _rowLimitOverride = null;
            _isFetchMoreRun = false;
            _executing = false;
        }
    }
}
