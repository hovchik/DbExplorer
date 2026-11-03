using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Updates;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>
/// Update check: once a day at start-up (Preferences › Updates) and on demand (command palette › Check for updates,
/// or Preferences' Check now) the GitHub releases are read; a newer one shows a bar under the toolbar with Download,
/// What's new, Skip this version and ✕. Nothing is downloaded or installed without the user clicking.
/// </summary>
public partial class MainWindowViewModel
{
    private UpdateChecker? _updates;
    private int _checking;

    /// <summary>The newer release the update bar offers, or null when the bar is hidden.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUpdateBar), nameof(UpdateBarTitle), nameof(UpdateBarDetail), nameof(UpdateDownloadText))]
    private ReleaseInfo? _availableUpdate;

    public bool ShowUpdateBar => AvailableUpdate is not null;

    public string UpdateBarTitle => AvailableUpdate is { } r
        ? $"DB Explorer {r.Version} is available" + (r.IsPreRelease ? " (pre-release)" : "")
        : "";

    public string UpdateBarDetail => AvailableUpdate is { } r
        ? $"You have {AppVersion}." + (r.PublishedAt is { } at ? $" Released {at.ToLocalTime():d MMM yyyy}." : "") +
          " Your connections, queries and settings are kept when you install it."
        : "";

    public string UpdateDownloadText => AvailableUpdate is { InstallerSize: { } size }
        ? $"Download ({size / 1024d / 1024d:0.#} MB)"
        : "Download";

    /// <summary>Called once at start-up; wires the checker that the bar, the palette and Preferences use.</summary>
    public void AttachUpdates(UpdateChecker updates) => _updates = updates;

    /// <summary>The automatic check at start-up: quiet unless a newer release is found, a few seconds after the
    /// window is up so it never competes with reconnecting.</summary>
    public async Task CheckForUpdatesAtStartAsync(TimeSpan delay)
    {
        if (_updates is null || !_updates.ShouldCheckAutomatically()) return;
        try
        {
            await Task.Delay(delay);
            if (!_updates.ShouldCheckAutomatically()) return;
            await RunUpdateCheckAsync(manual: false);
        }
        catch
        {
            // A failed automatic check stays silent; Check now shows the reason.
        }
    }

    /// <summary>Check now: always reads the releases (a skipped version is offered again) and says the outcome in the
    /// status bar.</summary>
    public async Task<UpdateCheckResult?> CheckForUpdatesNowAsync()
    {
        var result = await RunUpdateCheckAsync(manual: true);
        if (result is null) return null;
        if (result.Status == UpdateCheckStatus.Failed) ShowError(result.Describe());
        else StatusText = result.Describe();
        return result;
    }

    [RelayCommand]
    private Task CheckForUpdates() => CheckForUpdatesNowAsync();

    private async Task<UpdateCheckResult?> RunUpdateCheckAsync(bool manual)
    {
        if (_updates is null || Interlocked.Exchange(ref _checking, 1) == 1) return null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await Task.Run(() => _updates.CheckAsync(manual, cts.Token));
            if (result.Status == UpdateCheckStatus.Available) AvailableUpdate = result.Release;
            else if (result.Status == UpdateCheckStatus.UpToDate) AvailableUpdate = null;
            return result;
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    /// <summary>Opens the installer download (or the release page when the release has no installer) in the browser.</summary>
    [RelayCommand]
    private void DownloadUpdate()
    {
        if (AvailableUpdate is not { } r) return;
        OpenLink(r.InstallerUrl ?? r.PageUrl);
        StatusText = r.InstallerUrl is null
            ? $"Opened the release page of DB Explorer {r.Version}."
            : $"Downloading DbExplorer-Setup-{r.Version}.exe in your browser. Run it to update; it closes DB Explorer first if needed.";
    }

    [RelayCommand]
    private void OpenUpdateNotes()
    {
        if (AvailableUpdate is { } r) OpenLink(r.PageUrl);
    }

    /// <summary>The start-up check stays quiet about this version from now on; a newer one is still offered.</summary>
    [RelayCommand]
    private void SkipUpdate()
    {
        if (AvailableUpdate is not { } r) return;
        _settings.SetSkippedUpdateVersion(r.Version.Text);
        AvailableUpdate = null;
        StatusText = $"DB Explorer {r.Version} skipped. Check for updates in the command palette still shows it.";
    }

    /// <summary>Hides the bar until the next check.</summary>
    [RelayCommand]
    private void DismissUpdate() => AvailableUpdate = null;

    /// <summary>Opens a web page; overridable so tests don't start a browser.</summary>
    public Action<string> OpenLink { get; set; } = url =>
    {
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // No browser to open; the link is in the tooltip.
        }
    };
}
