using System.Globalization;
using DbExplorer.Application.Copy;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Models;
using DbExplorer.Core.Search;

namespace DbExplorer.Application.Lab;

public enum ProbeOutcome
{
    Pass,
    Fail,
    Info,
    Error
}

/// <summary>One check of the debugger: what was tested, how it went, the probe query and (for failures) sample values.</summary>
public sealed record ProbeStep(string Stage, ProbeOutcome Outcome, string Message, string? Sql = null, QueryResultSet? Sample = null)
{
    public string Icon => Outcome switch
    {
        ProbeOutcome.Pass => "✓",
        ProbeOutcome.Fail => "✗",
        ProbeOutcome.Error => "!",
        _ => "•"
    };
}

public sealed record WhyNotReport(IReadOnlyList<ProbeStep> Steps, string Verdict);

/// <summary>
/// "Why isn't this row in my result?" Takes a SELECT and a predicate that identifies the expected row (o.OrderId = 1001)
/// and replays the query step by step with COUNT probes: does the row exist, which join drops it (and which part of the
/// ON condition), which WHERE condition excludes it (with the actual values of the columns involved), whether HAVING
/// removes its group, or whether TOP / LIMIT cut it. Every probe is a read-only query with the data-search lock and
/// statement timeouts.
/// </summary>
public sealed class WhyNotDebugger
{
    private readonly DatabaseSession _session;
    private readonly string? _database;
    private readonly DataSearchOptions _options;
    private readonly SqlDialect _dialect;

    public WhyNotDebugger(DatabaseSession session, string? database, DataSearchOptions options)
    {
        _session = session;
        _database = database;
        _options = options;
        _dialect = SqlDialect.For(session.Provider.ProviderKey);
    }

    public async Task<WhyNotReport> AnalyzeAsync(string sql, string expected, IProgress<ProbeStep>? progress = null, CancellationToken ct = default)
    {
        var steps = new List<ProbeStep>();
        void Add(ProbeStep step)
        {
            steps.Add(step);
            progress?.Report(step);
        }

        expected = SqlAnatomy.Trim(expected);
        if (expected.StartsWith("WHERE ", StringComparison.OrdinalIgnoreCase)) expected = expected[6..].Trim();
        var q = SqlAnatomy.ParseSelect(sql);
        if (q is null)
            return Done(steps, "This works on a single SELECT … FROM … statement (no SELECT INTO / several statements).");
        if (q.HasSetOperation)
            return Done(steps, "Queries with UNION / INTERSECT / EXCEPT are not supported: debug each branch on its own.");
        if (string.IsNullOrWhiteSpace(expected))
            return Done(steps, "Describe the expected row with a condition, e.g. o.OrderId = 1001.");
        // The condition is pasted into every probe: one statement only, so a probe can never turn into a script.
        if (Query.SqlLexer.Tokenize(expected).Any(t => t.Kind == Query.SqlTokenKind.Semicolon))
            return Done(steps, "The expected-row condition must be a single condition (no ';').");

        var anchor = FindAnchor(q, expected);
        var anchorItem = q.From[anchor];

        // 1. Does the row exist at all?
        var existsSql = Count(q.Prefix, anchorItem.Source, expected);
        var exists = await CountAsync(existsSql, ct);
        if (exists is null) return Fail(steps, Add, "Row exists", existsSql, "the probe failed");
        if (exists == 0)
        {
            Add(new ProbeStep("Row exists", ProbeOutcome.Fail, $"No row of {anchorItem.Source} matches {expected}.", existsSql));
            return Done(steps, $"The row does not exist in {anchorItem.Source} (check the condition, the database, or uncommitted data).");
        }
        Add(new ProbeStep("Row exists", ProbeOutcome.Pass, $"{exists:N0} row(s) of {anchorItem.Source} match {expected}.", existsSql));

        // 2. Joins, in the order written: the first one after which no joined row matches drops it.
        for (var k = Math.Max(1, anchor); k < q.From.Count; k++)
        {
            var item = q.From[k];
            if (!item.CanEliminate && k != anchor) continue;
            var probe = Count(q.Prefix, q.FromClause(k + 1), expected);
            var n = await CountAsync(probe, ct);
            if (n is null) return Fail(steps, Add, $"Join {item.Source}", probe, "the probe failed");
            if (n > 0)
            {
                Add(new ProbeStep($"{item.Join} {item.Source}", ProbeOutcome.Pass, $"{n:N0} joined row(s) still match.", probe));
                continue;
            }

            Add(new ProbeStep($"{item.Join} {item.Source}", ProbeOutcome.Fail, $"After joining {item.Source} no row matches {expected}.", probe));
            await ExplainJoinAsync(q, k, expected, Add, ct);
            return Done(steps, $"The row is dropped by {item.Join} {item.Source}" + (item.Condition is null ? "." : $" ON {item.Condition}."));
        }
        var from = q.FromClause(q.From.Count);

        // 3. WHERE: each condition alone, then together.
        if (q.Where is { Length: > 0 } where)
        {
            var conjuncts = SqlAnatomy.SplitConjuncts(where);
            var failing = new List<string>();
            foreach (var c in conjuncts)
            {
                var probe = Count(q.Prefix, from, $"({expected}) AND ({c})");
                var n = await CountAsync(probe, ct);
                if (n is null) return Fail(steps, Add, $"WHERE {c}", probe, "the probe failed");
                if (n > 0)
                {
                    Add(new ProbeStep($"WHERE {c}", ProbeOutcome.Pass, $"{n:N0} row(s) pass.", probe));
                    continue;
                }
                failing.Add(c);
                var sample = await SampleAsync(q.Prefix, from, expected, c, ct);
                Add(new ProbeStep($"WHERE {c}", ProbeOutcome.Fail, "Excludes the row." + NullHint(c, sample), probe, sample));
            }

            if (failing.Count > 0)
                return Done(steps, failing.Count == 1
                    ? $"The row is excluded by WHERE {failing[0]}."
                    : $"The row is excluded by {failing.Count} WHERE conditions: {string.Join("; ", failing)}.");

            if (conjuncts.Count > 1)
            {
                var together = Count(q.Prefix, from, $"({expected}) AND ({where})");
                if (await CountAsync(together, ct) == 0)
                {
                    // Find the shortest prefix of conditions that no single joined row satisfies.
                    for (var m = 2; m <= conjuncts.Count; m++)
                    {
                        var part = string.Join(" AND ", conjuncts.Take(m).Select(c => $"({c})"));
                        if (await CountAsync(Count(q.Prefix, from, $"({expected}) AND {part}"), ct) == 0)
                        {
                            Add(new ProbeStep("WHERE (combined)", ProbeOutcome.Fail,
                                $"Each condition passes alone, but no single joined row satisfies {string.Join(" AND ", conjuncts.Take(m))} together " +
                                "(a 1-to-many join gives each condition a different row).", together));
                            return Done(steps, "The WHERE conditions exclude the row together, though none does alone.");
                        }
                    }
                }
            }
        }

        // 4. GROUP BY / HAVING: the row's group, then whether HAVING keeps it.
        if (q.GroupBy is { Length: > 0 } groupBy && q.Having is { Length: > 0 } having)
        {
            var groupExpressions = SqlAnatomy.SplitList(groupBy);
            var filter = WhereWith(expected, q.Where);
            var groupSql = $"{q.Prefix}SELECT {Top(1)}{string.Join(", ", groupExpressions)} FROM {from} WHERE {filter} GROUP BY {groupBy}{Limit(1)}";
            var group = await QueryAsync(groupSql, 1, ct);
            if (group is { Rows.Count: > 0 })
            {
                var row = group.Rows[0];
                var matchGroup = string.Join(" AND ", groupExpressions.Select((g, i) =>
                    row[i] is null ? $"({g}) IS NULL" : $"({g}) = {_dialect.Literal(row[i])}"));
                var probe = $"{q.Prefix}SELECT COUNT(*) FROM (SELECT 1 AS x FROM {from}{(q.Where is null ? "" : $" WHERE {q.Where}")} " +
                            $"GROUP BY {groupBy} HAVING ({having}) AND {matchGroup}) dbx_t";
                var n = await CountAsync(probe, ct);
                if (n == 0)
                {
                    foreach (var c in SqlAnatomy.SplitConjuncts(having))
                    {
                        var p = $"{q.Prefix}SELECT COUNT(*) FROM (SELECT 1 AS x FROM {from}{(q.Where is null ? "" : $" WHERE {q.Where}")} " +
                                $"GROUP BY {groupBy} HAVING ({c}) AND {matchGroup}) dbx_t";
                        if (await CountAsync(p, ct) == 0)
                            Add(new ProbeStep($"HAVING {c}", ProbeOutcome.Fail, $"Removes the row's group ({matchGroup}).", p));
                    }
                    return Done(steps, $"The row's group ({matchGroup}) is removed by HAVING {having}.");
                }
                Add(new ProbeStep("HAVING", ProbeOutcome.Pass, $"The row's group ({matchGroup}) is kept.", probe));
            }
        }

        if (q.GroupBy is { Length: > 0 })
            Add(new ProbeStep("GROUP BY", ProbeOutcome.Info, $"The row is aggregated into its GROUP BY {q.GroupBy} group: look for the group, not the row."));
        if (q.Modifiers.Contains("DISTINCT", StringComparison.OrdinalIgnoreCase))
            Add(new ProbeStep("DISTINCT", ProbeOutcome.Info, "DISTINCT merges it with identical rows."));
        if (q.LimitsRows)
        {
            var position = await PositionAsync(q, from, expected, ct);
            Add(new ProbeStep("TOP / LIMIT", ProbeOutcome.Fail,
                $"The row qualifies, but the query keeps only some rows ({(q.Modifiers.Length > 0 ? q.Modifiers : q.Tail)})" +
                (position is long p ? $"; it is row {p:N0} in ORDER BY {q.OrderBy} order." : ".")));
            return Done(steps, "The row satisfies the query but is cut by TOP / LIMIT / OFFSET.");
        }

        return Done(steps, "The row satisfies every join and condition: it should be in the result. Check the grid's row limit, its filters, " +
                           "or whether the data changed since the query ran.");
    }

    /// <summary>The FROM item the expected-row condition talks about: the first whose alias or name it qualifies with.</summary>
    public static int FindAnchor(SelectAnatomy q, string expected)
    {
        var qualifiers = SqlAnatomy.Qualifiers(expected);
        for (var i = 0; i < q.From.Count; i++)
            if (q.From[i].Reference is { } r && qualifiers.Any(x => string.Equals(x, r, StringComparison.OrdinalIgnoreCase)))
                return i;
        return 0;
    }

    /// <summary>Which part of the ON condition fails, and the values the earlier sources offer for it.</summary>
    private async Task ExplainJoinAsync(SelectAnatomy q, int k, string expected, Action<ProbeStep> add, CancellationToken ct)
    {
        var item = q.From[k];
        var before = q.FromClause(k);
        if (item.Condition is { } on && !on.StartsWith("USING", StringComparison.OrdinalIgnoreCase))
        {
            var conjuncts = SqlAnatomy.SplitConjuncts(on);
            if (conjuncts.Count > 1)
            {
                foreach (var c in conjuncts)
                {
                    var probe = Count(q.Prefix, $"{before} {item.Join} {item.Source} ON {c}", expected);
                    if (await CountAsync(probe, ct) == 0)
                        add(new ProbeStep($"ON {c}", ProbeOutcome.Fail, $"No row of {item.Source} satisfies this part of the join.", probe));
                }
            }

            // Values of the earlier sources' columns used in ON: what the join looks for.
            var reference = item.Reference;
            var columns = SqlAnatomy.QualifiedColumns(on)
                .Where(c => !string.Equals(c.Qualifier, reference, StringComparison.OrdinalIgnoreCase))
                .Select(c => $"{c.Qualifier}.{_dialect.Quote(c.Column)}")
                .Distinct().ToList();
            if (columns.Count > 0)
            {
                var sampleSql = $"{q.Prefix}SELECT {Top(5)}{string.Join(", ", columns)} FROM {before} WHERE {expected}{Limit(5)}";
                var sample = await QueryAsync(sampleSql, 5, ct);
                if (sample is not null)
                    add(new ProbeStep("Join values", ProbeOutcome.Info,
                        $"The join looks for these values in {item.Source}" + (sample.Rows.Any(r => r.Any(v => v is null)) ? " (NULL never matches with =)." : "."),
                        sampleSql, sample));
            }
        }
    }

    /// <summary>The values of the columns a failing condition reads, for the expected row.</summary>
    private async Task<QueryResultSet?> SampleAsync(string prefix, string from, string expected, string condition, CancellationToken ct)
    {
        var columns = SqlAnatomy.QualifiedColumns(condition).Select(c => $"{c.Qualifier}.{_dialect.Quote(c.Column)}").Distinct().ToList();
        if (columns.Count == 0) return null;
        return await QueryAsync($"{prefix}SELECT {Top(5)}{string.Join(", ", columns)} FROM {from} WHERE {expected}{Limit(5)}", 5, ct);
    }

    /// <summary>Where the row sorts among the qualifying rows (1-based), when the query has an ORDER BY over one source's columns.</summary>
    private async Task<long?> PositionAsync(SelectAnatomy q, string from, string expected, CancellationToken ct)
    {
        if (q.OrderBy is not { Length: > 0 } orderBy || q.GroupBy is not null) return null;
        var keys = SqlAnatomy.SplitList(orderBy);
        if (keys.Count != 1) return null;
        var key = keys[0].Trim();
        var descending = key.EndsWith(" DESC", StringComparison.OrdinalIgnoreCase);
        var expression = descending ? key[..^5].Trim() : key.EndsWith(" ASC", StringComparison.OrdinalIgnoreCase) ? key[..^4].Trim() : key;
        var valueSql = $"{q.Prefix}SELECT {Top(1)}{expression} FROM {from} WHERE {WhereWith(expected, q.Where)}{Limit(1)}";
        var value = await QueryAsync(valueSql, 1, ct);
        if (value is not { Rows.Count: > 0 } || value.Rows[0][0] is not { } v) return null;
        var compare = descending ? ">" : "<";
        var n = await CountAsync(Count(q.Prefix, from, WhereWith($"({expression}) {compare} {_dialect.Literal(v)}", q.Where)), ct);
        return n is long before ? before + 1 : null;
    }

    private static string NullHint(string condition, QueryResultSet? sample)
    {
        if (sample is null || !sample.Rows.Any(r => r.Any(v => v is null))) return "";
        var negative = condition.Contains("<>", StringComparison.Ordinal) || condition.Contains("!=", StringComparison.Ordinal) ||
                       condition.Contains("NOT IN", StringComparison.OrdinalIgnoreCase) || condition.Contains('=');
        return negative ? " A column it compares is NULL, and NULL never satisfies =, <>, IN or NOT IN (use IS NULL / COALESCE)." : "";
    }

    private static string WhereWith(string expected, string? where) =>
        where is { Length: > 0 } ? $"({expected}) AND ({where})" : expected;

    private string Count(string prefix, string from, string where) => $"{prefix}SELECT COUNT(*) FROM {from} WHERE {where}";

    private string Top(int n) => _dialect.ProviderKey == SqlDialect.SqlServerKey ? $"TOP ({n}) " : "";

    private string Limit(int n) => _dialect.ProviderKey == SqlDialect.SqlServerKey ? "" : $" LIMIT {n}";

    private async Task<long?> CountAsync(string sql, CancellationToken ct)
    {
        var result = await QueryAsync(sql, 1, ct);
        return result is { Rows.Count: > 0 } && result.Rows[0][0] is { } v ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : null;
    }

    private string? _lastError;

    private async Task<QueryResultSet?> QueryAsync(string sql, int maxRows, CancellationToken ct)
    {
        try
        {
            _lastError = null;
            return await _session.Provider.QueryReadOnlyAsync(sql, _database, _options, maxRows, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _lastError = ex.Message;
            return null;
        }
    }

    private WhyNotReport Fail(List<ProbeStep> steps, Action<ProbeStep> add, string stage, string sql, string what)
    {
        add(new ProbeStep(stage, ProbeOutcome.Error, $"{what}: {_lastError}", sql));
        return Done(steps, $"Could not finish: {what} ({_lastError}). The probe is shown so it can be run in a query tab.");
    }

    private static WhyNotReport Done(List<ProbeStep> steps, string verdict) => new(steps, verdict);
}
