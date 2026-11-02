using System.Diagnostics;
using DbExplorer.Application.Query;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Export;

/// <summary>Which columns of the result go into the file, in which order, and which rows: the grid's view without
/// its sort (a streamed result keeps the query's own order).</summary>
/// <param name="ColumnIndexes">Indexes into the result's columns, in file order.</param>
public sealed record ExportProjection(IReadOnlyList<int> ColumnIndexes, string? QuickFilter = null, IReadOnlyCollection<ColumnFilter>? Filters = null)
{
    public bool HasRowFilter => (QuickFilter?.Trim().Length ?? 0) > 0 || Filters?.Any(f => f.IsActive) == true;
}

/// <summary>Progress of a full export: rows read from the database and rows written to the file so far.</summary>
public readonly record struct ExportProgress(long RowsRead, long RowsWritten);

/// <summary>How a full export ended.</summary>
public sealed record FullExportResult(long RowsRead, long RowsWritten, TimeSpan Elapsed, string? Note);

/// <summary>
/// Exports a whole result, not just the fetched rows: the query runs again and its rows go straight from the database
/// reader into the file, so the result is never held in memory. Runs on the calling thread's continuations; call it from
/// a background thread.
/// </summary>
public static class FullResultExport
{
    /// <summary>How often progress is reported, at most.</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(200);

    /// <param name="run">Runs the query again, handing the wanted result set to the stream (e.g.
    /// <see cref="Core.Abstractions.IDatabaseProvider.StreamScriptAsync"/>).</param>
    /// <param name="expectedColumns">The columns of the result on screen. The query must return the same ones again,
    /// or the projection would pick the wrong columns.</param>
    public static async Task<FullExportResult> RunAsync(
        Func<ResultSetStream, CancellationToken, Task> run, int resultSetIndex, IReadOnlyList<string> expectedColumns,
        ResultFileWriter writer, ExportProjection projection, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        var indexes = projection.ColumnIndexes;
        var keep = ResultViewQuery.RowFilter(projection.QuickFilter, projection.Filters ?? []);
        long read = 0;

        void Begin(IReadOnlyList<string> columns)
        {
            if (!SameColumns(columns, expectedColumns))
                throw new InvalidOperationException(
                    "The query returns different columns than the result on screen (it changed since it ran). Run it again, then export.");
            writer.Begin(indexes.Select(i => columns[i]).ToList());
        }

        void Row(object?[] values)
        {
            read++;
            if (keep(values))
            {
                var projected = new object?[indexes.Count];
                for (var i = 0; i < projected.Length; i++) projected[i] = values[indexes[i]];
                writer.WriteRow(projected);
            }
            if (progress is not null && clock.Elapsed - lastReport >= ProgressInterval)
            {
                lastReport = clock.Elapsed;
                progress.Report(new ExportProgress(read, writer.RowCount));
            }
        }

        var stream = new ResultSetStream(resultSetIndex, Begin, Row);
        await run(stream, ct);
        stream.EnsureStarted();
        writer.Complete();
        progress?.Report(new ExportProgress(read, writer.RowCount));
        return new FullExportResult(read, writer.RowCount, clock.Elapsed, writer.Note);
    }

    private static bool SameColumns(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.Count == b.Count && a.Zip(b).All(p => string.Equals(p.First, p.Second, StringComparison.Ordinal));
}
