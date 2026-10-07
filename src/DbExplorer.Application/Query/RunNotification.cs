namespace DbExplorer.Application.Query;

/// <summary>
/// When a finished query deserves a notification: it ran long enough for the user to look elsewhere, and they are
/// not looking at its tab now.
/// </summary>
public static class RunNotification
{
    /// <summary>Runs shorter than this finish while the user is still watching.</summary>
    public static readonly TimeSpan Threshold = TimeSpan.FromSeconds(10);

    public static bool ShouldNotify(TimeSpan elapsed, bool windowActive, bool tabVisible) =>
        elapsed >= Threshold && !(windowActive && tabVisible);

    /// <summary>"Orders by day finished in 1:42" / "… failed after 0:12" / "… was cancelled after 2:05".</summary>
    public static string Message(string tabTitle, TimeSpan elapsed, RunOutcome outcome)
    {
        var clock = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"h\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture)
            : elapsed.ToString(@"m\:ss", System.Globalization.CultureInfo.InvariantCulture);
        return outcome switch
        {
            RunOutcome.Succeeded => $"{tabTitle} finished in {clock}",
            RunOutcome.Failed => $"{tabTitle} failed after {clock}",
            _ => $"{tabTitle} was cancelled after {clock}"
        };
    }
}

public enum RunOutcome { Succeeded, Failed, Cancelled }
