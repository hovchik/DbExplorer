using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Models;
using DbExplorer.Core.Search;

namespace DbExplorer.Application.Search;

/// <summary>
/// Searches values across many tables. Tables are processed smallest-first with bounded
/// parallelism; each table is one read-only, lock-timeout-guarded query. Tables that time out
/// or hit a lock are reported as skipped instead of waiting.
/// </summary>
public sealed class DataSearchService
{
    public async IAsyncEnumerable<DataSearchEvent> SearchAsync(
        DatabaseSession session,
        DataSearchRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var term = SearchTerm.Create(request.Term, request.Mode, request.IncludeNumericColumns, request.IncludeGuidColumns);
        var (targets, tooLarge) = BuildPlan(session, request, term);
        var total = targets.Count + tooLarge.Count;
        var done = new Counter();

        foreach (var t in tooLarge)
        {
            yield return new TableSkipped(t.Schema, t.Name,
                $"Estimated {t.RowCount:N0} rows exceeds the limit", ++done.Value, total);
        }

        var options = new DataSearchOptions(request.MaxMatchesPerTable, request.QueryTimeoutSeconds, request.LockTimeoutMs);
        var channel = Channel.CreateUnbounded<DataSearchEvent>(new UnboundedChannelOptions { SingleReader = true });

        var producer = Task.Run(async () =>
        {
            try
            {
                await Parallel.ForEachAsync(
                    targets,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Math.Max(1, request.MaxDegreeOfParallelism),
                        CancellationToken = ct
                    },
                    async (target, token) =>
                    {
                        try
                        {
                            var matches = await session.Provider.SearchTableAsync(target, term, options, token);
                            foreach (var m in matches) channel.Writer.TryWrite(new DataMatchFound(m));
                            channel.Writer.TryWrite(new TableSearched(
                                target.Schema, target.Name, matches.Count, Interlocked.Increment(ref done.Value), total));
                        }
                        catch (Exception ex) when (!token.IsCancellationRequested)
                        {
                            // Lock timeout, statement timeout, permission denied...: report and move on.
                            channel.Writer.TryWrite(new TableSkipped(
                                target.Schema, target.Name, ex.Message, Interlocked.Increment(ref done.Value), total));
                        }
                    });
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }, CancellationToken.None);

        await foreach (var e in channel.Reader.ReadAllAsync(ct))
            yield return e;

        await producer;
    }

    private static (List<DbTableTarget> Targets, List<DbObject> TooLarge) BuildPlan(
        DatabaseSession session, DataSearchRequest request, SearchTerm term)
    {
        var snapshot = session.Snapshot;
        var provider = session.Provider;
        var schemas = request.Schemas.Count == 0
            ? null
            : new HashSet<string>(request.Schemas, StringComparer.OrdinalIgnoreCase);

        var targets = new List<(DbTableTarget Target, long Rows)>();
        var tooLarge = new List<DbObject>();

        foreach (var obj in snapshot.Objects)
        {
            var include = obj.Type == DbObjectType.Table
                || (request.IncludeViews && obj.Type is DbObjectType.View or DbObjectType.MaterializedView);
            if (!include) continue;
            if (schemas is not null && !schemas.Contains(obj.Schema)) continue;
            if (!string.IsNullOrWhiteSpace(request.TableNameFilter)
                && !obj.Name.Contains(request.TableNameFilter.Trim(), StringComparison.OrdinalIgnoreCase)) continue;

            var columns = snapshot.ColumnsOf(obj.Schema, obj.Name).ToList();
            if (!columns.Any(c => provider.IsSearchable(c, term))) continue;

            if (request.MaxTableRows is long max && obj.RowCount > max)
            {
                tooLarge.Add(obj);
                continue;
            }

            targets.Add((new DbTableTarget(obj.Schema, obj.Name, columns), obj.RowCount ?? long.MaxValue));
        }

        // Small tables first: most results show up within seconds.
        return (targets.OrderBy(t => t.Rows).Select(t => t.Target).ToList(), tooLarge);
    }

    private sealed class Counter
    {
        public int Value;
    }
}
