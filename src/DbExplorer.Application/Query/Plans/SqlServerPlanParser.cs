using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace DbExplorer.Application.Query.Plans;

/// <summary>Reads SQL Server showplan XML (<c>SET SHOWPLAN_XML</c> estimated plans, <c>SET STATISTICS XML</c> actual
/// plans) into <see cref="ExecutionPlan"/>s, one per statement that has a plan.</summary>
public static class SqlServerPlanParser
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    private static readonly HashSet<string> ScanOps = ["Table Scan", "Clustered Index Scan", "Index Scan"];

    /// <summary>Throws <see cref="XmlException"/> when <paramref name="xml"/> is not showplan XML.</summary>
    public static IReadOnlyList<ExecutionPlan> Parse(string xml)
    {
        var doc = XDocument.Parse(xml);
        if (doc.Root?.Name != Ns + "ShowPlanXML") throw new XmlException("The text is not SQL Server showplan XML.");

        var plans = new List<ExecutionPlan>();
        foreach (var statement in doc.Root.Descendants().Where(e => e.Name.LocalName.StartsWith("Stmt", StringComparison.Ordinal)))
        {
            var queryPlan = statement.Element(Ns + "QueryPlan");
            var top = queryPlan?.Element(Ns + "RelOp");
            if (queryPlan is null || top is null) continue;

            var root = ReadRelOp(top);
            var isActual = queryPlan.Descendants(Ns + "RunTimeInformation").Any();
            var stats = queryPlan.Element(Ns + "QueryTimeStats");
            var plan = new ExecutionPlan("SqlServer", root, isActual, (string?)statement.Attribute("StatementText"), xml)
            {
                TotalCost = Number(statement, "StatementSubTreeCost") ?? root.SubtreeCost,
                PlanningTimeMs = Number(queryPlan, "CompileTime"),
                ExecutionTimeMs = stats is null ? null : Number(stats, "ElapsedTime")
            };
            plan.AddCommonWarnings(node => (IsFullScan(node), RowsRead(node), false));
            foreach (var node in plan.Nodes)
                if (node.Operator is "Key Lookup" or "RID Lookup")
                    node.Warnings.Add(new PlanWarning(PlanWarningKind.Lookup,
                        $"{node.Operator} on {node.Object ?? "the table"} runs once per row (≈{PlanFormat.Count(node.EstimatedRows * node.EstimatedExecutions)} times): " +
                        "INCLUDE the looked-up columns in the index used to find the rows to avoid it",
                        node));
            AddEngineWarnings(plan, top, queryPlan);
            AddMissingIndexes(plan, queryPlan);
            plans.Add(plan);
        }
        return plans;
    }

    private static PlanNode ReadRelOp(XElement relOp)
    {
        // The element named after the operator (IndexScan, Hash, NestedLoops…), holding its details and inputs.
        var body = relOp.Elements().FirstOrDefault(e => e.Name.LocalName is not ("OutputList" or "Warnings" or "MemoryFractions" or "RunTimeInformation"
            or "RunTimePartitionSummary" or "InternalInfo" or "DefinedValues"));
        var own = body is null ? [] : OwnElements(body).ToList();

        var physical = (string?)relOp.Attribute("PhysicalOp") ?? "?";
        var logical = (string?)relOp.Attribute("LogicalOp");
        var isLookup = body?.Name.LocalName == "IndexScan" && (string?)body.Attribute("Lookup") is "1" or "true";
        var name = isLookup ? "Key Lookup" : physical;
        if (!isLookup && logical is not null && logical != physical && physical is "Hash Match" or "Nested Loops" or "Merge Join" or "Stream Aggregate" or "Sort")
            name += $" ({logical})";

        var counters = relOp.Element(Ns + "RunTimeInformation")?.Elements(Ns + "RunTimeCountersPerThread").ToList() ?? [];
        double? Sum(string attribute) => counters.Count == 0 ? null : counters.Sum(c => Number(c, attribute) ?? 0);

        var obj = own.FirstOrDefault(e => e.Name == Ns + "Object");
        var properties = relOp.Attributes().Select(a => new KeyValuePair<string, string>(a.Name.LocalName, a.Value)).ToList();
        if (obj is not null) properties.Add(new("Object", string.Join(".", obj.Attributes().Where(a => a.Name.LocalName is "Database" or "Schema" or "Table" or "Index").Select(a => a.Value))));
        if (counters.Count > 0)
        {
            properties.Add(new("Actual rows", $"{Sum("ActualRows"):N0}"));
            properties.Add(new("Actual executions", $"{Sum("ActualExecutions"):N0}"));
            if (Sum("ActualRowsRead") is > 0 and var read) properties.Add(new("Actual rows read", $"{read:N0}"));
            if (counters.Any(c => c.Attribute("ActualElapsedms") is not null))
                properties.Add(new("Actual elapsed ms", $"{counters.Max(c => Number(c, "ActualElapsedms") ?? 0):N0}"));
        }
        var predicate = Predicate(own);
        if (predicate is not null) properties.Add(new("Predicate", predicate));
        var output = relOp.Element(Ns + "OutputList")?.Elements(Ns + "ColumnReference").Select(Column).ToList();
        if (output is { Count: > 0 }) properties.Add(new("Output", string.Join(", ", output)));

        var node = new PlanNode
        {
            Operator = name,
            Object = obj is null ? null : ObjectName(obj),
            Detail = predicate,
            EstimatedRows = Number(relOp, "EstimateRows") ?? 0,
            EstimatedExecutions = 1 + (Number(relOp, "EstimateRebinds") ?? 0) + (Number(relOp, "EstimateRewinds") ?? 0),
            SubtreeCost = Number(relOp, "EstimatedTotalSubtreeCost"),
            ActualRows = Sum("ActualRows"),
            ActualExecutions = Sum("ActualExecutions"),
            ActualTimeMs = counters.Any(c => c.Attribute("ActualElapsedms") is not null) ? counters.Max(c => Number(c, "ActualElapsedms") ?? 0) : null,
            Properties = properties
        };

        if (body is not null)
            foreach (var child in ChildRelOps(body)) node.Add(ReadRelOp(child));
        // SSMS's "operator cost": the subtree cost minus the inputs' subtree costs.
        node.OwnWeight = (node.SubtreeCost ?? 0) - node.Children.Sum(c => c.SubtreeCost ?? 0);
        return node;
    }

    /// <summary>The RelOps directly under <paramref name="element"/>, not those of nested operators.</summary>
    private static IEnumerable<XElement> ChildRelOps(XElement element)
    {
        foreach (var child in element.Elements())
        {
            if (child.Name == Ns + "RelOp") yield return child;
            else foreach (var nested in ChildRelOps(child)) yield return nested;
        }
    }

    /// <summary>The descendants of an operator's body that belong to it, stopping at its input operators.</summary>
    private static IEnumerable<XElement> OwnElements(XElement element)
    {
        foreach (var child in element.Elements())
        {
            if (child.Name == Ns + "RelOp") continue;
            yield return child;
            foreach (var nested in OwnElements(child)) yield return nested;
        }
    }

    /// <summary>"dbo.Orders (IX_Orders_Customer)" or "dbo.Orders o".</summary>
    private static string ObjectName(XElement obj)
    {
        static string? A(XElement e, string n) => ((string?)e.Attribute(n))?.Trim('[', ']');
        var name = A(obj, "Schema") is { } schema ? $"{schema}.{A(obj, "Table")}" : A(obj, "Table") ?? "?";
        if (A(obj, "Index") is { } index) name += $" ({index})";
        if (A(obj, "Alias") is { } alias && !string.Equals(alias, A(obj, "Table"), StringComparison.OrdinalIgnoreCase)) name += $" {alias}";
        return name;
    }

    private static string Column(XElement reference)
    {
        var column = (string?)reference.Attribute("Column") ?? "?";
        return (string?)reference.Attribute("Table") is { } table ? $"{table.Trim('[', ']')}.{column}" : column;
    }

    /// <summary>The operator's predicate, seek keys or join keys as one line.</summary>
    private static string? Predicate(IReadOnlyList<XElement> own)
    {
        var seek = own.Where(e => e.Name == Ns + "SeekKeys").Select(keys =>
        {
            var columns = keys.Descendants(Ns + "RangeColumns").SelectMany(r => r.Elements(Ns + "ColumnReference")).Select(c => (string?)c.Attribute("Column")).ToList();
            var values = keys.Descendants(Ns + "RangeExpressions").SelectMany(r => r.Elements(Ns + "ScalarOperator")).Select(s => (string?)s.Attribute("ScalarString")).ToList();
            var scanType = (string?)keys.Descendants().FirstOrDefault(d => d.Attribute("ScanType") is not null)?.Attribute("ScanType");
            var op = scanType switch { "GT" => ">", "GE" => ">=", "LT" => "<", "LE" => "<=", _ => "=" };
            return string.Join(" AND ", columns.Zip(values, (c, v) => $"{c} {op} {v}"));
        }).Where(s => s.Length > 0).ToList();

        var scalar = own.Where(e => e.Name.LocalName is "Predicate" or "ProbeResidual" or "Residual")
            .Select(e => (string?)e.Element(Ns + "ScalarOperator")?.Attribute("ScalarString")).FirstOrDefault(s => !string.IsNullOrEmpty(s));
        var hashKeys = own.FirstOrDefault(e => e.Name == Ns + "HashKeysProbe")?.Elements(Ns + "ColumnReference").Select(Column).ToList();

        var parts = new List<string>();
        if (seek.Count > 0) parts.Add("Seek: " + string.Join(" OR ", seek));
        if (hashKeys is { Count: > 0 }) parts.Add("Hash keys: " + string.Join(", ", hashKeys));
        if (scalar is not null) parts.Add(scalar);
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static bool IsFullScan(PlanNode node) => ScanOps.Contains(node.Operator);

    /// <summary>Rows the scan read: measured, else the planner's rows read / table size, else its output.</summary>
    private static double? RowsRead(PlanNode node)
    {
        double? P(string key) => node.Properties.FirstOrDefault(p => p.Key == key).Value is { } v &&
                                 double.TryParse(v.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        return P("Actual rows read") ?? node.ActualRows ?? P("EstimatedRowsRead") ?? P("TableCardinality") ?? node.EstimatedRows * node.EstimatedExecutions;
    }

    private static void AddEngineWarnings(ExecutionPlan plan, XElement top, XElement queryPlan)
    {
        // Operator warnings: the Warnings element right under each RelOp, in plan order (NodeId order matches Nodes).
        var relOps = new List<XElement> { top };
        void Collect(XElement relOp)
        {
            var body = relOp.Elements().FirstOrDefault(e => e.Name.LocalName is not ("OutputList" or "Warnings" or "MemoryFractions" or "RunTimeInformation"
                or "RunTimePartitionSummary" or "InternalInfo" or "DefinedValues"));
            if (body is null) return;
            foreach (var child in ChildRelOps(body)) { relOps.Add(child); Collect(child); }
        }
        Collect(top);

        for (var i = 0; i < relOps.Count && i < plan.Nodes.Count; i++)
            if (relOps[i].Element(Ns + "Warnings") is { } warnings)
                foreach (var text in Describe(warnings))
                    plan.Nodes[i].Warnings.Add(new PlanWarning(text.Contains("spill", StringComparison.OrdinalIgnoreCase) ? PlanWarningKind.Spill : PlanWarningKind.Engine,
                        $"{plan.Nodes[i].Operator}: {text}", plan.Nodes[i]));

        if (queryPlan.Element(Ns + "Warnings") is { } statementWarnings)
            foreach (var text in Describe(statementWarnings))
                plan.StatementWarnings.Add(new PlanWarning(PlanWarningKind.Engine, text));
    }

    private static IEnumerable<string> Describe(XElement warnings)
    {
        if ((string?)warnings.Attribute("NoJoinPredicate") is "1" or "true")
            yield return "no join predicate: every row is joined with every row of the other input";
        foreach (var w in warnings.Elements())
        {
            switch (w.Name.LocalName)
            {
                case "SpillToTempDb":
                case "SortSpillDetails":
                case "HashSpillDetails":
                case "ExchangeSpillDetails":
                    yield return "spilled to tempdb (not enough memory granted): check the row estimates feeding it";
                    break;
                case "ColumnsWithNoStatistics":
                    yield return "no statistics on " + string.Join(", ", w.Elements(Ns + "ColumnReference").Select(Column)) + ": estimates are guesses";
                    break;
                case "PlanAffectingConvert":
                    yield return $"implicit conversion {(string?)w.Attribute("Expression")} affects {(string?)w.Attribute("ConvertIssue")}: compare values of the column's own type";
                    break;
                case "MemoryGrantWarning":
                    yield return $"memory grant: {(string?)w.Attribute("GrantWarningKind")} (requested {(string?)w.Attribute("RequestedMemory")} KB, used {(string?)w.Attribute("MaxUsedMemory")} KB)";
                    break;
                case "Wait":
                    break;
                default:
                    yield return SplitWords(w.Name.LocalName);
                    break;
            }
        }
    }

    private static void AddMissingIndexes(ExecutionPlan plan, XElement queryPlan)
    {
        foreach (var group in queryPlan.Descendants(Ns + "MissingIndexGroup"))
        {
            var impact = Number(group, "Impact") ?? 0;
            foreach (var index in group.Elements(Ns + "MissingIndex"))
            {
                List<string> Columns(string usage) => index.Elements(Ns + "ColumnGroup")
                    .Where(g => (string?)g.Attribute("Usage") == usage)
                    .SelectMany(g => g.Elements(Ns + "Column")).Select(c => (string?)c.Attribute("Name") ?? "").ToList();
                var keys = Columns("EQUALITY").Concat(Columns("INEQUALITY")).ToList();
                var include = Columns("INCLUDE");
                var table = $"{(string?)index.Attribute("Schema")}.{(string?)index.Attribute("Table")}";
                var sql = $"CREATE INDEX IX_{((string?)index.Attribute("Table"))?.Trim('[', ']')}_{string.Join("_", keys.Select(k => k.Trim('[', ']')))} ON {table} ({string.Join(", ", keys)})" +
                          (include.Count > 0 ? $" INCLUDE ({string.Join(", ", include)})" : "");
                plan.StatementWarnings.Add(new PlanWarning(PlanWarningKind.MissingIndex,
                    $"Missing index (the engine estimates {impact:0}% less cost): {sql}"));
            }
        }
    }

    private static string SplitWords(string name) =>
        string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1]) ? " " + char.ToLowerInvariant(c) : c.ToString()));

    private static double? Number(XElement e, string attribute) =>
        (string?)e.Attribute(attribute) is { } v && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
}
