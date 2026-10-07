using System.Text.Json;
using System.Xml;
using DbExplorer.Application.Copy;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Query.Plans;

/// <summary>Asks the engine for plans it can draw (PostgreSQL JSON, SQL Server showplan XML) and reads them.</summary>
public static class PlanReader
{
    /// <summary>The statements of <paramref name="sql"/> as PostgreSQL will explain them, one plan each.</summary>
    public static IReadOnlyList<string> Statements(string sql) =>
        SqlScriptTools.SplitStatements(sql)
            .Select(r => r.Of(sql).TrimEnd().TrimEnd(';').Trim())
            .Where(s => s.Length > 0)
            .ToList();

    /// <summary>
    /// The script that returns machine-readable plans of <paramref name="sql"/>. Estimated plans do not run the
    /// statements. With <paramref name="analyze"/> they do run (to measure them), inside a transaction that is rolled back.
    /// </summary>
    public static string BuildScript(string sql, string providerKey, bool analyze)
    {
        if (providerKey == SqlDialect.SqlServerKey)
        {
            return analyze
                ? $"SET STATISTICS XML ON;\nGO\nBEGIN TRANSACTION;\nGO\n{sql}\nGO\nIF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;\nGO\nSET STATISTICS XML OFF;"
                : $"SET SHOWPLAN_XML ON;\nGO\n{sql}\nGO\nSET SHOWPLAN_XML OFF;";
        }

        // VERBOSE adds the schema of each table (and the columns each node outputs).
        var options = analyze ? "ANALYZE, BUFFERS, VERBOSE, FORMAT JSON" : "VERBOSE, FORMAT JSON";
        var body = string.Join("\n", Statements(sql).Select(s => $"EXPLAIN ({options})\n{s};"));
        return analyze ? $"BEGIN;\n{body}\nROLLBACK;" : body;
    }

    /// <summary>
    /// The plans in a result of <see cref="BuildScript"/>, in statement order. Result sets that are not plans (the
    /// statements' own results, which SQL Server's actual plans interleave) are skipped. Throws when a result set looks
    /// like a plan but cannot be read.
    /// </summary>
    public static IReadOnlyList<ExecutionPlan> Read(IReadOnlyList<QueryResultSet> resultSets, string providerKey, string sql)
    {
        var plans = new List<ExecutionPlan>();
        var statements = providerKey == SqlDialect.SqlServerKey ? [] : Statements(sql);
        var index = 0;
        foreach (var rs in resultSets)
        {
            if (rs.Columns.Count != 1 || rs.Rows.Count == 0) continue;
            var text = string.Concat(rs.Rows.Select(r => r.FirstOrDefault()?.ToString())).Trim();
            if (providerKey == SqlDialect.SqlServerKey)
            {
                if (text.StartsWith("<ShowPlanXML", StringComparison.Ordinal) || (text.StartsWith("<?xml", StringComparison.Ordinal) && text.Contains("<ShowPlanXML", StringComparison.Ordinal))) plans.AddRange(SqlServerPlanParser.Parse(text));
            }
            else if (text.StartsWith('[') && text.Contains("\"Plan\"", StringComparison.Ordinal))
            {
                plans.AddRange(PostgresPlanParser.Parse(text, index < statements.Count ? statements[index] : null));
                index++;
            }
        }
        return plans;
    }

    /// <summary>True when <paramref name="error"/> means the text was not a plan this reader understands.</summary>
    public static bool IsUnreadable(Exception error) => error is JsonException or XmlException or FormatException or InvalidOperationException;
}
