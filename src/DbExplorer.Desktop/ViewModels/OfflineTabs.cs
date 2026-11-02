namespace DbExplorer.Desktop.ViewModels;

/// <summary>
/// Which main tabs work with nothing connected, and where the window goes when the connection comes and goes:
/// with no connection it shows the Query tab instead of a disabled one, and the first connect goes back to the tab
/// it left, unless the user started working in the Query tab meanwhile.
/// </summary>
public static class OfflineTabs
{
    /// <summary>Query (write, open and save scripts), Comparer (picks its own connections) and ER model (works on a
    /// model file) are usable offline; every other tab shows a server's catalog or state.</summary>
    public static bool NeedsConnection(AppTab tab) => tab is not (AppTab.Query or AppTab.Comparer or AppTab.ErModel);

    /// <summary>The tab to show once nothing is connected; null to stay on <paramref name="current"/>.</summary>
    public static AppTab? TabWhenOffline(AppTab current) => NeedsConnection(current) ? AppTab.Query : null;

    /// <summary>The tab to return to on connect: the one left for the Query tab, as long as the user is still on the
    /// Query tab and has not changed its scripts (then they went offline to write something they now want to run).</summary>
    public static AppTab? TabOnConnect(AppTab? left, AppTab current, bool queryChanged) =>
        left is { } tab && current == AppTab.Query && !queryChanged ? tab : null;

    /// <summary>A fingerprint of the Query tabs' scripts, to tell whether they changed while offline.</summary>
    public static string Fingerprint(IEnumerable<string> scripts) => string.Join("\u0001", scripts);
}
