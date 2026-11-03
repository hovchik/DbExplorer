using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DbExplorer.Application.Copy;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Query.Plans;

/// <summary>
/// SQLite's EXPLAIN QUERY PLAN: rows of (id, parent, notused, detail) forming a tree, one line of text per step
/// ("SCAN orders", "SEARCH customers USING INTEGER PRIMARY KEY (rowid=?)", "USE TEMP B-TREE FOR ORDER BY"). SQLite
/// estimates no row counts and never measures a run, so every plan is an estimate and weights come from the step kind.
/// </summary>
public static class SqlitePlanParser
{
    private static readonly Regex Step = new(
        @"^(?<op>SCAN|SEARCH|USE TEMP B-TREE|CORRELATED SCALAR SUBQUERY|SCALAR SUBQUERY|LIST SUBQUERY|CO-ROUTINE|MATERIALIZE|COMPOUND QUERY|LEFT-MOST SUBQUERY|UNION ALL|UNION USING TEMP B-TREE|MULTI-INDEX OR|INDEX|BLOOM FILTER ON)\b\s*(?<rest>.*)$",
        RegexOptions.CultureInvariant);

    private static readonly Regex TableAndUsing = new(@"^(?<table>(?:""(?:[^""]|"""")+""|\S+))(?:\s+(?<detail>.*))?$", RegexOptions.CultureInvariant);

    /// <summary>The result set is EXPLAIN QUERY PLAN output.</summary>
    public static bool IsPlan(QueryResultSet rs) =>
        rs.Columns.Count == 4 && rs.Columns[0] == "id" && rs.Columns[1] == "parent" && rs.Columns[3] == "detail";

    public static ExecutionPlan Parse(QueryResultSet rs, string? statement)
    {
        var root = new PlanNode { Operator = "QUERY PLAN", Object = null, Detail = null, HasRowEstimates = false };
        var byId = new Dictionary<long, PlanNode>();
        var raw = new StringBuilder("QUERY PLAN\n");
        var depth = new Dictionary<long, int> { [0] = 0 };
        var hasFilter = statement is not null && Regex.IsMatch(statement, @"\b(WHERE|JOIN)\b", RegexOptions.IgnoreCase);

        foreach (var row in rs.Rows)
        {
            var id = Convert.ToInt64(row[0], CultureInfo.InvariantCulture);
            var parentId = Convert.ToInt64(row[1], CultureInfo.InvariantCulture);
            var detail = Convert.ToString(row[3], CultureInfo.InvariantCulture)?.Trim() ?? "";
            var node = Node(detail);
            byId[id] = node;
            var parent = byId.GetValueOrDefault(parentId) ?? root;
            parent.Add(node);
            var d = depth.GetValueOrDefault(parentId) + 1;
            depth[id] = d;
            raw.Append(new string(' ', d * 3 - 1)).Append("|--").Append(detail).Append('\n');
        }

        var plan = new ExecutionPlan(SqlDialect.SqliteKey, root, isActual: false, statement, raw.ToString().TrimEnd());
        foreach (var node in plan.Nodes.Where(n => n.Operator == "SCAN" && n.Object is not null && n.Detail is null))
            node.Warnings.Add(new PlanWarning(PlanWarningKind.FullScan,
                $"Full scan of {node.Object}: every row is read" + (hasFilter ? ". An index on the filtered or joined columns may help." : "."),
                node));
        foreach (var node in plan.Nodes.Where(n => n.Operator == "USE TEMP B-TREE"))
            node.Warnings.Add(new PlanWarning(PlanWarningKind.Engine,
                $"Sorts into a temporary b-tree {node.Detail?.ToLowerInvariant()}: an index in that order would skip the sort.", node));
        return plan;
    }

    private static PlanNode Node(string detail)
    {
        var m = Step.Match(detail);
        if (!m.Success) return new PlanNode { Operator = detail, OwnWeight = 0.1, HasRowEstimates = false };
        var op = m.Groups["op"].Value;
        var rest = m.Groups["rest"].Value.Trim();
        if (op is "SCAN" or "SEARCH")
        {
            // "SCAN TABLE t" before SQLite 3.36, "SCAN t" after.
            if (rest.StartsWith("TABLE ", StringComparison.Ordinal)) rest = rest[6..];
            var t = TableAndUsing.Match(rest);
            var table = t.Success ? t.Groups["table"].Value : rest;
            var using_ = t.Success && t.Groups["detail"].Success ? t.Groups["detail"].Value : null;
            var covering = using_?.Contains("COVERING INDEX", StringComparison.Ordinal) == true;
            return new PlanNode
            {
                Operator = op,
                Object = table,
                Detail = string.IsNullOrEmpty(using_) ? null : using_,
                OwnWeight = op == "SCAN" ? (covering ? 0.6 : 1.0) : 0.25,
                HasRowEstimates = false
            };
        }
        return new PlanNode { Operator = op, Detail = rest.Length > 0 ? rest : null, OwnWeight = op == "USE TEMP B-TREE" ? 0.4 : 0.1, HasRowEstimates = false };
    }
}
