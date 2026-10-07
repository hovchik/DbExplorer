using System.Collections.Concurrent;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Query;

/// <summary>The outcome of running a script against one database.</summary>
public sealed record DatabaseRunResult(string Database, QueryExecutionResult? Result, string? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>Result sets with the same shape from several databases, stacked with a leading database column.</summary>
public sealed record MergedResultSet(string Title, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows)
{
    /// <summary>The databases returned more rows than the row limit; only the first ones were kept.</summary>
    public bool IsTruncated { get; init; }

    /// <summary>Rows the databases returned together (a lower bound when <see cref="TotalRowCountIsExact"/> is false).</summary>
    public long TotalRowCount { get; init; }

    public bool TotalRowCountIsExact { get; init; } = true;
}

/// <summary>Runs one script against many databases (bounded parallelism, one failure never stops the rest).</summary>
public sealed class MultiDatabaseQueryService(QueryExecutionService queryService)
{
    public const string DatabaseColumn = "Database";

    public async Task<IReadOnlyList<DatabaseRunResult>> RunAsync(
        DatabaseSession session, string sql, IReadOnlyList<string> databases, int timeoutSeconds,
        int maxParallel = 4, IProgress<int>? completed = null, CancellationToken ct = default, int maxRows = int.MaxValue)
    {
        var results = new ConcurrentDictionary<string, DatabaseRunResult>(StringComparer.OrdinalIgnoreCase);
        var done = 0;

        await Parallel.ForEachAsync(databases, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, maxParallel),
            CancellationToken = ct
        }, async (database, token) =>
        {
            try
            {
                var result = await queryService.ExecuteScriptAsync(session, sql, database, timeoutSeconds, token, maxRows);
                results[database] = new DatabaseRunResult(database, result, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                results[database] = new DatabaseRunResult(database, null, ex.Message);
            }
            completed?.Report(Interlocked.Increment(ref done));
        });

        // Keep the caller's database order, not completion order.
        return databases.Where(results.ContainsKey).Select(d => results[d]).ToList();
    }

    /// <summary>
    /// Stacks the i-th result set of every database into one grid when their columns match
    /// (case-insensitively, in order); differently-shaped sets become separate grids. Each grid keeps at most
    /// <paramref name="maxRows"/> rows in all, so running on many databases does not multiply the row limit.
    /// </summary>
    public static IReadOnlyList<MergedResultSet> Merge(IReadOnlyList<DatabaseRunResult> results, int maxRows = int.MaxValue)
    {
        var merged = new List<MergedResultSet>();
        var maxSets = results.Select(r => r.Result?.ResultSets.Count ?? 0).DefaultIfEmpty(0).Max();

        for (var i = 0; i < maxSets; i++)
        {
            var shapes = results
                .Where(r => r.Result is not null && r.Result.ResultSets.Count > i)
                .Select(r => (r.Database, Set: r.Result!.ResultSets[i]))
                .GroupBy(x => string.Join('\u0001', x.Set.Columns.Select(c => c.ToUpperInvariant())))
                .ToList();

            for (var s = 0; s < shapes.Count; s++)
            {
                var group = shapes[s].ToList();
                var columns = group[0].Set.Columns;
                var dbColumn = UniqueName(DatabaseColumn, columns);
                // Plain arrays: UI grids bind cells through the indexer (Values[i]), which needs a public indexed type.
                var rows = group
                    .SelectMany(x => x.Set.Rows.Select(row => (x.Database, Row: row)))
                    .Take(Math.Max(1, maxRows))
                    .Select(x => (IReadOnlyList<object?>)Prepend(x.Database, x.Row))
                    .ToList();
                var total = group.Sum(x => Math.Max(x.Set.TotalRowCount, x.Set.Rows.Count));
                var title = shapes.Count == 1 ? $"Result set {i + 1}" : $"Result set {i + 1} ({(char)('a' + s)})";
                merged.Add(new MergedResultSet(title, [dbColumn, .. columns], rows)
                {
                    IsTruncated = total > rows.Count || group.Any(x => x.Set.IsTruncated),
                    TotalRowCount = total,
                    TotalRowCountIsExact = group.All(x => x.Set.TotalRowCountIsExact)
                });
            }
        }

        return merged;
    }

    private static object?[] Prepend(string database, IReadOnlyList<object?> row)
    {
        var values = new object?[row.Count + 1];
        values[0] = database;
        for (var i = 0; i < row.Count; i++) values[i + 1] = row[i];
        return values;
    }

    private static string UniqueName(string name, IReadOnlyList<string> existing)
    {
        var candidate = name;
        for (var n = 2; existing.Contains(candidate, StringComparer.OrdinalIgnoreCase); n++) candidate = $"{name}_{n}";
        return candidate;
    }
}
