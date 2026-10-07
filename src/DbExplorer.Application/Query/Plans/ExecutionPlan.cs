using System.Globalization;

namespace DbExplorer.Application.Query.Plans;

public enum PlanWarningKind
{
    /// <summary>A table read from start to end (Seq Scan, Table Scan, Clustered Index Scan) over many rows.</summary>
    FullScan,

    /// <summary>The planner expected far fewer or far more rows than the operator really produced.</summary>
    EstimateGap,

    /// <summary>A sort or hash ran out of memory and spilled to disk / tempdb.</summary>
    Spill,

    /// <summary>A per-row lookup into the table (Key Lookup / RID Lookup) for columns the index does not cover.</summary>
    Lookup,

    /// <summary>The engine suggests an index it did not have (SQL Server missing-index hint).</summary>
    MissingIndex,

    /// <summary>Anything else the engine flagged (no join predicate, columns without statistics, implicit conversion…).</summary>
    Engine
}

/// <summary>Something in a plan worth a look; <see cref="Node"/> is the operator it is about (null: the whole statement).</summary>
public sealed record PlanWarning(PlanWarningKind Kind, string Message, PlanNode? Node = null)
{
    public string Icon => Kind switch
    {
        PlanWarningKind.FullScan => "▤",
        PlanWarningKind.EstimateGap => "≠",
        PlanWarningKind.Spill => "⛁",
        PlanWarningKind.Lookup => "↻",
        PlanWarningKind.MissingIndex => "＋",
        _ => "⚠"
    };
}

/// <summary>One operator of an execution plan, with what the planner expected of it and, when the plan was measured,
/// what really happened.</summary>
public sealed class PlanNode
{
    /// <summary>Position in the plan, depth first from 0 (the root).</summary>
    public int Id { get; internal set; }

    /// <summary>"Hash Join", "Seq Scan", "Clustered Index Seek"…</summary>
    public required string Operator { get; init; }

    /// <summary>What the operator works on: "public.orders", "dbo.Orders.IX_Orders_Customer", a CTE or function name.</summary>
    public string? Object { get; init; }

    /// <summary>One short line under the operator: its join kind, condition or filter.</summary>
    public string? Detail { get; init; }

    /// <summary>Rows the planner expected per execution.</summary>
    public double EstimatedRows { get; init; }

    /// <summary>Executions the planner expected (SQL Server rebinds/rewinds + 1; PostgreSQL does not say, so 1).</summary>
    public double EstimatedExecutions { get; init; } = 1;

    /// <summary>Rows really produced, all executions together; null for an estimated plan.</summary>
    public double? ActualRows { get; init; }

    /// <summary>How many times the operator really ran; null for an estimated plan.</summary>
    public double? ActualExecutions { get; init; }

    /// <summary>Planner cost of this operator and everything under it, in the engine's own units.</summary>
    public double? SubtreeCost { get; init; }

    /// <summary>Time spent in this operator and everything under it, in milliseconds; null for an estimated plan.</summary>
    public double? ActualTimeMs { get; init; }

    /// <summary>Every property the engine reported for the operator, for the details panel.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Properties { get; init; } = [];

    public List<PlanNode> Children { get; } = [];

    public PlanNode? Parent { get; private set; }

    /// <summary>The operator's own share of the work (cost, or measured time when the plan has it), 0..1.</summary>
    public double Share { get; internal set; }

    public List<PlanWarning> Warnings { get; } = [];

    /// <summary>Among the few operators doing most of the work.</summary>
    public bool IsHotspot { get; internal set; }

    /// <summary>Set by the parsers while building: lets <see cref="ExecutionPlan"/> compute shares.</summary>
    internal double OwnWeight { get; set; }

    internal void Add(PlanNode child)
    {
        child.Parent = this;
        Children.Add(child);
    }

    /// <summary>Rows per execution as measured; null for an estimated plan.</summary>
    public double? ActualRowsPerExecution =>
        ActualRows is { } rows ? ActualExecutions is { } n && n > 0 ? rows / n : rows : null;

    /// <summary>Rows that flow out of the operator in total, measured when possible.</summary>
    public double RowsOut => ActualRows ?? EstimatedRows * Math.Max(1, EstimatedExecutions);

    public IEnumerable<PlanNode> DescendantsAndSelf()
    {
        yield return this;
        foreach (var child in Children)
        foreach (var node in child.DescendantsAndSelf())
            yield return node;
    }

    /// <summary>"≈ 1,200" estimated, plus "→ 9,875" measured when known.</summary>
    public string RowsText =>
        ActualRows is { } actual
            ? $"{PlanFormat.Count(actual)} of ≈{PlanFormat.Count(EstimatedRows * Math.Max(1, ActualExecutions ?? 1))} est."
            : $"≈ {PlanFormat.Count(EstimatedRows)} rows";

    public override string ToString() => Object is null ? Operator : $"{Operator} · {Object}";
}

/// <summary>The plan of one statement: its operator tree, and what stands out in it.</summary>
public sealed class ExecutionPlan
{
    /// <summary>Ratio between estimated and actual rows above which the estimate is worth pointing out.</summary>
    public const double EstimateGapRatio = 10;

    /// <summary>Rows read by a full scan above which it is worth pointing out.</summary>
    public const double FullScanRows = 10_000;

    public ExecutionPlan(string provider, PlanNode root, bool isActual, string? statement, string raw)
    {
        Provider = provider;
        Root = root;
        IsActual = isActual;
        Statement = statement;
        Raw = raw;
        Nodes = root.DescendantsAndSelf().ToList();
        for (var i = 0; i < Nodes.Count; i++) Nodes[i].Id = i;

        var total = Nodes.Sum(n => Math.Max(0, n.OwnWeight));
        foreach (var node in Nodes) node.Share = total > 0 ? Math.Max(0, node.OwnWeight) / total : 0;
        // The few operators that do most of the work: up to three, each at least 15% of the total.
        foreach (var node in Nodes.Where(n => n.Share >= 0.15).OrderByDescending(n => n.Share).Take(3)) node.IsHotspot = true;
    }

    /// <summary>"PostgreSQL" or "SqlServer" (the provider key).</summary>
    public string Provider { get; }

    public PlanNode Root { get; }

    /// <summary>The statement was run and measured (EXPLAIN ANALYZE / actual plan), not only estimated.</summary>
    public bool IsActual { get; }

    public string? Statement { get; }

    /// <summary>The plan as the engine returned it (JSON or showplan XML), for copying or saving.</summary>
    public string Raw { get; }

    /// <summary>Every operator, depth first; a node's <see cref="PlanNode.Id"/> is its index here.</summary>
    public IReadOnlyList<PlanNode> Nodes { get; }

    public double? TotalCost { get; init; }
    public double? PlanningTimeMs { get; init; }
    public double? ExecutionTimeMs { get; init; }

    /// <summary>Warnings about the statement as a whole (missing indexes), on top of those on its operators.</summary>
    public List<PlanWarning> StatementWarnings { get; } = [];

    /// <summary>Every warning: the statement's first, then the operators' in plan order.</summary>
    public IReadOnlyList<PlanWarning> Warnings => [.. StatementWarnings, .. Nodes.SelectMany(n => n.Warnings)];

    /// <summary>"Actual plan · 23.4 ms · cost 431 · 12 operators · 2 warnings".</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string> { IsActual ? "Actual plan" : "Estimated plan (not run)" };
            if (ExecutionTimeMs is { } exec) parts.Add($"executed in {PlanFormat.Ms(exec)}");
            else if (IsActual && Root.ActualTimeMs is { } t) parts.Add($"ran in {PlanFormat.Ms(t)}");
            if (PlanningTimeMs is { } plan) parts.Add($"planned in {PlanFormat.Ms(plan)}");
            if ((TotalCost ?? Root.SubtreeCost) is { } cost) parts.Add($"cost {PlanFormat.Number(cost)}");
            parts.Add($"{Nodes.Count} operator{(Nodes.Count == 1 ? "" : "s")}");
            var warnings = Warnings.Count;
            if (warnings > 0) parts.Add($"{warnings} warning{(warnings == 1 ? "" : "s")}");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Adds the warnings every engine shares: full scans over many rows and big estimate-vs-actual gaps.
    /// <paramref name="scan"/> says whether a node reads a whole table and how many rows it reads (null when the engine
    /// does not say; then <c>Large</c> decides).</summary>
    internal void AddCommonWarnings(Func<PlanNode, (bool IsFullScan, double? RowsRead, bool Large)> scan)
    {
        foreach (var node in Nodes)
        {
            var (isFullScan, rowsRead, large) = scan(node);
            if (isFullScan && (rowsRead is { } read ? read >= FullScanRows : large))
                node.Warnings.Add(new PlanWarning(PlanWarningKind.FullScan,
                    $"Full scan of {node.Object ?? "a table"}" +
                    (rowsRead is { } r ? $" reads ≈{PlanFormat.Count(r)} rows" : $" (cost {PlanFormat.Number(node.SubtreeCost ?? 0)})") +
                    (node.Detail is { Length: > 0 } ? " to keep the rows matching its filter: an index on the filtered columns may help" : ""),
                    node));
        }

        // A wrong estimate is passed up to every operator above it; point at the one where it starts.
        foreach (var node in Nodes)
        {
            if (Gap(node) is not { } gap) continue;
            if (node.Children.Any(c => Gap(c) is { } below && below.More == gap.More)) continue;
            var (estimated, actual, factor, more) = gap;
            node.Warnings.Add(new PlanWarning(PlanWarningKind.EstimateGap,
                $"{node.Operator}{(node.Object is null ? "" : " on " + node.Object)} expected ≈{PlanFormat.Count(estimated)} rows but got {PlanFormat.Count(actual)} " +
                $"({factor.ToString(factor >= 100 ? "N0" : "0.#", CultureInfo.InvariantCulture)}× {(more ? "more" : "fewer")}): statistics may be out of date",
                node));
        }
    }

    /// <summary>The estimate-vs-actual gap of a measured operator, when it is big enough to matter.</summary>
    private static (double Estimated, double Actual, double Factor, bool More)? Gap(PlanNode node)
    {
        if (node.ActualRowsPerExecution is not { } actual) return null;
        var estimated = node.EstimatedRows;
        var high = Math.Max(actual, estimated);
        var low = Math.Max(Math.Min(actual, estimated), 1);
        // Under a LIMIT / TOP, inputs stop early by design: fewer rows than planned is expected there.
        if (actual < estimated && HasLimitAbove(node)) return null;
        return high >= 100 && high / low >= EstimateGapRatio ? (estimated, actual, high / low, actual > estimated) : null;
    }

    private static bool HasLimitAbove(PlanNode node)
    {
        for (var p = node.Parent; p is not null; p = p.Parent)
            if (p.Operator.StartsWith("Limit", StringComparison.Ordinal) || p.Operator.StartsWith("Top", StringComparison.Ordinal)) return true;
        return false;
    }
}

/// <summary>Number formatting shared by the plan views.</summary>
public static class PlanFormat
{
    /// <summary>12 · 1,234 · 45.6k · 1.2M.</summary>
    public static string Count(double n) => n switch
    {
        >= 1_000_000_000 => (n / 1_000_000_000).ToString("0.#", CultureInfo.InvariantCulture) + "B",
        >= 1_000_000 => (n / 1_000_000).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        >= 100_000 => (n / 1_000).ToString("0", CultureInfo.InvariantCulture) + "k",
        >= 10_000 => (n / 1_000).ToString("0.#", CultureInfo.InvariantCulture) + "k",
        _ => Math.Round(n).ToString("N0", CultureInfo.InvariantCulture)
    };

    public static string Number(double n) => n switch
    {
        >= 10_000 => Count(n),
        >= 100 => n.ToString("N0", CultureInfo.InvariantCulture),
        >= 1 => n.ToString("0.##", CultureInfo.InvariantCulture),
        _ => n.ToString("0.####", CultureInfo.InvariantCulture)
    };

    public static string Ms(double ms) => ms >= 1000
        ? (ms / 1000).ToString("0.##", CultureInfo.InvariantCulture) + " s"
        : ms.ToString(ms >= 10 ? "0.#" : "0.###", CultureInfo.InvariantCulture) + " ms";

    public static string Percent(double share) => share switch
    {
        <= 0 => "0%",
        < 0.01 => "<1%",
        _ => (share * 100).ToString("0", CultureInfo.InvariantCulture) + "%"
    };
}
