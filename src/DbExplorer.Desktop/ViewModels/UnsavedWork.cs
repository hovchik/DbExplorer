namespace DbExplorer.Desktop.ViewModels;

/// <summary>Why the current connection is about to go away.</summary>
public enum LeaveReason
{
    /// <summary>The Disconnect button, the navigator's Disconnect or the command palette.</summary>
    Disconnect,

    /// <summary>The navigator's "Connect" on another connection while one is open.</summary>
    Switch,

    /// <summary>The window is closing.</summary>
    Quit
}

/// <summary>What one Query tab holds that only lives on the connection.</summary>
public sealed record QueryTabWork(string Title, bool OpenTransaction, bool UncommittedEdits, bool Running);

/// <summary>Everything the app would drop when the connection goes away, gathered from the tabs and windows.</summary>
public sealed record UnsavedWorkState
{
    public IReadOnlyList<QueryTabWork> QueryTabs { get; init; } = [];

    /// <summary>Security tab changes queued but not applied.</summary>
    public int SecurityPending { get; init; }

    /// <summary>What the Table designer's unexecuted edits are for ("orders", "a new table"); null when it has none.</summary>
    public string? TableDesign { get; init; }

    /// <summary>Tables on the Query builder canvas (the builder starts empty after any reconnect).</summary>
    public int QueryBuilderTables { get; init; }

    /// <summary>The tables of the open Get data windows whose rows have uncommitted edits ("public.orders").</summary>
    public IReadOnlyList<string> DataWindows { get; init; } = [];
}

/// <summary>
/// The list behind "Disconnecting will lose…": one line per thing that is rolled back, discarded or stopped. Query
/// text is not listed: tabs are saved on quit and stay open across a disconnect. Nor is the ER model, which autosaves.
/// </summary>
public static class UnsavedWork
{
    public static IReadOnlyList<string> Describe(UnsavedWorkState state, LeaveReason reason)
    {
        var lines = new List<string>();
        foreach (var tab in state.QueryTabs)
        {
            if (tab.OpenTransaction) lines.Add($"{tab.Title}: the open transaction is rolled back");
            if (tab.UncommittedEdits) lines.Add($"{tab.Title}: uncommitted edits in the results are discarded");
            if (tab.Running) lines.Add($"{tab.Title}: the running query is stopped");
        }
        foreach (var window in state.DataWindows)
            lines.Add($"Data window for {window}: uncommitted edits are discarded");
        if (state.SecurityPending > 0)
            lines.Add($"Security: {Count(state.SecurityPending, "unapplied change")} {(state.SecurityPending == 1 ? "is" : "are")} discarded");
        // A plain disconnect keeps the design for a reconnect to the same connection; switching or quitting does not.
        if (state.TableDesign is { } design && reason != LeaveReason.Disconnect)
            lines.Add($"Table designer: unsaved changes to {design} are discarded");
        if (state.QueryBuilderTables > 0)
            lines.Add($"Query builder: the canvas with {Count(state.QueryBuilderTables, "table")} is cleared");
        return lines;
    }

    public static string Message(LeaveReason reason, string connection, IReadOnlyList<string> lines)
    {
        var lead = reason switch
        {
            LeaveReason.Disconnect => $"Disconnecting from {connection} loses work that is not saved:",
            LeaveReason.Switch => $"Switching away from {connection} loses work that is not saved:",
            _ => $"Closing DB Explorer loses work on {connection} that is not saved:"
        };
        return lead + "\n\n" + string.Join("\n", lines.Select(l => "•  " + l));
    }

    public static string ConfirmText(LeaveReason reason) => reason switch
    {
        LeaveReason.Disconnect => "Disconnect anyway",
        LeaveReason.Switch => "Switch anyway",
        _ => "Close anyway"
    };

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}
