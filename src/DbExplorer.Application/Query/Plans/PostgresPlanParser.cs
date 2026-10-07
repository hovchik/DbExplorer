using System.Globalization;
using System.Text.Json;

namespace DbExplorer.Application.Query.Plans;

/// <summary>Reads <c>EXPLAIN (FORMAT JSON [, ANALYZE])</c> output into <see cref="ExecutionPlan"/>s.</summary>
public static class PostgresPlanParser
{
    /// <summary>Properties shown as the node's one-line detail, first found wins.</summary>
    private static readonly string[] DetailKeys =
        ["Hash Cond", "Merge Cond", "Index Cond", "Recheck Cond", "Join Filter", "Filter", "Sort Key", "Group Key", "Cache Key"];

    /// <summary>Properties already shown elsewhere on the node or meaningless in the details panel.</summary>
    private static readonly HashSet<string> Hidden = ["Plans", "Node Type"];

    /// <summary>One plan per statement in <paramref name="json"/>; throws <see cref="JsonException"/> when it is not plan JSON.</summary>
    public static IReadOnlyList<ExecutionPlan> Parse(string json, string? statement = null)
    {
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
        var plans = new List<ExecutionPlan>();
        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("Plan", out var planElement)) continue;
            var root = ReadNode(planElement);
            var isActual = root.ActualRows is not null;
            var plan = new ExecutionPlan("PostgreSQL", root, isActual, statement, json)
            {
                TotalCost = root.SubtreeCost,
                PlanningTimeMs = Number(item, "Planning Time"),
                ExecutionTimeMs = Number(item, "Execution Time")
            };
            plan.AddCommonWarnings(node => (IsFullScan(node), RowsRead(node), node.SubtreeCost >= LargeScanCost));
            foreach (var node in plan.Nodes) AddSpills(node);
            plans.Add(plan);
        }
        if (plans.Count == 0) throw new JsonException("The text is not a PostgreSQL JSON plan.");
        return plans;
    }

    private static PlanNode ReadNode(JsonElement e)
    {
        var type = Text(e, "Node Type") ?? "?";
        var loops = Number(e, "Actual Loops");
        var actualRows = Number(e, "Actual Rows");
        var properties = new List<KeyValuePair<string, string>>();
        foreach (var p in e.EnumerateObject())
        {
            if (Hidden.Contains(p.Name)) continue;
            properties.Add(new(p.Name, Display(p.Value)));
        }

        var node = new PlanNode
        {
            Operator = OperatorName(e, type),
            Object = ObjectName(e),
            Detail = DetailKeys.Select(k => Text(e, k) ?? Join(e, k)).FirstOrDefault(v => !string.IsNullOrEmpty(v)),
            EstimatedRows = Number(e, "Plan Rows") ?? 0,
            SubtreeCost = Number(e, "Total Cost"),
            // PostgreSQL reports actual rows and time per loop; the plan model keeps totals.
            ActualRows = actualRows is { } r ? r * (loops ?? 1) : null,
            ActualExecutions = loops,
            ActualTimeMs = Number(e, "Actual Total Time") is { } t ? t * (loops ?? 1) : null,
            Properties = properties
        };

        if (e.TryGetProperty("Plans", out var children) && children.ValueKind == JsonValueKind.Array)
            foreach (var child in children.EnumerateArray()) node.Add(ReadNode(child));

        // Own work: what this node adds on top of its children (time when measured, else cost).
        if (node.ActualTimeMs is { } total)
            node.OwnWeight = total - node.Children.Sum(c => c.ActualTimeMs ?? 0);
        else if (node.SubtreeCost is { } cost)
            node.OwnWeight = cost - node.Children.Sum(c => c.SubtreeCost ?? 0);
        return node;
    }

    /// <summary>"Hash Join (Left)", "Aggregate (Hashed)", "Index Scan Backward".</summary>
    private static string OperatorName(JsonElement e, string type)
    {
        // The names EXPLAIN's text format uses: HashAggregate, GroupAggregate, HashSetOp…
        var name = (type, Text(e, "Strategy")) switch
        {
            ("Aggregate" or "SetOp", "Hashed") => "Hash" + type,
            ("Aggregate", "Sorted") => "GroupAggregate",
            ("Aggregate", "Mixed") => "MixedAggregate",
            _ => type
        };
        if (Text(e, "Scan Direction") == "Backward") name += " Backward";
        if (Text(e, "Join Type") is { } join && join != "Inner") name += $" ({join})";
        if (Text(e, "Subplan Name") is { } subplan) name = $"{subplan}: {name}";
        return name;
    }

    private static string? ObjectName(JsonElement e)
    {
        if (Text(e, "Relation Name") is { } relation)
        {
            var name = Text(e, "Schema") is { } schema ? $"{schema}.{relation}" : relation;
            if (Text(e, "Alias") is { } alias && alias != relation) name += $" {alias}";
            if (Text(e, "Index Name") is { } index) name += $" using {index}";
            return name;
        }
        return Text(e, "Index Name") ?? Text(e, "CTE Name") ?? Text(e, "Function Name");
    }

    private static bool IsFullScan(PlanNode node) => node.Operator.StartsWith("Seq Scan", StringComparison.Ordinal) ||
                                                     node.Operator.StartsWith("Parallel Seq Scan", StringComparison.Ordinal);

    /// <summary>Planner cost above which an estimated sequential scan is worth pointing out (≈ 1,000 pages or 100k rows).</summary>
    private const double LargeScanCost = 1_000;

    /// <summary>Rows a scan read when measured: what it returned plus what its filter threw away. An estimated plan only
    /// says the rows kept, so null there.</summary>
    private static double? RowsRead(PlanNode node) =>
        node.ActualRows is { } actual ? actual + Property(node, "Rows Removed by Filter") * (node.ActualExecutions ?? 1) : null;

    private static void AddSpills(PlanNode node)
    {
        var sortSpace = node.Properties.FirstOrDefault(p => p.Key == "Sort Space Type").Value;
        var sortMethod = node.Properties.FirstOrDefault(p => p.Key == "Sort Method").Value;
        if (sortSpace == "Disk" || sortMethod?.Contains("external", StringComparison.OrdinalIgnoreCase) == true)
            node.Warnings.Add(new PlanWarning(PlanWarningKind.Spill,
                $"{node.Operator} spilled to disk ({sortMethod ?? "external sort"}, {Property(node, "Sort Space Used"):N0} kB): raise work_mem or sort on an indexed column",
                node));
        if (Property(node, "Hash Batches") > 1)
            node.Warnings.Add(new PlanWarning(PlanWarningKind.Spill,
                $"{node.Operator} needed {Property(node, "Hash Batches"):N0} batches (spilled to disk): raise work_mem",
                node));
    }

    private static double Property(PlanNode node, string key) =>
        node.Properties.FirstOrDefault(p => p.Key == key).Value is { } v && double.TryParse(v.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? Join(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? string.Join(", ", v.EnumerateArray().Select(Display)) : null;

    private static double? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static string Display(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.Number => v.TryGetInt64(out var l) ? l.ToString(CultureInfo.InvariantCulture) : v.GetDouble().ToString("0.###", CultureInfo.InvariantCulture),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Array => string.Join(", ", v.EnumerateArray().Select(Display)),
        JsonValueKind.Null => "",
        _ => v.GetRawText()
    };
}
