using DbExplorer.Application.Export;
using DbExplorer.Core.Models;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>Runs the query behind a result again, handing the result set to <paramref name="stream"/> row by row.</summary>
public delegate Task ResultStreamer(ResultSetStream stream, CancellationToken ct);

/// <summary>
/// What a Query tab offers for a result that was cut off at the row limit: the database reader is closed after the limit,
/// so there is nothing to continue from, and the only way to the other rows is to run the (read-only) query again.
/// </summary>
public sealed class ResultRerun
{
    /// <summary>The result's position among the run's result sets (counted like <see cref="QueryExecutionResult.ResultSets"/>).</summary>
    public required int ResultSetIndex { get; init; }

    /// <summary>Rows "Fetch next" adds: the tab's row limit.</summary>
    public required int Step { get; init; }

    /// <summary>
    /// Runs <c>work</c> on a background thread with the tab marked as running (Run is disabled and the tab's Cancel stops
    /// it too); <c>work</c> gets a <see cref="ResultStreamer"/> that runs the query again on the tab's connection, inside
    /// its open transaction when there is one. Throws when the tab is busy or on another connection now.
    /// </summary>
    public required Func<Func<ResultStreamer, CancellationToken, Task<FullExportResult>>, CancellationToken, Task<FullExportResult>> ExportAsync { get; init; }

    /// <summary>Runs the script again keeping this many rows per result set (<see cref="int.MaxValue"/>: all), replacing
    /// the results; the tab's own row limit stays as it is.</summary>
    public required Func<int, Task> FetchAsync { get; init; }
}
