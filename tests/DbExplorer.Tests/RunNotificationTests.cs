using DbExplorer.Application.Query;

namespace DbExplorer.Tests;

public class RunNotificationTests
{
    [Theory]
    [InlineData(30, false, true, true)]   // another app is in front
    [InlineData(30, true, false, true)]   // another tab is in front
    [InlineData(30, true, true, false)]   // the user watched it finish
    [InlineData(5, false, false, false)]  // too quick to have looked away
    public void Only_long_runs_the_user_did_not_watch_notify(int seconds, bool windowActive, bool tabVisible, bool expected) =>
        Assert.Equal(expected, RunNotification.ShouldNotify(TimeSpan.FromSeconds(seconds), windowActive, tabVisible));

    [Fact]
    public void Message_names_the_tab_the_outcome_and_the_time()
    {
        Assert.Equal("Orders by day finished in 1:42", RunNotification.Message("Orders by day", TimeSpan.FromSeconds(102), RunOutcome.Succeeded));
        Assert.Equal("Load failed after 0:12", RunNotification.Message("Load", TimeSpan.FromSeconds(12), RunOutcome.Failed));
        Assert.Equal("Rebuild was cancelled after 1:00:05", RunNotification.Message("Rebuild", TimeSpan.FromSeconds(3605), RunOutcome.Cancelled));
    }
}
