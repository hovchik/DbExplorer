using System.Text.Json;
using System.Xml;
using DbExplorer.Application.Copy;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Query.Plans;

/// <summary>Asks the engine for plans it can draw (PostgreSQL JSON, SQL Server showplan XML, MySQL trees, MariaDB JSON)
/// and reads them.</summary>
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
    /// <param name="serverVersion">The session's server version: MySQL and MariaDB explain differently.</param>
    public static string BuildScript(string sql, string providerKey, bool analyze, string? serverVersion = null)
    {
        if (providerKey == SqlDialect.MySqlKey)
        {
            var mariaDb = IsMariaDb(serverVersion);
            var explain = (mariaDb, analyze) switch
            {
                (true, true) => "ANALYZE FORMAT=JSON",
                (true, false) => "EXPLAIN FORMAT=JSON",
                (false, true) => "EXPLAIN ANALYZE",
                _ => "EXPLAIN FORMAT=TREE"
            };
            var statements = string.Join("\n", Statements(sql).Select(s => $"{explain}\n{s};"));
            // DDL commits on its own in MySQL; Explain Analyze warns before running anything that writes.
            return analyze ? $"START TRANSACTION;\n{statements}\nROLLBACK;" : statements;
        }

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
        if (providerKey == SqlDialect.MySqlKey) return ReadMySql(resultSets, sql);

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

    public static bool IsMariaDb(string? serverVersion) => serverVersion?.Contains("MariaDB", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>One result set per explained statement, each a single text cell (a tree or JSON).</summary>
    private static IReadOnlyList<ExecutionPlan> ReadMySql(IReadOnlyList<QueryResultSet> resultSets, string sql)
    {
        var plans = new List<ExecutionPlan>();
        var statements = Statements(sql);
        var index = 0;
        foreach (var rs in resultSets)
        {
            if (rs.Columns.Count != 1 || rs.Rows.Count == 0) continue;
            var text = string.Concat(rs.Rows.Select(r => r.FirstOrDefault()?.ToString())).Trim();
            var statement = index < statements.Count ? statements[index] : null;
            if (MySqlPlanParser.IsTree(text)) plans.Add(MySqlPlanParser.ParseTree(text, statement));
            else if (MySqlPlanParser.IsMariaDbJson(text)) plans.Add(MySqlPlanParser.ParseMariaDbJson(text, statement));
            else continue;
            index++;
        }
        return plans;
    }

    /// <summary>True when <paramref name="error"/> means the text was not a plan this reader understands.</summary>
    public static bool IsUnreadable(Exception error) => error is JsonException or XmlException or FormatException or InvalidOperationException;
}
