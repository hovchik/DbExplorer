using DbExplorer.Application.Copy;
using DbExplorer.Application.Design;
using DbExplorer.Application.Metadata;
using DbExplorer.Core.Abstractions;
using DbExplorer.Core.Models;
using DbExplorer.Core.Search;

namespace DbExplorer.Application.TestData;

/// <summary>
/// The server side of the test data generator: plans a table from the cached catalog, reads what only the data can
/// tell (parent keys to point at, the largest key so far, enum labels, keys already taken), and inserts generated
/// rows in one transaction. Reads use the same non-blocking settings as data search; nothing is written before
/// <see cref="InsertAsync"/>.
/// </summary>
public static class TestDataService
{
    /// <summary>Parent rows sampled per foreign key.</summary>
    public const int ParentSample = 1000;

    /// <summary>Existing unique key values read so generated rows don't repeat them.</summary>
    public const int ExistingKeySample = 50_000;

    private static readonly DataSearchOptions ReadLimits = new(MaxMatchesPerTable: 0, QueryTimeoutSeconds: 30, LockTimeoutMs: 3000);

    public static async Task<TestDataPlan> LoadAsync(
        IDatabaseProvider provider, MetadataSnapshot snapshot, DbObject table, CancellationToken ct = default)
    {
        var columns = snapshot.ColumnsOf(table.Database, table.Schema, table.Name).ToList();
        if (columns.Count == 0)
            throw new InvalidOperationException($"No columns of {table.FullName} are in the metadata cache. Refresh metadata and try again.");
        var constraints = await provider.GetTableConstraintsAsync(table, ct);
        var plan = TestDataPlanner.Plan(provider.ProviderKey, table, columns,
            snapshot.ForeignKeysOf(table.Database, table.Schema, table.Name),
            snapshot.IndexesOf(table.Database, table.Schema, table.Name), constraints);
        return await SampleAsync(provider, plan, ct);
    }

    /// <summary>Fills in the data-dependent parts of a plan. A read that fails (no permission on the parent, a
    /// timeout) becomes a warning instead of stopping the generator.</summary>
    public static async Task<TestDataPlan> SampleAsync(IDatabaseProvider provider, TestDataPlan plan, CancellationToken ct = default)
    {
        var dialect = SqlDialect.For(plan.ProviderKey);
        var table = plan.Table;
        var database = string.IsNullOrEmpty(table.Database) ? null : table.Database;
        var target = dialect.Table(table.Schema, table.Name);
        var warnings = plan.Warnings.ToList();

        var references = new List<TestReference>();
        foreach (var reference in plan.References)
        {
            var parent = dialect.Table(reference.Key.ReferencedSchema, reference.Key.ReferencedTable);
            var cols = reference.ParentColumns.Select(dialect.Quote).ToList();
            var sql = dialect.SelectTop(string.Join(", ", cols), parent,
                string.Join(" AND ", cols.Select(c => c + " IS NOT NULL")), null, ParentSample);
            try
            {
                var result = await provider.QueryReadOnlyAsync(sql, database, ReadLimits, ParentSample, ct);
                var rows = result.Rows.Select(r => r.ToArray()).ToList();
                references.Add(reference with { Rows = rows });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add($"Could not read {reference.ParentName} for {string.Join(", ", reference.Columns)}: {ex.Message}");
                references.Add(reference);
            }
        }

        var columns = new List<TestColumnPlan>();
        foreach (var column in plan.Columns)
        {
            var updated = column;
            if (column.Family == TypeFamily.Integer && !column.Column.IsIdentity && !column.Column.IsComputed
                && (column.Unique || column.Kind == TestValueKind.Sequence))
            {
                try
                {
                    var result = await provider.QueryReadOnlyAsync(
                        $"SELECT MAX({dialect.Quote(column.Name)}) FROM {target}", database, ReadLimits, 1, ct);
                    var max = result.Rows.FirstOrDefault()?.FirstOrDefault();
                    if (max is not null and not DBNull) updated = updated with { SequenceStart = Convert.ToInt64(max) + 1 };
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    warnings.Add($"Could not read the largest {column.Name}: {ex.Message}");
                }
            }
            if (plan.ProviderKey == SqlDialect.PostgresKey && column.Family == TypeFamily.Other && column.Choices.Count == 0
                && !column.Column.IsComputed && column.ReferenceIndex is null)
            {
                var labels = await PostgresEnumLabelsAsync(provider, column.Column, database, ct);
                if (labels.Count > 0)
                    updated = updated with
                    {
                        Choices = labels,
                        Kind = TestValueKind.OneOf,
                        Note = "enum: one of " + string.Join(", ", labels.Take(4)) + (labels.Count > 4 ? ", …" : ""),
                        Options = new[] { TestValueKind.OneOf }.Concat(column.Options.Where(o => o != TestValueKind.OneOf)).ToList()
                    };
            }
            columns.Add(updated);
        }

        // Keys already in the table, so generated rows don't clash with them: every unique key over several columns,
        // and single unique columns whose values come from a short list (parents, enum labels, booleans).
        var keys = plan.UniqueKeys.ToList();
        foreach (var column in columns.Where(c => c.Unique && c.Kind is TestValueKind.Reference or TestValueKind.OneOf or TestValueKind.Boolean))
            if (!keys.Any(k => k.Columns.Count == 1 && string.Equals(k.Columns[0], column.Name, StringComparison.OrdinalIgnoreCase)))
                keys.Add(new TestUniqueKey(column.Name, [column.Name]));
        var sampled = new List<TestUniqueKey>();
        foreach (var key in keys)
        {
            var cols = key.Columns.Select(dialect.Quote).ToList();
            try
            {
                var result = await provider.QueryReadOnlyAsync(
                    dialect.SelectTop(string.Join(", ", cols), target, null, null, ExistingKeySample), database, ReadLimits, ExistingKeySample, ct);
                var existing = result.Rows
                    .Where(r => r.All(v => v is not null and not DBNull))
                    .Select(r => string.Concat(r.Select(v => TestDataGenerator.KeyText(v) + '\u001f')))
                    .ToHashSet(StringComparer.Ordinal);
                sampled.Add(key with { Existing = existing });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add($"Could not read the existing values of {key.Name}: {ex.Message}");
                sampled.Add(key);
            }
        }

        return plan with { Columns = columns, References = references, UniqueKeys = sampled, Warnings = warnings };
    }

    private static async Task<IReadOnlyList<string>> PostgresEnumLabelsAsync(
        IDatabaseProvider provider, DbColumn column, string? database, CancellationToken ct)
    {
        // DataType is format_type(): already quoted and schema-qualified when needed.
        var type = column.DataType.Trim();
        if (type.Length == 0 || type.EndsWith("[]", StringComparison.Ordinal) || type.Contains(';') || type.Contains("--"))
            return [];
        try
        {
            var result = await provider.QueryReadOnlyAsync(
                $"SELECT e.enumlabel FROM pg_enum e WHERE e.enumtypid = '{type.Replace("'", "''")}'::regtype ORDER BY e.enumsortorder",
                database, ReadLimits, 500, ct);
            return result.Rows.Select(r => r[0]?.ToString()).OfType<string>().ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }
    }

    /// <summary>Runs the INSERTs in one transaction, reporting rows inserted so far; any failure (or cancel) rolls
    /// every row back.</summary>
    public static async Task<int> InsertAsync(
        IDatabaseProvider provider, DbObject table, TestDataRows rows, IProgress<int>? progress = null,
        CancellationToken ct = default, int timeoutSeconds = 300)
    {
        var statements = TestDataScript.Statements(provider.ProviderKey, table, rows);
        var database = string.IsNullOrEmpty(table.Database) ? null : table.Database;
        await using var session = await provider.BeginScriptSessionAsync(database, transactional: true, ct);
        var done = 0;
        var perStatement = rows.Columns.Count == 0 && provider.ProviderKey != SqlDialect.MySqlKey ? 1 : TestDataScript.RowsPerStatement;
        const int statementsPerCall = 10;
        for (var i = 0; i < statements.Count; i += statementsPerCall)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = statements.Skip(i).Take(statementsPerCall).ToList();
            await session.ExecuteAsync(string.Join("\n", chunk), timeoutSeconds, ct);
            done = Math.Min(rows.Rows.Count, done + chunk.Count * perStatement);
            progress?.Report(done);
        }
        await session.CommitAsync(ct);
        return rows.Rows.Count;
    }
}
