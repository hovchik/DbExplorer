using DbExplorer.Application.Diagnostics;
using DbExplorer.Core.Models;

namespace DbExplorer.Tests;

public class BlockingChainBuilderTests
{
    private static DbLock Grant(int session, string sql = "") =>
        new() { SessionId = session, Status = "GRANT", LockMode = "X", ResourceType = "KEY", ObjectName = "dbo.T", SqlText = sql, LoginName = "u" + session };

    private static DbLock Wait(int session, int blocker, int ms = 100) =>
        new() { SessionId = session, Status = "WAIT", LockMode = "S", ResourceType = "KEY", ObjectName = "dbo.T", BlockedBy = blocker, WaitMs = ms, WaitType = "LCK_M_S" };

    [Fact]
    public void No_blocking_yields_no_trees() =>
        Assert.Empty(BlockingChainBuilder.Build([Grant(1), Grant(2)]));

    [Fact]
    public void Builds_multi_level_chain_from_head_blocker()
    {
        // 10 blocks 20 and 30; 30 blocks 40.
        var locks = new[] { Grant(10, "UPDATE dbo.T ..."), Grant(10), Wait(20, 10), Wait(30, 10), Grant(30), Wait(40, 30) };

        var root = Assert.Single(BlockingChainBuilder.Build(locks));
        Assert.Equal(10, root.SessionId);
        Assert.Equal(3, root.TotalBlocked);
        Assert.Equal("UPDATE dbo.T ...", root.SqlText);
        Assert.Equal([20, 30], root.Children.Select(c => c.SessionId));
        var s30 = root.Children[1];
        Assert.Equal(40, Assert.Single(s30.Children).SessionId);
        Assert.Equal("waiting on S KEY dbo.T (LCK_M_S) for 100 ms", s30.Detail);
    }

    [Fact]
    public void Blocker_without_lock_rows_becomes_unknown_head()
    {
        var root = Assert.Single(BlockingChainBuilder.Build([Wait(5, 99)]));
        Assert.Equal(99, root.SessionId);
        Assert.True(root.IsUnknown);
        Assert.Equal(5, Assert.Single(root.Children).SessionId);
    }

    [Fact]
    public void Cycles_terminate_and_are_flagged()
    {
        // 1 → 2 → 3 → 1, plus 4 waiting on 2.
        var roots = BlockingChainBuilder.Build([Wait(1, 3), Wait(2, 1), Wait(3, 2), Wait(4, 2)]);

        var root = Assert.Single(roots);
        Assert.Equal(1, root.SessionId);
        Assert.True(root.IsInCycle);
        Assert.Equal(3, root.TotalBlocked);
        Assert.Contains("CYCLE", root.Title);
        var s2 = Assert.Single(root.Children);
        Assert.Equal([3, 4], s2.Children.Select(c => c.SessionId).Order());
        Assert.False(s2.Children.Single(c => c.SessionId == 4).IsInCycle);
    }

    [Fact]
    public void Largest_chain_is_listed_first()
    {
        var roots = BlockingChainBuilder.Build([Wait(2, 1), Wait(11, 10), Wait(12, 10)]);
        Assert.Equal([10, 1], roots.Select(r => r.SessionId));
    }
}
