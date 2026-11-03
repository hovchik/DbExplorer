using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Tests;

public sealed class WelcomeRulesTests
{
    [Fact]
    public void Shows_only_before_anything_is_started()
    {
        Assert.True(WelcomeRules.Show(connected: false, busy: false, dismissed: false, [""]));
        Assert.True(WelcomeRules.Show(false, false, false, []));
        Assert.False(WelcomeRules.Show(true, false, false, [""]));
        Assert.False(WelcomeRules.Show(false, true, false, [""]));
        Assert.False(WelcomeRules.Show(false, false, true, [""]));
        Assert.False(WelcomeRules.Show(false, false, false, ["select 1"]));
        Assert.False(WelcomeRules.Show(false, false, false, ["", ""]));
    }
}
