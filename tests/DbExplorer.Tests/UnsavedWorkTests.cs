using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Tests;

/// <summary>The list shown before Disconnect, switching connections or closing the window loses work.</summary>
public class UnsavedWorkTests
{
    [Fact]
    public void Nothing_to_lose_gives_an_empty_list_so_no_prompt_is_shown()
    {
        var state = new UnsavedWorkState { QueryTabs = [new QueryTabWork("Query 1", false, false, false)] };

        foreach (var reason in Enum.GetValues<LeaveReason>())
            Assert.Empty(UnsavedWork.Describe(state, reason));
    }

    [Fact]
    public void Each_query_tab_lists_its_transaction_grid_edits_and_running_query()
    {
        var state = new UnsavedWorkState
        {
            QueryTabs =
            [
                new QueryTabWork("Orders fix", OpenTransaction: true, UncommittedEdits: true, Running: false),
                new QueryTabWork("Query 2", false, false, false),
                new QueryTabWork("Big report", false, false, Running: true)
            ]
        };

        Assert.Equal(
            [
                "Orders fix: the open transaction is rolled back",
                "Orders fix: uncommitted edits in the results are discarded",
                "Big report: the running query is stopped"
            ],
            UnsavedWork.Describe(state, LeaveReason.Disconnect));
    }

    [Fact]
    public void Other_tabs_and_data_windows_are_listed_with_counts()
    {
        var state = new UnsavedWorkState
        {
            SecurityPending = 3,
            QueryBuilderTables = 1,
            DataWindows = ["public.orders"]
        };

        Assert.Equal(
            [
                "Data window for public.orders: uncommitted edits are discarded",
                "Security: 3 unapplied changes are discarded",
                "Query builder: the canvas with 1 table is cleared"
            ],
            UnsavedWork.Describe(state, LeaveReason.Quit));
        Assert.Contains("Security: 1 unapplied change is discarded", UnsavedWork.Describe(state with { SecurityPending = 1 }, LeaveReason.Quit));
    }

    [Fact]
    public void Table_design_survives_a_plain_disconnect_but_not_a_switch_or_quit()
    {
        var state = new UnsavedWorkState { TableDesign = "orders" };

        Assert.Empty(UnsavedWork.Describe(state, LeaveReason.Disconnect));
        Assert.Equal(["Table designer: unsaved changes to orders are discarded"], UnsavedWork.Describe(state, LeaveReason.Switch));
        Assert.Equal(["Table designer: unsaved changes to orders are discarded"], UnsavedWork.Describe(state, LeaveReason.Quit));
    }

    [Theory]
    [InlineData(LeaveReason.Disconnect, "Disconnecting from Shop", "Disconnect anyway")]
    [InlineData(LeaveReason.Switch, "Switching away from Shop", "Switch anyway")]
    [InlineData(LeaveReason.Quit, "Closing DB Explorer loses work on Shop", "Close anyway")]
    public void Message_names_the_action_and_lists_every_line(LeaveReason reason, string lead, string button)
    {
        var message = UnsavedWork.Message(reason, "Shop", ["a", "b"]);

        Assert.StartsWith(lead, message);
        Assert.EndsWith("•  a\n•  b", message);
        Assert.Equal(button, UnsavedWork.ConfirmText(reason));
    }
}
