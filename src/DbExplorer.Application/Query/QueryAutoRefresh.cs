namespace DbExplorer.Application.Query;

/// <summary>An interval the Query tab can re-run its last query at.</summary>
public sealed record AutoRefreshInterval(string Label, TimeSpan Period)
{
    public override string ToString() => Label;
}

/// <summary>Rules for re-running a query tab's last query on a timer.</summary>
public static class QueryAutoRefresh
{
    public static IReadOnlyList<AutoRefreshInterval> Intervals { get; } =
    [
        new("5 s", TimeSpan.FromSeconds(5)),
        new("10 s", TimeSpan.FromSeconds(10)),
        new("30 s", TimeSpan.FromSeconds(30)),
        new("1 min", TimeSpan.FromMinutes(1)),
        new("5 min", TimeSpan.FromMinutes(5)),
    ];

    public static AutoRefreshInterval DefaultInterval => Intervals[1];

    /// <summary>
    /// Null when <paramref name="sql"/> can be re-run unattended; otherwise why not. Only scripts that read are repeated:
    /// anything that could change data or schema is confirmed by hand on every run.
    /// </summary>
    public static string? WhyNotRepeatable(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return "Run a query first, then turn on auto refresh to repeat it.";
        if (new QueryExecutionService().IsPotentiallyDestructive(sql) || SqlEditorAnalysis.FindUnsafeStatements(sql).Count > 0)
            return "Auto refresh only repeats queries that read data; the last run may change data or schema.";
        return null;
    }
}
