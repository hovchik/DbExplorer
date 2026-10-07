using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DbExplorer.Application.Query.Plans;

/// <summary>
/// Reads MySQL's tree plans (<c>EXPLAIN FORMAT=TREE</c> / <c>EXPLAIN ANALYZE</c>) and MariaDB's JSON plans
/// (<c>EXPLAIN FORMAT=JSON</c> / <c>ANALYZE FORMAT=JSON</c>) into <see cref="ExecutionPlan"/>s.
/// </summary>
public static class MySqlPlanParser
{
    private const string Provider = "MySql";

    /// <summary>Rows above which an estimated full scan is worth pointing out when the plan says nothing better.</summary>
    private const double LargeScanRows = ExecutionPlan.FullScanRows;

    public static bool IsTree(string text) => text.TrimStart().StartsWith("->", StringComparison.Ordinal);

    public static bool IsMariaDbJson(string text) =>
        text.TrimStart().StartsWith('{') && text.Contains("\"query_block\"", StringComparison.Ordinal);

    // ----- MySQL tree -----

    private static readonly Regex TreeLine = new(
        @"^(?<indent>\s*)-> (?<label>.*?)(?:\s+\(cost=(?<cost>[\d.e+-]+)(?:\.\.(?<cost2>[\d.e+-]+))? rows=(?<rows>[\d.e+-]+)\))?" +
        @"(?:\s+\(actual time=(?<first>[\d.e+-]+)\.\.(?<last>[\d.e+-]+) rows=(?<arows>[\d.e+-]+) loops=(?<loops>\d+)\))?" +
        @"(?:\s+\(never executed\))?\s*$",
        RegexOptions.CultureInvariant);

    private static readonly Regex OnTable = new(
        @"^(?<op>.*?) on (?<table><[^>]+>|\S+)(?: using (?<index>\S+))?(?<rest>.*)$", RegexOptions.CultureInvariant);

    /// <summary>One plan from MySQL's tree text; throws <see cref="FormatException"/> when it is not one.</summary>
    public static ExecutionPlan ParseTree(string text, string? statement = null)
    {
        var lines = text.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).ToList();
        var stack = new List<(int Indent, PlanNode Node)>();
        PlanNode? root = null;
        foreach (var line in lines)
        {
            var m = TreeLine.Match(line);
            if (!m.Success) continue;
            var node = TreeNode(m);
            var indent = m.Groups["indent"].Length;
            while (stack.Count > 0 && stack[^1].Indent >= indent) stack.RemoveAt(stack.Count - 1);
            if (stack.Count == 0)
            {
                if (root is not null) break; // a second top-level tree does not belong to this statement
                root = node;
            }
            else stack[^1].Node.Add(node);
            stack.Add((indent, node));
        }
        if (root is null) throw new FormatException("The text is not a MySQL tree plan.");

        foreach (var node in root.DescendantsAndSelf()) SetOwnWeight(node);
        var isActual = root.ActualRows is not null;
        var plan = new ExecutionPlan(Provider, root, isActual, statement, text) { TotalCost = root.SubtreeCost };
        plan.AddCommonWarnings(n => (IsFullScan(n), n.ActualRows, n.EstimatedRows * Math.Max(1, n.EstimatedExecutions) >= LargeScanRows));
        foreach (var node in plan.Nodes) AddTreeWarnings(node);
        return plan;
    }

    private static PlanNode TreeNode(Match m)
    {
        var label = m.Groups["label"].Value.Trim();
        string op = label, detail = "";
        string? obj = null;
        var colon = label.IndexOf(": ", StringComparison.Ordinal);
        if (OnTable.Match(label) is { Success: true } on && (colon < 0 || on.Groups["op"].Length < colon))
        {
            op = on.Groups["op"].Value;
            obj = on.Groups["table"].Value + (on.Groups["index"].Success ? " using " + on.Groups["index"].Value : "");
            detail = on.Groups["rest"].Value.Trim();
        }
        else if (colon > 0)
        {
            op = label[..colon];
            detail = label[(colon + 2)..];
        }

        double? D(string group) => m.Groups[group].Success && double.TryParse(m.Groups[group].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        var loops = D("loops");
        var properties = new List<KeyValuePair<string, string>> { new("Operation", label) };
        if (D("cost") is { } c) properties.Add(new("Cost", c.ToString("0.###", CultureInfo.InvariantCulture)));
        if (D("rows") is { } r) properties.Add(new("Estimated rows", r.ToString("0.###", CultureInfo.InvariantCulture)));
        if (D("first") is { } first) properties.Add(new("First row (ms)", first.ToString("0.###", CultureInfo.InvariantCulture)));
        if (D("last") is { } last) properties.Add(new("All rows (ms)", last.ToString("0.###", CultureInfo.InvariantCulture)));
        if (D("arows") is { } ar) properties.Add(new("Actual rows per loop", ar.ToString("0.###", CultureInfo.InvariantCulture)));
        if (loops is { } l) properties.Add(new("Loops", l.ToString("0", CultureInfo.InvariantCulture)));
        if (m.Value.Contains("(never executed)", StringComparison.Ordinal)) properties.Add(new("Executed", "never"));

        return new PlanNode
        {
            Operator = op,
            Object = obj,
            Detail = detail.Length == 0 ? null : detail.Trim('(', ')', ' '),
            EstimatedRows = D("rows") ?? 0,
            // Like PostgreSQL, MySQL reports actual rows and time per loop; the plan model keeps totals.
            ActualRows = D("arows") is { } rows ? rows * (loops ?? 1) : m.Value.Contains("(never executed)") ? 0 : null,
            ActualExecutions = loops ?? (m.Value.Contains("(never executed)") ? 0 : null),
            ActualTimeMs = D("last") is { } t ? t * (loops ?? 1) : null,
            SubtreeCost = D("cost2") ?? D("cost"),
            Properties = properties
        };
    }

    private static bool IsFullScan(PlanNode node) =>
        node.Operator.StartsWith("Table scan", StringComparison.Ordinal) ||
        node.Operator.StartsWith("Full scan", StringComparison.Ordinal);

    private static void AddTreeWarnings(PlanNode node)
    {
        if (node.Operator.StartsWith("Sort", StringComparison.Ordinal) && node.RowsOut >= 100_000)
            node.Warnings.Add(new PlanWarning(PlanWarningKind.Spill,
                $"{node.Operator} of ≈{PlanFormat.Count(node.RowsOut)} rows: an index in that order avoids the sort", node));
        if (node.Operator.Contains("temporary", StringComparison.OrdinalIgnoreCase) && node.RowsOut >= 100_000)
            node.Warnings.Add(new PlanWarning(PlanWarningKind.Spill,
                $"{node.Operator} holds ≈{PlanFormat.Count(node.RowsOut)} rows: it goes to disk past tmp_table_size", node));
    }

    // ----- MariaDB JSON -----

    /// <summary>One plan from MariaDB's JSON; throws <see cref="JsonException"/> when it is not one.</summary>
    public static ExecutionPlan ParseMariaDbJson(string json, string? statement = null)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("query_block", out var block))
            throw new JsonException("The text is not a MariaDB JSON plan.");

        var root = QueryBlock(block, "Query block");
        foreach (var node in root.DescendantsAndSelf()) SetOwnWeight(node);
        var isActual = root.DescendantsAndSelf().Any(n => n.ActualRows is not null);
        var planningMs = doc.RootElement.TryGetProperty("query_optimization", out var opt) ? Number(opt, "r_total_time_ms") : null;
        var plan = new ExecutionPlan(Provider, root, isActual, statement, json)
        {
            TotalCost = Number(block, "cost"),
            PlanningTimeMs = planningMs,
            ExecutionTimeMs = Number(block, "r_total_time_ms")
        };
        plan.AddCommonWarnings(n => (IsJsonFullScan(n), n.ActualRows, n.EstimatedRows * Math.Max(1, n.EstimatedExecutions) >= LargeScanRows));
        return plan;
    }

    private static bool IsJsonFullScan(PlanNode node) => node.Operator == "Table scan";

    private static PlanNode QueryBlock(JsonElement block, string name)
    {
        var node = new PlanNode
        {
            Operator = name + (Number(block, "select_id") is { } id ? $" #{id}" : ""),
            Detail = Text(block, "having_condition") is { } having ? "having " + having : null,
            SubtreeCost = Number(block, "cost"),
            ActualExecutions = Number(block, "r_loops"),
            ActualTimeMs = Number(block, "r_total_time_ms"),
            Properties = Scalars(block)
        };
        AddChildren(node, block);
        return node;
    }

    /// <summary>Adds the operators found in <paramref name="e"/>'s known members, in the order MariaDB writes them.</summary>
    private static void AddChildren(PlanNode parent, JsonElement e)
    {
        foreach (var p in e.EnumerateObject())
        {
            switch (p.Name)
            {
                case "nested_loop" when p.Value.ValueKind == JsonValueKind.Array:
                {
                    var loop = new PlanNode { Operator = "Nested loop", Properties = [] };
                    foreach (var item in p.Value.EnumerateArray()) AddChildren(loop, item);
                    // A nested loop over one table is just that table.
                    if (loop.Children.Count == 1) parent.Add(loop.Children[0]);
                    else parent.Add(loop);
                    break;
                }
                case "table":
                    parent.Add(Table(p.Value));
                    break;
                case "block-nl-join" or "bka-join" or "hash-join":
                {
                    var join = new PlanNode
                    {
                        Operator = p.Name switch { "bka-join" => "Batched key access join", "hash-join" => "Hash join", _ => "Block nested loop join" },
                        Detail = Text(p.Value, "attached_condition"),
                        ActualExecutions = Number(p.Value, "r_loops"),
                        Properties = Scalars(p.Value)
                    };
                    AddChildren(join, p.Value);
                    parent.Add(join);
                    break;
                }
                case "filesort":
                {
                    var sort = new PlanNode
                    {
                        Operator = "Sort",
                        Detail = Text(p.Value, "sort_key"),
                        ActualExecutions = Number(p.Value, "r_loops"),
                        ActualTimeMs = Number(p.Value, "r_total_time_ms"),
                        ActualRows = Number(p.Value, "r_output_rows"),
                        Properties = Scalars(p.Value)
                    };
                    AddChildren(sort, p.Value);
                    parent.Add(sort);
                    break;
                }
                case "temporary_table":
                {
                    var temp = new PlanNode { Operator = "Temporary table", Properties = Scalars(p.Value) };
                    AddChildren(temp, p.Value);
                    parent.Add(temp);
                    break;
                }
                case "duplicates_removal" or "read_sorted_file" or "window_functions_computation":
                {
                    var step = new PlanNode
                    {
                        Operator = p.Name switch { "duplicates_removal" => "Remove duplicates", "read_sorted_file" => "Read sorted rows", _ => "Window functions" },
                        Properties = Scalars(p.Value)
                    };
                    AddChildren(step, p.Value);
                    parent.Add(step);
                    break;
                }
                case "materialized" or "query_block":
                    parent.Add(QueryBlock(p.Value.TryGetProperty("query_block", out var qb) ? qb : p.Value, p.Name == "materialized" ? "Materialize" : "Query block"));
                    break;
                case "subqueries" when p.Value.ValueKind == JsonValueKind.Array:
                    foreach (var sub in p.Value.EnumerateArray())
                        if (sub.TryGetProperty("query_block", out var sq)) parent.Add(QueryBlock(sq, "Subquery"));
                        else AddChildren(parent, sub);
                    break;
                case "union_result":
                {
                    var union = new PlanNode
                    {
                        Operator = "Union",
                        Object = Text(p.Value, "table_name"),
                        ActualExecutions = Number(p.Value, "r_loops"),
                        ActualRows = Number(p.Value, "r_rows"),
                        Properties = Scalars(p.Value)
                    };
                    if (p.Value.TryGetProperty("query_specifications", out var specs) && specs.ValueKind == JsonValueKind.Array)
                        foreach (var spec in specs.EnumerateArray())
                            if (spec.TryGetProperty("query_block", out var sb)) union.Add(QueryBlock(sb, "Query block"));
                    parent.Add(union);
                    break;
                }
            }
        }
    }

    private static PlanNode Table(JsonElement t)
    {
        var access = Text(t, "access_type") ?? "";
        var name = Text(t, "table_name");
        var key = Text(t, "key");
        var rows = Number(t, "rows") ?? 0;
        var filtered = Number(t, "filtered") ?? 100;
        var loops = Number(t, "loops");
        var rLoops = Number(t, "r_loops");
        var rRows = Number(t, "r_rows");
        var rFiltered = Number(t, "r_filtered") ?? 100;
        var time = (Number(t, "r_table_time_ms") ?? 0) + (Number(t, "r_other_time_ms") ?? 0);

        var node = new PlanNode
        {
            Operator = access switch
            {
                "ALL" => "Table scan",
                "index" => "Full index scan",
                "range" => "Index range scan",
                "ref" or "ref_or_null" => "Index lookup",
                "eq_ref" => "Unique index lookup",
                "const" or "system" => "Constant row",
                "index_merge" => "Index merge",
                "fulltext" => "Full-text search",
                "hash_ALL" or "hash_index" or "hash_range" => "Hash join scan",
                "" => "Table",
                _ => access
            },
            Object = name is null ? null : key is null ? name : $"{name} using {key}",
            Detail = Text(t, "attached_condition") ?? (t.TryGetProperty("ref", out var r) && r.ValueKind == JsonValueKind.Array
                ? string.Join(", ", r.EnumerateArray().Select(x => x.ToString())) : null),
            // Rows read per execution, kept after the table's condition.
            EstimatedRows = rows * filtered / 100,
            EstimatedExecutions = loops ?? 1,
            ActualRows = rRows is { } rr ? rr * rFiltered / 100 * (rLoops ?? 1) : null,
            ActualExecutions = rLoops,
            ActualTimeMs = rRows is null ? null : time,
            SubtreeCost = Number(t, "cost"),
            Properties = Scalars(t)
        };
        if (t.TryGetProperty("materialized", out var materialized) && materialized.TryGetProperty("query_block", out var mq))
            node.Add(QueryBlock(mq, "Materialize"));
        return node;
    }

    // ----- shared -----

    /// <summary>Own work: what this node adds on top of its children (time when measured, else cost, else rows).</summary>
    private static void SetOwnWeight(PlanNode node)
    {
        if (node.ActualTimeMs is { } total)
            node.OwnWeight = total - node.Children.Sum(c => c.ActualTimeMs ?? 0);
        else if (node.SubtreeCost is { } cost)
            node.OwnWeight = cost - node.Children.Sum(c => c.SubtreeCost ?? 0);
        else if (node.Children.Count == 0)
            node.OwnWeight = node.EstimatedRows * Math.Max(1, node.EstimatedExecutions);
    }

    private static List<KeyValuePair<string, string>> Scalars(JsonElement e) =>
        e.ValueKind != JsonValueKind.Object ? [] :
        e.EnumerateObject()
            .Where(p => p.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null) &&
                        !(p.Value.ValueKind == JsonValueKind.Array && p.Value.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.Object)))
            .Select(p => new KeyValuePair<string, string>(p.Name, Display(p.Value)))
            .ToList();

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Number(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static string Display(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.Number => v.TryGetInt64(out var l) ? l.ToString(CultureInfo.InvariantCulture) : v.GetDouble().ToString("0.###", CultureInfo.InvariantCulture),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Array => string.Join(", ", v.EnumerateArray().Select(Display)),
        _ => v.GetRawText()
    };
}
