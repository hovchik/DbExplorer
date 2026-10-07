using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DbExplorer.Application.Copy;
using DbExplorer.Application.Query;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Lab;

public enum ImpactSeverity
{
    Info,
    Warning,
    Danger
}

/// <summary>A table a statement writes (or locks) and roughly how many rows, from the estimated plan.</summary>
public sealed record LockTarget(string Table, string Operation, double? EstimatedRows, string LockKind);

public sealed record ImpactFinding(ImpactSeverity Severity, string Table, string Message)
{
    public string Icon => Severity switch { ImpactSeverity.Danger => "⛔", ImpactSeverity.Warning => "⚠", _ => "ℹ" };
}

public sealed record LockImpactReport(IReadOnlyList<LockTarget> Targets, IReadOnlyList<ImpactFinding> Findings)
{
    public ImpactSeverity Worst => Findings.Count == 0 ? ImpactSeverity.Info : Findings.Max(f => f.Severity);
}

/// <summary>
/// Predicts who a script would block before it runs: the estimated plan gives the tables it writes and how many rows
/// (nothing is executed), engine rules turn that into the locks it will take (row locks, SQL Server lock escalation past
/// 5,000 rows, PostgreSQL's ACCESS EXCLUSIVE for most DDL, MySQL metadata locks and InnoDB locking every row a write scans),
/// and the live lock list and running requests show which
/// sessions are on those tables right now and would block it or be blocked by it.
/// </summary>
public sealed class LockImpactAnalyzer
{
    /// <summary>SQL Server escalates a statement's row/page locks on one table to a table lock at about this many.</summary>
    public const int EscalationThreshold = 5000;

    public async Task<LockImpactReport> AnalyzeAsync(DatabaseSession session, string? database, string script, CancellationToken ct = default)
    {
        var key = session.Provider.ProviderKey;
        var statements = SqlScriptTools.SplitStatements(script, splitOnBlankLines: false)
            .Select(r => SqlAnatomy.Trim(r.Of(script))).Where(s => s.Length > 0).ToList();

        var targets = new List<LockTarget>();
        var findings = new List<ImpactFinding>();

        foreach (var statement in statements)
        {
            ct.ThrowIfCancellationRequested();
            var ddl = ClassifyDdl(statement, key);
            if (ddl is not null)
            {
                targets.Add(ddl);
                continue;
            }

            var kind = SqlAnatomy.KindOf(statement);
            if (kind is DmlKind.Select && !LocksOnRead(statement, key)) continue;
            if (kind is DmlKind.Other) continue;

            try
            {
                targets.AddRange(await EstimateAsync(session, database, statement, key, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var dml = SqlAnatomy.ParseDml(statement);
                findings.Add(new ImpactFinding(ImpactSeverity.Info, dml?.Target ?? "", $"No estimated plan ({ex.Message}); rows unknown."));
                if (dml is not null) targets.Add(new LockTarget(dml.Target, kind.ToString().ToUpperInvariant(), null, "row locks"));
            }
        }

        var merged = targets.GroupBy(t => (Normalize(t.Table), t.Operation))
            .Select(g => g.OrderByDescending(t => t.EstimatedRows ?? 0).First()).ToList();
        findings.AddRange(merged.SelectMany(t => Rules(t, key)));

        if (merged.Count > 0) findings.AddRange(await LiveConflictsAsync(session, merged, ct));
        if (findings.Count == 0)
            findings.Add(new ImpactFinding(ImpactSeverity.Info, "", "The script reads only: no locks that block other sessions."));
        return new LockImpactReport(merged, findings.OrderByDescending(f => f.Severity).ToList());
    }

    /// <summary>What the engine does with locks for one target; pure, so the rules are testable.</summary>
    public static IEnumerable<ImpactFinding> Rules(LockTarget t, string providerKey)
    {
        var rows = t.EstimatedRows is double r ? $"≈{r:N0} row(s)" : "an unknown number of rows";
        if (providerKey == SqlDialect.MySqlKey)
        {
            switch (t.LockKind)
            {
                case MySqlExclusiveMetadata:
                    yield return new ImpactFinding(ImpactSeverity.Danger, t.Table,
                        $"{t.Operation} needs an exclusive metadata lock on {t.Table}: it waits for every open transaction that has touched the table (idle ones included), and every new query on the table queues behind it. Set lock_wait_timeout and run it when the table is quiet.");
                    yield break;
                case MySqlOnlineDdl:
                    yield return new ImpactFinding(ImpactSeverity.Warning, t.Table,
                        $"{t.Operation} runs online where InnoDB can (reads and writes continue), but takes an exclusive metadata lock at the start and the end: it waits for open transactions on {t.Table}, and queries arriving meanwhile queue behind it. Changes InnoDB cannot do in place (e.g. a column type) copy the table and block writes.");
                    yield break;
                case MySqlTableWrite:
                    yield return new ImpactFinding(ImpactSeverity.Danger, t.Table,
                        $"{t.Operation} takes a table lock on {t.Table}: other sessions wait until UNLOCK TABLES.");
                    yield break;
                case MySqlFullScanRowLocks:
                    yield return new ImpactFinding(ImpactSeverity.Danger, t.Table,
                        $"{t.Operation} finds its rows without an index: InnoDB locks every row it scans ({rows}), not just the ones it changes, so other writers of {t.Table} wait until commit. An index on the WHERE columns limits the locks.");
                    yield break;
            }
            yield return new ImpactFinding(t.EstimatedRows is >= 100_000 ? ImpactSeverity.Warning : ImpactSeverity.Info, t.Table,
                $"{t.Operation} of {rows}: InnoDB row locks (with gap locks under REPEATABLE READ) on the rows it reaches through the index; other writers of those rows wait until commit, readers never do." +
                (t.EstimatedRows is >= 100_000 ? " A large transaction also grows the undo log and replication lag; consider batches (LIMIT in a loop)." : ""));
            yield break;
        }

        if (providerKey == SqlDialect.SqlServerKey)
        {
            switch (t.LockKind)
            {
                case "Sch-M":
                    yield return new ImpactFinding(ImpactSeverity.Danger, t.Table,
                        $"{t.Operation} takes a schema-modification (Sch-M) lock: every query on {t.Table}, NOLOCK readers included, waits until the transaction ends — and it waits itself for every running query on the table first.");
                    yield break;
                case "S table":
                    yield return new ImpactFinding(ImpactSeverity.Warning, t.Table,
                        $"{t.Operation} without ONLINE = ON holds a shared table lock: writers of {t.Table} wait until it finishes.");
                    yield break;
            }
            if (t.EstimatedRows is double n && n >= EscalationThreshold)
                yield return new ImpactFinding(ImpactSeverity.Danger, t.Table,
                    $"{t.Operation} of {rows}: past {EscalationThreshold:N0} locks SQL Server escalates to an exclusive TABLE lock — readers (except READ UNCOMMITTED / snapshot) and writers of {t.Table} block until commit. Batch it (e.g. TOP (4000) in a loop).");
            else
                yield return new ImpactFinding(ImpactSeverity.Info, t.Table,
                    $"{t.Operation} of {rows}: exclusive key/row locks (intent locks on pages and the table); others block only on those rows, or when they scan them under READ COMMITTED.");
            yield break;
        }

        switch (t.LockKind)
        {
            case "ACCESS EXCLUSIVE":
                yield return new ImpactFinding(ImpactSeverity.Danger, t.Table,
                    $"{t.Operation} takes ACCESS EXCLUSIVE on {t.Table}: even SELECTs wait. It first queues behind every running query on the table, and every new query then queues behind it — set lock_timeout and run it when the table is quiet.");
                yield break;
            case "SHARE":
                yield return new ImpactFinding(ImpactSeverity.Warning, t.Table,
                    $"{t.Operation} takes a SHARE lock: INSERT/UPDATE/DELETE on {t.Table} wait until it finishes (use CREATE INDEX CONCURRENTLY).");
                yield break;
            case "SHARE UPDATE EXCLUSIVE":
                yield return new ImpactFinding(ImpactSeverity.Info, t.Table,
                    $"{t.Operation} takes SHARE UPDATE EXCLUSIVE: reads and writes continue; other DDL and VACUUM on {t.Table} wait.");
                yield break;
        }
        yield return new ImpactFinding(t.EstimatedRows is >= 100_000 ? ImpactSeverity.Warning : ImpactSeverity.Info, t.Table,
            $"{t.Operation} of {rows}: ROW EXCLUSIVE on the table (only DDL waits) and a row lock per row — other writers of those rows wait until commit; readers never do." +
            (t.EstimatedRows is >= 100_000 ? " A large transaction also delays VACUUM and grows WAL; consider batches." : ""));
    }

    /// <summary>DDL and other statements whose lock does not depend on rows; null for DML / queries.</summary>
    public static LockTarget? ClassifyDdl(string statement, string providerKey)
    {
        var tokens = SqlLexer.Tokenize(statement).Where(t => !t.IsTrivia).ToList();
        if (tokens.Count < 2) return null;
        string Word(int i) => i < tokens.Count && tokens[i].Kind is SqlTokenKind.Word or SqlTokenKind.QuotedIdentifier ? tokens[i].Text.ToUpperInvariant() : "";

        string NameAfter(int i)
        {
            var parts = new List<string>();
            while (i < tokens.Count && (tokens[i].IsIdentifier || tokens[i].Kind == SqlTokenKind.Dot))
            {
                if (tokens[i].IsIdentifier)
                {
                    if (parts.Count > 0 && i > 0 && tokens[i - 1].IsIdentifier) break;
                    if (Word(i) is "IF" or "EXISTS" or "ONLY" or "CONCURRENTLY" or "TABLE") { i++; continue; }
                    parts.Add(tokens[i].Identifier);
                }
                i++;
            }
            return string.Join(".", parts);
        }

        var sqlServer = providerKey == SqlDialect.SqlServerKey;
        var mySql = providerKey == SqlDialect.MySqlKey;
        var exclusive = sqlServer ? "Sch-M" : mySql ? MySqlExclusiveMetadata : "ACCESS EXCLUSIVE";
        var w0 = Word(0);
        var w1 = Word(1);
        if (mySql)
        {
            switch (w0)
            {
                case "ALTER" when w1 is "TABLE":
                    return new LockTarget(NameAfter(2), "ALTER TABLE", null, MySqlOnlineDdl);
                case "CREATE" or "DROP" when IsIndexStatement(tokens):
                {
                    var on = tokens.FindIndex(t => t.Is("ON"));
                    return new LockTarget(on >= 0 ? NameAfter(on + 1) : "", w0 + " INDEX", null, MySqlOnlineDdl);
                }
                case "LOCK" when w1 is "TABLES" or "TABLE":
                    return new LockTarget(NameAfter(2), "LOCK TABLES", null, MySqlTableWrite);
                case "OPTIMIZE" or "RENAME" when w1 is "TABLE":
                    return new LockTarget(NameAfter(2), w0 + " TABLE", null, MySqlExclusiveMetadata);
            }
        }
        switch (w0)
        {
            case "ALTER" when w1 is "TABLE":
                return new LockTarget(NameAfter(2), "ALTER TABLE", null, exclusive);
            case "DROP" when w1 is "TABLE":
                return new LockTarget(NameAfter(2), "DROP TABLE", null, exclusive);
            case "TRUNCATE":
                return new LockTarget(NameAfter(1), "TRUNCATE", null, exclusive);
            case "LOCK" when w1 is "TABLE" || !sqlServer:
                return new LockTarget(NameAfter(w1 is "TABLE" ? 2 : 1), "LOCK TABLE", null, exclusive);
            case "VACUUM" when !mySql && statement.Contains("FULL", StringComparison.OrdinalIgnoreCase):
                return new LockTarget(NameAfter(2), "VACUUM FULL", null, "ACCESS EXCLUSIVE");
            case "CLUSTER" or "REINDEX" when !sqlServer && !mySql && !statement.Contains("CONCURRENTLY", StringComparison.OrdinalIgnoreCase):
                return new LockTarget(NameAfter(w0 == "REINDEX" ? 2 : 1), w0, null, w0 == "CLUSTER" ? "ACCESS EXCLUSIVE" : "SHARE");
            case "CREATE" or "DROP" when IsIndexStatement(tokens):
            {
                var on = tokens.FindIndex(t => t.Is("ON"));
                var table = on >= 0 ? NameAfter(on + 1) : "";
                var operation = w0 + " INDEX";
                if (sqlServer)
                    return Regex.IsMatch(statement, @"ONLINE\s*=\s*ON", RegexOptions.IgnoreCase)
                        ? new LockTarget(table, operation + " (ONLINE)", null, "row locks")
                        : new LockTarget(table, operation, null, w0 == "DROP" ? "Sch-M" : "S table");
                if (statement.Contains("CONCURRENTLY", StringComparison.OrdinalIgnoreCase))
                    return new LockTarget(table, operation + " CONCURRENTLY", null, "SHARE UPDATE EXCLUSIVE");
                return new LockTarget(table, operation, null, w0 == "DROP" ? "ACCESS EXCLUSIVE" : "SHARE");
            }
        }
        return null;
    }

    private const string MySqlExclusiveMetadata = "METADATA EXCLUSIVE";
    private const string MySqlOnlineDdl = "ONLINE DDL";
    private const string MySqlTableWrite = "TABLE LOCK";
    private const string MySqlFullScanRowLocks = "row locks (full scan)";

    private static bool IsIndexStatement(IReadOnlyList<SqlToken> tokens) =>
        tokens.Take(5).Any(t => t.Is("INDEX"));

    /// <summary>SELECT … FOR UPDATE / FOR SHARE (PostgreSQL) or with UPDLOCK / XLOCK / TABLOCKX / HOLDLOCK hints (SQL Server).</summary>
    public static bool LocksOnRead(string statement, string providerKey) =>
        providerKey == SqlDialect.SqlServerKey
            ? Regex.IsMatch(statement, @"\b(UPDLOCK|XLOCK|TABLOCKX|TABLOCK|HOLDLOCK|SERIALIZABLE)\b", RegexOptions.IgnoreCase)
            : Regex.IsMatch(statement, @"\bFOR\s+(NO\s+KEY\s+)?(UPDATE|SHARE|KEY\s+SHARE)\b|\bLOCK\s+IN\s+SHARE\s+MODE\b", RegexOptions.IgnoreCase);

    private static async Task<IReadOnlyList<LockTarget>> EstimateAsync(
        DatabaseSession session, string? database, string statement, string key, CancellationToken ct)
    {
        if (key == SqlDialect.SqlServerKey)
        {
            var result = await session.Provider.ExecuteScriptAsync(QueryPlanTools.BuildExplainScript(statement, key, analyze: false), database, 60, ct);
            var plan = result.ResultSets.FirstOrDefault(rs => rs.Columns.Contains("PhysicalOp", StringComparer.OrdinalIgnoreCase));
            return plan is null ? [] : ParseShowPlan(plan.Columns, plan.Rows);
        }

        if (key == SqlDialect.MySqlKey)
        {
            var result = await session.Provider.ExecuteScriptAsync($"EXPLAIN {statement}", database, 60, ct);
            var table = result.ResultSets.FirstOrDefault(rs => rs.Columns.Contains("select_type", StringComparer.OrdinalIgnoreCase));
            return table is null ? [] : ParseMySqlExplain(table.Columns, table.Rows, statement);
        }

        var json = await session.Provider.ExecuteScriptAsync($"EXPLAIN (FORMAT JSON) {statement}", database, 60, ct);
        var text = json.ResultSets.FirstOrDefault()?.Rows.FirstOrDefault()?.FirstOrDefault()?.ToString();
        return text is null ? [] : ParsePostgresPlan(text);
    }

    /// <summary>
    /// MySQL's classic EXPLAIN table for one statement: the written table's estimated rows, and whether it is reached
    /// without an index (type ALL / index), in which case InnoDB locks every row it scans. A locking SELECT locks each
    /// table it reads.
    /// </summary>
    public static IReadOnlyList<LockTarget> ParseMySqlExplain(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<object?>> rows, string statement)
    {
        int Col(string name) => columns.ToList().FindIndex(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
        var table = Col("table");
        var type = Col("type");
        var estimate = Col("rows");
        if (rows.Count == 0) return [];
        double? Rows(IReadOnlyList<object?> row) => estimate >= 0 && row[estimate] is { } v and not DBNull ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : null;
        bool Scans(IReadOnlyList<object?> row) => type >= 0 && row[type]?.ToString() is "ALL" or "index";

        var kind = SqlAnatomy.KindOf(statement);
        if (kind == DmlKind.Select)
        {
            return rows.Where(r => table >= 0 && r[table] is string name && !name.StartsWith('<'))
                .Select(r => new LockTarget(r[table]!.ToString()!, "SELECT … FOR UPDATE/SHARE", Rows(r), Scans(r) ? MySqlFullScanRowLocks : "row locks"))
                .ToList();
        }

        var dml = SqlAnatomy.ParseDml(statement);
        if (dml is null) return [];
        var first = rows[0];
        var operation = kind.ToString().ToUpperInvariant();
        var scans = kind is DmlKind.Update or DmlKind.Delete && Scans(first);
        return [new LockTarget(dml.Target, operation, kind == DmlKind.Insert ? null : Rows(first), scans ? MySqlFullScanRowLocks : "row locks")];
    }

    private static readonly Regex ObjectArgument = new(@"OBJECT:\((?:\[[^\]]*\]\.)?(?<schema>\[[^\]]*\])\.(?<table>\[[^\]]*\])", RegexOptions.CultureInvariant);

    /// <summary>Write operators of a SHOWPLAN_ALL result with their estimated rows, one per table (the largest).</summary>
    public static IReadOnlyList<LockTarget> ParseShowPlan(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        int Col(string name) => columns.ToList().FindIndex(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
        var op = Col("PhysicalOp");
        var argument = Col("Argument");
        var estimate = Col("EstimateRows");
        if (op < 0 || argument < 0) return [];

        var result = new List<LockTarget>();
        foreach (var row in rows)
        {
            var physical = row[op]?.ToString() ?? "";
            var operation = physical switch
            {
                _ when physical.EndsWith(" Update", StringComparison.Ordinal) => "UPDATE",
                _ when physical.EndsWith(" Delete", StringComparison.Ordinal) => "DELETE",
                _ when physical.EndsWith(" Insert", StringComparison.Ordinal) => "INSERT",
                _ when physical.EndsWith(" Merge", StringComparison.Ordinal) => "MERGE",
                _ => null
            };
            if (operation is null || !physical.StartsWith("Clustered", StringComparison.Ordinal) && !physical.StartsWith("Table", StringComparison.Ordinal))
                continue;
            var m = ObjectArgument.Match(row[argument]?.ToString() ?? "");
            if (!m.Success) continue;
            var table = $"{m.Groups["schema"].Value.Trim('[', ']')}.{m.Groups["table"].Value.Trim('[', ']')}";
            double? rowsEstimate = estimate >= 0 && row[estimate] is { } e ? Convert.ToDouble(e, CultureInfo.InvariantCulture) : null;
            result.Add(new LockTarget(table, operation, rowsEstimate, "row locks"));
        }
        return result;
    }

    /// <summary>ModifyTable nodes (and, for FOR UPDATE / SHARE, LockRows over scans) of an EXPLAIN (FORMAT JSON) plan.</summary>
    public static IReadOnlyList<LockTarget> ParsePostgresPlan(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new List<LockTarget>();

        void Walk(JsonElement plan, bool underLockRows)
        {
            var nodeType = plan.TryGetProperty("Node Type", out var n) ? n.GetString() : null;
            double? Rows(JsonElement p) => p.TryGetProperty("Plan Rows", out var r) ? r.GetDouble() : null;
            string? Relation(JsonElement p) =>
                p.TryGetProperty("Relation Name", out var rel)
                    ? (p.TryGetProperty("Schema", out var s) ? s.GetString() + "." : "") + rel.GetString()
                    : null;

            if (nodeType == "ModifyTable" && Relation(plan) is { } target)
            {
                var operation = plan.TryGetProperty("Operation", out var o) ? o.GetString()?.ToUpperInvariant() ?? "MODIFY" : "MODIFY";
                // The rows written are the rows the sub-plan produces.
                var rows = plan.TryGetProperty("Plans", out var sub) && sub.GetArrayLength() > 0 ? Rows(sub[0]) : Rows(plan);
                result.Add(new LockTarget(target, operation, rows, "row locks"));
            }
            else if (underLockRows && Relation(plan) is { } scanned)
            {
                result.Add(new LockTarget(scanned, "SELECT … FOR UPDATE/SHARE", Rows(plan), "row locks"));
            }

            if (plan.TryGetProperty("Plans", out var children))
                foreach (var child in children.EnumerateArray())
                    Walk(child, underLockRows || nodeType == "LockRows");
        }

        foreach (var entry in doc.RootElement.EnumerateArray())
            if (entry.TryGetProperty("Plan", out var plan)) Walk(plan, false);
        return result;
    }

    /// <summary>Sessions holding locks on, waiting for, or running statements against the target tables right now.</summary>
    private static async Task<IReadOnlyList<ImpactFinding>> LiveConflictsAsync(DatabaseSession session, IReadOnlyList<LockTarget> targets, CancellationToken ct)
    {
        var findings = new List<ImpactFinding>();
        IReadOnlyList<DbLock> locks;
        IReadOnlyList<DbActiveRequest> active;
        try
        {
            var locksTask = session.Provider.GetLocksAsync(ct);
            var activeTask = session.Provider.GetActiveRequestsAsync(ct);
            await Task.WhenAll(locksTask, activeTask);
            locks = locksTask.Result;
            active = activeTask.Result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            findings.Add(new ImpactFinding(ImpactSeverity.Info, "", "Live sessions not checked: " + ex.Message));
            return findings;
        }

        // One report per table, however many statements of the script touch it; the strongest lock decides.
        foreach (var group in targets.GroupBy(t => Normalize(t.Table), StringComparer.OrdinalIgnoreCase))
        {
            var target = group.First();
            var exclusive = group.Any(t => t.LockKind is "Sch-M" or "ACCESS EXCLUSIVE" or "S table" or "SHARE" ||
                                           t.LockKind is MySqlExclusiveMetadata or MySqlTableWrite ||
                                           t.EstimatedRows is >= EscalationThreshold && session.Provider.ProviderKey == SqlDialect.SqlServerKey);

            var holders = locks.Where(l => SameTable(l.ObjectName, target.Table))
                .GroupBy(l => l.SessionId)
                .Select(g => (Session: g.Key, Modes: string.Join("/", g.Select(l => l.LockMode).Distinct()), Waiting: g.Any(l => l.IsWaiting)))
                .ToList();
            foreach (var h in holders)
            {
                var request = active.FirstOrDefault(a => a.SessionId == h.Session);
                var what = request is null ? "" : $" ({request.Status}{(request.ElapsedMs is long ms ? $", {ms / 1000.0:N0} s" : "")}: {Shorten(request.SqlText)})";
                findings.Add(new ImpactFinding(exclusive ? ImpactSeverity.Danger : ImpactSeverity.Warning, target.Table,
                    $"Session {h.Session} {(h.Waiting ? "is waiting for" : "holds")} {h.Modes} lock(s) on it now{what} — " +
                    (exclusive ? "your statement would wait for it, and block everyone queued after." : "you block each other if you touch the same rows.")));
            }

            var bare = target.Table.Split('.')[^1];
            foreach (var request in active.Where(a => holders.All(h => h.Session != a.SessionId) &&
                                                      a.SqlText is { } sql && Regex.IsMatch(sql, $@"\b{Regex.Escape(bare)}\b", RegexOptions.IgnoreCase)))
            {
                findings.Add(new ImpactFinding(exclusive ? ImpactSeverity.Warning : ImpactSeverity.Info, target.Table,
                    $"Session {request.SessionId} is running a statement that mentions {bare}" +
                    (request.ElapsedMs is long ms ? $" for {ms / 1000.0:N0} s" : "") + $": {Shorten(request.SqlText)}"));
            }
        }
        return findings;
    }

    /// <summary>"dbo.Orders" matches "Orders", "[dbo].[Orders]" and PostgreSQL's "public.orders" / "orders" regclass text.</summary>
    public static bool SameTable(string? lockObject, string target)
    {
        if (string.IsNullOrEmpty(lockObject)) return false;
        var a = Normalize(lockObject).Split('.');
        var b = Normalize(target).Split('.');
        if (!string.Equals(a[^1], b[^1], StringComparison.OrdinalIgnoreCase)) return false;
        return a.Length < 2 || b.Length < 2 || string.Equals(a[^2], b[^2], StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string name) => name.Replace("[", "").Replace("]", "").Replace("\"", "").Replace("`", "").Trim();

    private static string Shorten(string? sql)
    {
        var line = string.Join(" ", (sql ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length > 120 ? line[..120] + "…" : line;
    }
}
