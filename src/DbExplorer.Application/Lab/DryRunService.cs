using DbExplorer.Application.Copy;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.Query;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Abstractions;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Lab;

/// <summary>What one statement of a dry run did: rows affected and, for INSERT / UPDATE / DELETE, the rows themselves.</summary>
public sealed record DryRunStatement(string Sql, DmlKind Kind, string? Table, int RowsAffected, IReadOnlyList<RowChange> Changes, string? Note, string? Error)
{
    public string Summary => Error is not null
        ? $"✗ {Short(Sql)} — {Error}"
        : $"{Kind.ToString().ToUpperInvariant()} {Table ?? ""}: {RowsAffected:N0} row(s)" +
          (Changes.Count > 0 ? $" · {Changes.Count:N0} shown" : "") + (Note is null ? "" : $" · {Note}");

    private static string Short(string sql)
    {
        var line = string.Join(" ", sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length > 80 ? line[..80] + "…" : line;
    }
}

public sealed record DryRunResult(IReadOnlyList<DryRunStatement> Statements, TimeSpan Elapsed)
{
    /// <summary>Changed columns per table, e.g. "dbo.Orders.Status: 37".</summary>
    public IReadOnlyList<string> ColumnSummary => Statements
        .SelectMany(s => s.Changes.Where(c => c.Kind == RowChangeKind.Updated)
            .SelectMany(c => c.Columns.Where(x => x.Changed).Select(x => $"{s.Table}.{x.Column}")))
        .GroupBy(x => x).Select(g => $"{g.Key}: {g.Count():N0}").ToList();
}

/// <summary>
/// Runs a script inside a transaction that is always rolled back, and shows what it would have changed: for each
/// UPDATE / DELETE the affected rows are read before (through a SELECT built from the statement's own FROM / WHERE) and,
/// for UPDATE, again after by primary key; INSERT rows come from RETURNING (PostgreSQL) / OUTPUT (SQL Server). Probes run
/// under savepoints, so one that fails (a type the driver cannot read, OUTPUT on a table with triggers) only loses its
/// detail, not the run. Locks taken by the statements are held until the rollback, with a short lock timeout.
/// </summary>
public sealed class DryRunService
{
    public const int DefaultMaxRows = 500;

    public async Task<DryRunResult> RunAsync(
        DatabaseSession session, MetadataSnapshot snapshot, string? database, string script, int timeoutSeconds,
        int maxRows = DefaultMaxRows, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var started = DateTimeOffset.Now;
        var dialect = SqlDialect.For(session.Provider.ProviderKey);
        var statements = SplitScript(script);
        // A COMMIT in the script would make its changes real; refuse before running anything.
        if (RefuseTransactionControl(statements) is { } refused)
            return new DryRunResult([refused], DateTimeOffset.Now - started);

        var results = new List<DryRunStatement>();
        await using var tx = await session.Provider.BeginScriptSessionAsync(database, transactional: true, ct);
        try
        {
            await tx.ExecuteAsync(dialect.ProviderKey switch
            {
                SqlDialect.SqlServerKey => "SET LOCK_TIMEOUT 5000;",
                SqlDialect.MySqlKey => "SET SESSION innodb_lock_wait_timeout = 5; SET SESSION lock_wait_timeout = 5;",
                _ => "SET LOCAL lock_timeout = '5s';"
            }, timeoutSeconds, ct);

            var probe = new Probe(tx, dialect, timeoutSeconds);
            for (var i = 0; i < statements.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Dry run: statement {i + 1}/{statements.Count}…");
                var result = await RunStatementAsync(probe, snapshot, statements[i], maxRows, ct);
                // SQL Server: a COMMIT / ROLLBACK inside an IF or a block ends the dry run's transaction unseen.
                if (dialect.ProviderKey == SqlDialect.SqlServerKey && !await InTransactionAsync(tx, timeoutSeconds, ct))
                {
                    results.Add(result with { Error = TransactionEndedError });
                    break;
                }
                results.Add(result);
                if (result.Error is not null) break; // the script would have stopped here too
            }
        }
        finally
        {
            try { await tx.RollbackAsync(CancellationToken.None); } catch { /* disposing rolls back as well */ }
        }

        return new DryRunResult(results, DateTimeOffset.Now - started);
    }

    /// <summary>The script's statements as the dry run runs them: trimmed, without GO lines.</summary>
    public static IReadOnlyList<string> SplitScript(string script) =>
        SqlScriptTools.SplitStatements(script, splitOnBlankLines: false)
            .Select(r => SqlAnatomy.Trim(r.Of(script)))
            .Where(s => s.Length > 0 && !s.Equals("GO", StringComparison.OrdinalIgnoreCase))
            .ToList();

    public const string TransactionEndedError =
        "The dry run's transaction ended during this statement (a COMMIT or ROLLBACK inside it?), so what ran up to here may have been committed. The dry run stopped.";

    /// <summary>An error for the first statement that starts, commits or rolls back a transaction; null when there is none.</summary>
    public static DryRunStatement? RefuseTransactionControl(IReadOnlyList<string> statements)
    {
        foreach (var sql in statements)
            if (SqlAnatomy.TransactionControl(sql) is { } keyword)
                return new DryRunStatement(sql, DmlKind.Other, null, 0, [], null,
                    $"A dry run can't include {keyword} (or any BEGIN / COMMIT / ROLLBACK): remove the transaction statements. " +
                    "The dry run already runs everything in a transaction it rolls back. Nothing was run.");
        return null;
    }

    /// <summary>Whether the SQL Server session still has the dry run's transaction open.</summary>
    private static async Task<bool> InTransactionAsync(IScriptSession tx, int timeoutSeconds, CancellationToken ct)
    {
        try
        {
            var result = await tx.QueryAsync("SELECT @@TRANCOUNT;", timeoutSeconds, 1, ct);
            return result.ResultSets.FirstOrDefault()?.Rows is [[var count, ..], ..] && Convert.ToInt32(count) > 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false; // the driver refuses a command on a transaction that has completed
        }
    }

    private static async Task<DryRunStatement> RunStatementAsync(Probe probe, MetadataSnapshot snapshot, string sql, int maxRows, CancellationToken ct)
    {
        var dml = SqlAnatomy.ParseDml(sql);
        var kind = dml?.Kind ?? SqlAnatomy.KindOf(sql);
        var table = dml is null ? null : ResolveTarget(snapshot, dml);
        var keys = table is null ? [] : snapshot.ColumnsOf(table.Database, table.Schema, table.Name)
            .Where(c => c.IsPrimaryKey).OrderBy(c => c.Ordinal).Select(c => c.Name).ToList();
        var tableName = table?.FullName ?? dml?.Target;

        try
        {
            // MySQL commits DDL (and LOCK TABLES and the like) on the spot, so running it would end the dry run's transaction.
            if (probe.Dialect.ProviderKey == SqlDialect.MySqlKey && kind == DmlKind.Other && !MySqlSafeOther(sql))
                return new DryRunStatement(sql, kind, tableName, 0, [], null,
                    "MySQL commits this kind of statement immediately, so it can't be tried and rolled back. The dry run stopped before it.");

            switch (kind)
            {
                case DmlKind.Update or DmlKind.Delete when dml is not null:
                {
                    var beforeSql = BeforeSelect(dml, probe.Dialect, maxRows);
                    var before = beforeSql is null ? null : await probe.TryQueryAsync(beforeSql, maxRows, ct);
                    var affected = await probe.ExecuteAsync(sql, ct);
                    if (before is null || table is null)
                        return new DryRunStatement(sql, kind, tableName, affected, [], before is null ? "rows not previewed" : "target not in the catalog", null);

                    var beforeRows = new TableRows(table, before.Columns, before.Rows, !before.IsTruncated);
                    var limited = before.IsTruncated ? $"first {maxRows:N0} rows previewed" : null;
                    if (kind == DmlKind.Delete)
                    {
                        var deleted = ChangeRecorder.DiffRows(table, beforeRows, new TableRows(table, before.Columns, [], true), keys);
                        return new DryRunStatement(sql, kind, tableName, affected, deleted, limited, null);
                    }

                    if (keys.Count == 0)
                        return new DryRunStatement(sql, kind, tableName, affected,
                            before.Rows.Select(r => new RowChange(table.Schema, table.Name, RowChangeKind.Updated, "",
                                before.Columns.Select((c, i) => new ColumnChange(c, r[i], null, false)).ToList())).ToList(),
                            "no primary key: rows before the update only", null);

                    var columns = snapshot.ColumnsOf(table.Database, table.Schema, table.Name);
                    var keyTypes = keys.Select(k => columns.FirstOrDefault(c => c.Name == k)?.BaseType).ToList();
                    var afterSql = AfterSelect(probe.Dialect, table, keys, before, keyTypes);
                    var after = afterSql is null ? null : await probe.TryQueryAsync(afterSql, maxRows, ct);
                    if (after is null)
                        return new DryRunStatement(sql, kind, tableName, affected, [], "rows after the update could not be read", null);
                    // Rows the WHERE matched but the update left as they were are not changes; a changed key shows as deleted.
                    var changes = ChangeRecorder.DiffRows(table, beforeRows, new TableRows(table, after.Columns, after.Rows, true), keys);
                    return new DryRunStatement(sql, kind, tableName, affected, changes,
                        limited ?? (changes.Any(c => c.Kind == RowChangeKind.Deleted) ? "a changed key shows as deleted" : null), null);
                }

                case DmlKind.Insert when dml is not null && dml.ReturningAt < 0:
                {
                    var returning = probe.Dialect.ProviderKey switch
                    {
                        SqlDialect.SqlServerKey => dml.InsertOutputAt < 0 ? null : sql[..dml.InsertOutputAt] + " OUTPUT inserted.* " + sql[dml.InsertOutputAt..],
                        SqlDialect.MySqlKey => null, // no RETURNING on MySQL
                        _ => sql + " RETURNING *"
                    };
                    if (returning is not null && await probe.TryQueryAsync(returning, maxRows, ct) is { } inserted)
                    {
                        var rows = inserted.Rows.Select(r => new RowChange(table?.Schema ?? "", table?.Name ?? dml.Target, RowChangeKind.Inserted,
                            ChangeRecorder.RowKey(inserted.Columns, r, keys),
                            inserted.Columns.Select((c, i) => new ColumnChange(c, null, r[i], true)).ToList())).ToList();
                        return new DryRunStatement(sql, kind, tableName, (int)Math.Min(int.MaxValue, inserted.TotalRowCount), rows,
                            inserted.IsTruncated ? $"first {maxRows:N0} rows shown" : null, null);
                    }
                    var affected = await probe.ExecuteAsync(sql, ct);
                    return new DryRunStatement(sql, kind, tableName, affected, [], "inserted rows could not be listed", null);
                }

                default:
                {
                    var affected = await probe.ExecuteAsync(sql, ct);
                    return new DryRunStatement(sql, kind, tableName, affected, [],
                        kind is DmlKind.Select or DmlKind.Other ? null : "rows not previewed for this statement shape", null);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DryRunStatement(sql, kind, tableName, 0, [], null, ex.Message);
        }
    }

    /// <summary>Non-DML MySQL statements that neither commit nor write: SET, SHOW, EXPLAIN, REPLACE (a DML) and the like.</summary>
    private static bool MySqlSafeOther(string sql)
    {
        var word = new string(sql.TrimStart().TakeWhile(char.IsLetter).ToArray());
        return word.ToUpperInvariant() is "SET" or "SHOW" or "DESCRIBE" or "DESC" or "EXPLAIN" or "DO" or "USE" or "REPLACE" or "VALUES" or "TABLE";
    }

    /// <summary>The table an UPDATE / DELETE / INSERT writes to, resolving SQL Server's "UPDATE alias … FROM table alias".</summary>
    public static DbObject? ResolveTarget(MetadataSnapshot snapshot, DmlAnatomy dml)
    {
        var name = dml.Target;
        if (dml.From is { } from && SqlAnatomy.ParseSelect("SELECT 1 FROM " + from) is { } q)
        {
            var bare = Unquote(name.Split('.')[^1]);
            if (q.From.FirstOrDefault(f => string.Equals(f.Alias, bare, StringComparison.OrdinalIgnoreCase)) is { Name: { } aliased })
                name = aliased;
        }
        var parts = name.Split('.').Select(Unquote).ToList();
        var table = parts[^1];
        var schema = parts.Count >= 2 ? parts[^2] : null;
        return snapshot.Objects
            .Where(o => o.IsTableLike && string.Equals(o.Name, table, StringComparison.OrdinalIgnoreCase))
            .OrderBy(o => schema is null || string.Equals(o.Schema, schema, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(o => o.Schema is "dbo" or "public" ? 0 : 1)
            .FirstOrDefault(o => schema is null || string.Equals(o.Schema, schema, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A SELECT of the rows an UPDATE / DELETE would touch, from its own target, FROM / USING and WHERE.</summary>
    public static string? BeforeSelect(DmlAnatomy dml, SqlDialect dialect, int maxRows)
    {
        var sqlServer = dialect.ProviderKey == SqlDialect.SqlServerKey;
        string from;
        string columns;
        if (sqlServer)
        {
            // UPDATE/DELETE t … FROM <sources>: the target is one of the sources (by name or alias).
            from = dml.From ?? (dml.Target + (dml.TargetAlias is null ? "" : " " + dml.TargetAlias));
            columns = dml.From is null ? "*" : (dml.TargetAlias ?? dml.Target) + ".*";
        }
        else
        {
            var target = dml.Target + (dml.TargetAlias is null ? "" : " AS " + dml.TargetAlias);
            from = dml.From is null ? target : $"{target}, {dml.From}";
            columns = (dml.TargetAlias ?? dml.Target) + ".*";
        }

        var top = sqlServer
            ? dml.Top.Length > 0 ? dml.Top + " " : $"TOP ({maxRows + 1}) "
            : "";
        var limit = sqlServer ? "" : $" LIMIT {maxRows + 1}";
        return $"{dml.Prefix}SELECT {top}{columns} FROM {from}" + (dml.Where is null ? "" : $" WHERE {dml.Where}") + limit;
    }

    /// <summary>The same rows read back by primary key after the update. <paramref name="keyTypes"/> are the key columns'
    /// base types, so a value is written in a form its column converts (a SQL Server datetime takes 3 fractional digits).</summary>
    public static string? AfterSelect(SqlDialect dialect, DbObject table, IReadOnlyList<string> keys, QueryResultSet before,
        IReadOnlyList<string?>? keyTypes = null)
    {
        var indexes = keys.Select(k => IndexOf(before.Columns, k)).ToList();
        if (indexes.Any(i => i < 0) || before.Rows.Count == 0) return null;
        var predicates = before.Rows.Select(r => "(" + string.Join(" AND ", keys.Select((k, j) =>
            r[indexes[j]] is null ? $"{dialect.Quote(k)} IS NULL"
                : $"{dialect.Quote(k)} = {dialect.Literal(r[indexes[j]], keyTypes is not null && j < keyTypes.Count ? keyTypes[j] : null)}")) + ")");
        return $"SELECT * FROM {dialect.Table(table.Schema, table.Name)} WHERE {string.Join(" OR ", predicates)}";
    }

    private static int IndexOf(IReadOnlyList<string> columns, string name)
    {
        for (var i = 0; i < columns.Count; i++)
            if (string.Equals(columns[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        return s.Length >= 2 && (s[0] == '[' || s[0] == '"') ? s[1..^1] : s;
    }

    /// <summary>Runs statements and savepoint-guarded probes on the dry run's transaction.</summary>
    private sealed class Probe(IScriptSession tx, SqlDialect dialect, int timeoutSeconds)
    {
        private int _savepoints;

        public SqlDialect Dialect { get; } = dialect;

        public async Task<int> ExecuteAsync(string sql, CancellationToken ct)
        {
            var result = await tx.QueryAsync(sql, timeoutSeconds, 0, ct);
            return result.RowsAffected;
        }

        /// <summary>The query's first result set, or null when it failed (the transaction is restored to before it).</summary>
        public async Task<QueryResultSet?> TryQueryAsync(string sql, int maxRows, CancellationToken ct)
        {
            var name = $"dbx_probe_{++_savepoints}";
            var sqlServer = Dialect.ProviderKey == SqlDialect.SqlServerKey;
            await tx.ExecuteAsync(sqlServer ? $"SAVE TRANSACTION {name};" : $"SAVEPOINT {name};", timeoutSeconds, ct);
            try
            {
                var result = await tx.QueryAsync(sql, timeoutSeconds, maxRows, ct);
                if (!sqlServer) await tx.ExecuteAsync($"RELEASE SAVEPOINT {name};", timeoutSeconds, ct);
                return result.ResultSets.FirstOrDefault() ?? new QueryResultSet();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await tx.ExecuteAsync(sqlServer ? $"ROLLBACK TRANSACTION {name};" : $"ROLLBACK TO SAVEPOINT {name};", timeoutSeconds, ct);
                return null;
            }
        }
    }
}
