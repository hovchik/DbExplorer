using System.Globalization;
using System.Text.Json;
using DbExplorer.Application.Copy;
using DbExplorer.Application.Query;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Lab;

public enum CostLevel
{
    Cheap,
    Moderate,
    Expensive
}

/// <summary>A full scan the planner expects, with the rows it expects out of it.</summary>
public sealed record PlannedScan(string Table, double Rows);

/// <summary>What the planner expects of a statement, read from its estimated plan.</summary>
/// <param name="Cost">The planner's total cost, in its own units (PostgreSQL cost, SQL Server subtree cost).</param>
public sealed record CostEstimate(double Rows, double? Cost, IReadOnlyList<PlannedScan> Scans, CostLevel Level)
{
    /// <summary>"≈ 12,400 rows · cost 431 · full scan of public.orders".</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string> { $"≈ {Compact(Rows)} row{(Math.Round(Rows) == 1 ? "" : "s")}" };
            if (Cost is { } cost) parts.Add($"cost {Compact(cost)}");
            if (Scans.Count > 0)
                parts.Add("full scan of " + string.Join(", ", Scans.Take(2).Select(s => s.Table)) + (Scans.Count > 2 ? $" +{Scans.Count - 2}" : ""));
            return string.Join(" · ", parts);
        }
    }

    public string Icon => Level switch { CostLevel.Expensive => "🔴", CostLevel.Moderate => "🟠", _ => "🟢" };

    private static string Compact(double n) => n switch
    {
        >= 1_000_000_000 => (n / 1_000_000_000).ToString("0.#", CultureInfo.InvariantCulture) + "B",
        >= 1_000_000 => (n / 1_000_000).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        >= 10_000 => (n / 1_000).ToString("0.#", CultureInfo.InvariantCulture) + "k",
        >= 100 => n.ToString("N0", CultureInfo.InvariantCulture),
        _ => n.ToString("0.##", CultureInfo.InvariantCulture)
    };
}

/// <summary>
/// The cost lens: while you type, the statement at the caret is shown with what the database's planner expects of it
/// (rows, cost, full scans), before it is run. Only a single read-only query is ever estimated, and only by asking for its
/// estimated plan (EXPLAIN / SHOWPLAN_ALL), which does not execute it.
/// </summary>
public static class CostLens
{
    /// <summary>Planner work above which a full scan is worth pointing out.</summary>
    private const double ScanRowsWorthNoting = 1_000;

    /// <summary>The trimmed statement when it can be estimated: a single query that only reads and has no :parameters.</summary>
    public static string? Estimable(string statement)
    {
        var text = statement.Trim().TrimEnd(';').Trim();
        if (text.Length is 0 or > 20_000) return null;
        if (ReadOnlyScriptAnalyzer.Analyze(text) is not { Statements: [{ ReturnsRows: true }] }) return null;
        if (SqlEditorAnalysis.FindParameters(text).Count > 0) return null;
        return text;
    }

    /// <summary>Asks for the estimated plan of <paramref name="statement"/> (see <see cref="Estimable"/>); null when the plan has nothing usable.</summary>
    public static async Task<CostEstimate?> EstimateAsync(DatabaseSession session, string? database, string statement, CancellationToken ct)
    {
        var key = session.Provider.ProviderKey;
        if (key == SqlDialect.SqlServerKey)
        {
            var result = await session.Provider.ExecuteScriptAsync(QueryPlanTools.BuildExplainScript(statement, key, analyze: false), database, 15, ct);
            var plan = result.ResultSets.FirstOrDefault(rs => rs.Columns.Contains("PhysicalOp", StringComparer.OrdinalIgnoreCase));
            return plan is null ? null : FromShowPlan(plan.Columns, plan.Rows);
        }

        var json = await session.Provider.ExecuteScriptAsync($"EXPLAIN (FORMAT JSON) {statement}", database, 15, ct);
        return json.ResultSets.FirstOrDefault()?.Rows.FirstOrDefault()?.FirstOrDefault()?.ToString() is { } text ? FromPostgresPlan(text) : null;
    }

    /// <summary>Reads an <c>EXPLAIN (FORMAT JSON)</c> plan: the top node's rows and cost, and every Seq Scan under it.</summary>
    public static CostEstimate? FromPostgresPlan(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array) root = root.EnumerateArray().FirstOrDefault();
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Plan", out var plan)) return null;

        var scans = new List<PlannedScan>();
        void Walk(JsonElement node)
        {
            var type = node.TryGetProperty("Node Type", out var t) ? t.GetString() : null;
            if (type == "Seq Scan" && node.TryGetProperty("Relation Name", out var rel))
            {
                var schema = node.TryGetProperty("Schema", out var s) ? s.GetString() + "." : "";
                // Work done by a scan is better shown by its cost than its output rows (a filter can drop most rows).
                var rows = node.TryGetProperty("Plan Rows", out var r) ? r.GetDouble() : 0;
                var cost = node.TryGetProperty("Total Cost", out var c) ? c.GetDouble() : 0;
                if (cost >= ScanRowsWorthNoting || rows >= ScanRowsWorthNoting) scans.Add(new PlannedScan(schema + rel.GetString(), rows));
            }
            if (node.TryGetProperty("Plans", out var children))
                foreach (var child in children.EnumerateArray()) Walk(child);
        }
        Walk(plan);

        var total = plan.TryGetProperty("Total Cost", out var tc) ? tc.GetDouble() : (double?)null;
        var planRows = plan.TryGetProperty("Plan Rows", out var pr) ? pr.GetDouble() : 0;
        var level = total switch
        {
            >= 100_000 => CostLevel.Expensive,
            >= 5_000 => CostLevel.Moderate,
            _ => scans.Count > 0 ? CostLevel.Moderate : CostLevel.Cheap
        };
        return new CostEstimate(planRows, total, Distinct(scans), level);
    }

    /// <summary>Reads a <c>SHOWPLAN_ALL</c> result: the statement row's rows and subtree cost, and the scans below it.</summary>
    public static CostEstimate? FromShowPlan(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        int Col(string name) => columns.ToList().FindIndex(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
        var op = Col("PhysicalOp");
        var estimate = Col("EstimateRows");
        var cost = Col("TotalSubtreeCost");
        var argument = Col("Argument");
        if (op < 0 || estimate < 0 || rows.Count == 0) return null;

        double Number(IReadOnlyList<object?> row, int i) =>
            i >= 0 && row[i] is { } v && v is not DBNull ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : 0;

        // The first row describes the statement as a whole (PhysicalOp is NULL there).
        var top = rows[0];
        var scans = new List<PlannedScan>();
        foreach (var row in rows.Skip(1))
        {
            var physical = row[op]?.ToString() ?? "";
            if (physical is not ("Table Scan" or "Clustered Index Scan" or "Index Scan")) continue;
            var scanned = Number(row, estimate);
            if (scanned < ScanRowsWorthNoting) continue;
            var table = argument >= 0 ? TableOf(row[argument]?.ToString() ?? "") : null;
            scans.Add(new PlannedScan(table ?? physical, scanned));
        }

        var total = cost >= 0 ? Number(top, cost) : (double?)null;
        var level = total switch
        {
            >= 50 => CostLevel.Expensive,
            >= 1 => CostLevel.Moderate,
            _ => scans.Count > 0 ? CostLevel.Moderate : CostLevel.Cheap
        };
        return new CostEstimate(Number(top, estimate), total, Distinct(scans), level);
    }

    private static IReadOnlyList<PlannedScan> Distinct(List<PlannedScan> scans) =>
        scans.GroupBy(s => s.Table, StringComparer.OrdinalIgnoreCase).Select(g => g.MaxBy(s => s.Rows)!).OrderByDescending(s => s.Rows).ToList();

    /// <summary>"OBJECT:([db].[dbo].[Orders].[PK_Orders])" → "dbo.Orders".</summary>
    private static string? TableOf(string argument)
    {
        var start = argument.IndexOf("OBJECT:(", StringComparison.Ordinal);
        if (start < 0) return null;
        var end = argument.IndexOf(')', start);
        var inner = argument[(start + 8)..(end < 0 ? argument.Length : end)];
        var alias = inner.IndexOf(" AS ", StringComparison.OrdinalIgnoreCase);
        if (alias >= 0) inner = inner[..alias];
        var parts = inner
            .Split('.').Select(p => p.Trim().Trim('[', ']')).Where(p => p.Length > 0).ToList();
        // [db].[schema].[table].[index] (index scans) or [db].[schema].[table] (heap scans).
        if (parts.Count >= 3) return $"{parts[1]}.{parts[2]}";
        return parts.Count == 2 ? $"{parts[0]}.{parts[1]}" : null;
    }
}
