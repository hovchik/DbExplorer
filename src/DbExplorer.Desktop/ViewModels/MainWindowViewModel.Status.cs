using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Connections;
using DbExplorer.Desktop.Services;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>One line the status bar showed, kept for the status history.</summary>
public sealed record StatusEntry(DateTime Time, string Text, bool IsError)
{
    public string TimeText => Time.ToString("HH:mm:ss");

    /// <summary>"14:02:11  Connection failed: …", for Copy.</summary>
    public override string ToString() => $"{Time:yyyy-MM-dd HH:mm:ss}  {(IsError ? "ERROR  " : "")}{Text}";
}

/// <summary>The status bar's last messages, newest first, at most <see cref="Capacity"/>.</summary>
public sealed class StatusHistory(int capacity = StatusHistory.DefaultCapacity) : ObservableCollection<StatusEntry>
{
    public const int DefaultCapacity = 50;

    public int Capacity { get; } = capacity;

    /// <summary>Adds <paramref name="entry"/> at the top; the same line twice in a row is kept once, with the newer time.</summary>
    public void Record(StatusEntry entry)
    {
        if (Count > 0 && this[0].Text == entry.Text && this[0].IsError == entry.IsError) this[0] = entry;
        else Insert(0, entry);
        while (Count > Capacity) RemoveAt(Count - 1);
    }

    /// <summary>Oldest first, one line each, for the clipboard.</summary>
    public string ToText() => string.Join(Environment.NewLine, this.Reverse().Select(e => e.ToString()));
}

/// <summary>
/// The status bar: every message is kept (newest first, up to 50) so a line that was
/// replaced a second later can still be read; errors stay marked until the history has been opened; a failed connect
/// is shown as a card with what went wrong, what to do, and Edit / Retry.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>Newest first.</summary>
    public StatusHistory StatusHistory { get; } = new();

    /// <summary>The line in the status bar is an error (drawn in the error colour).</summary>
    [ObservableProperty] private bool _statusIsError;

    /// <summary>An error was shown since the history was last opened.</summary>
    [ObservableProperty] private bool _hasUnseenError;

    /// <summary>The last connect that failed, until it is dismissed, retried or another connect succeeds.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowConnectionFailure))]
    private ConnectionFailure? _connectionFailure;

    /// <summary>The connection the failure is about (Edit opens it).</summary>
    private Guid? _failedProfileId;

    public bool ShowConnectionFailure => ConnectionFailure is not null;

    public bool HasStatusHistory => StatusHistory.Count > 0;

    /// <summary>Set while a progress line is shown: those replace each other quickly and are not worth keeping.</summary>
    private bool _transientStatus;

    /// <summary>Set by <see cref="ShowError"/> for the one change it makes.</summary>
    private bool _errorStatus;

    partial void OnStatusTextChanged(string value)
    {
        var isError = _errorStatus;
        StatusIsError = isError;
        if (isError) HasUnseenError = true;
        if (_transientStatus || string.IsNullOrWhiteSpace(value)) return;
        AddStatusEntry(new StatusEntry(DateTime.Now, value, isError));
    }

    private void AddStatusEntry(StatusEntry entry)
    {
        StatusHistory.Record(entry);
        OnPropertyChanged(nameof(HasStatusHistory));
    }

    /// <summary>Shows <paramref name="text"/> as an error: red, kept in the history, marked until seen.</summary>
    public void ShowError(string text)
    {
        _errorStatus = true;
        try
        {
            // The same text as before still has to count as a new error.
            if (StatusText == text) OnStatusTextChanged(text);
            else StatusText = text;
        }
        finally
        {
            _errorStatus = false;
        }
    }

    /// <summary>A progress line: shown, but not kept in the history.</summary>
    private void ShowProgress(string text)
    {
        _transientStatus = true;
        try { StatusText = text; }
        finally { _transientStatus = false; }
    }

    /// <summary>The status history was opened: the error mark goes away.</summary>
    [RelayCommand]
    private void MarkStatusSeen() => HasUnseenError = false;

    [RelayCommand]
    private void ClearStatusHistory()
    {
        StatusHistory.Clear();
        HasUnseenError = false;
        OnPropertyChanged(nameof(HasStatusHistory));
    }

    /// <summary>Puts text on the clipboard; set by the window (the view model has no clipboard of its own).</summary>
    public Func<string, Task>? SetClipboardText { get; set; }

    [RelayCommand]
    private async Task CopyStatusHistory()
    {
        if (SetClipboardText is { } set && StatusHistory.Count > 0) await set(StatusHistory.ToText());
    }

    [RelayCommand]
    private async Task CopyStatusEntry(StatusEntry? entry)
    {
        if (SetClipboardText is { } set && entry is not null) await set(entry.ToString());
    }

    public static string LogsFolder => Path.GetDirectoryName(ErrorLog.FilePath)!;

    [RelayCommand]
    private void OpenLogsFolder()
    {
        try
        {
            Directory.CreateDirectory(LogsFolder);
            Process.Start(new ProcessStartInfo(LogsFolder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowError($"Could not open {LogsFolder}: {ex.Message}");
        }
    }

    private void ReportConnectionFailure(Exception error, Core.Connections.ConnectionProfile profile)
    {
        var failure = ConnectionFailure.Classify(error, profile);
        _failedProfileId = profile.Id;
        ConnectionFailure = failure;
        ShowError($"Could not connect to {profile.DisplayName}: {failure.Summary}");
    }

    [RelayCommand]
    private void DismissConnectionFailure() => ConnectionFailure = null;

    /// <summary>Retry from the failure card: the same connection again.</summary>
    [RelayCommand]
    private async Task RetryConnectAsync()
    {
        if (SelectFailedProfile() is not { } profile) return;
        ConnectionFailure = null;
        await ConnectProfileAsync(profile);
    }

    /// <summary>Edit from the failure card: open the connection's settings, then try again if they were saved.</summary>
    [RelayCommand]
    private async Task EditFailedConnectionAsync()
    {
        if (SelectFailedProfile() is not { } profile) return;
        await EditProfileAsync(profile);
        // Saved: the list holds a new object for it, so connect again straight away. Cancelled: keep the card so
        // Retry is still there.
        if (Profiles.FirstOrDefault(p => p.Id == profile.Id) is { } edited && !ReferenceEquals(edited, profile) && !OpenConnections.IsOpen(edited.Id))
        {
            SelectedProfile = edited;
            ConnectionFailure = null;
            await ConnectProfileAsync(edited);
        }
    }

    /// <summary>The connection the card is about, selected; null (and the card dismissed) when it no longer exists.</summary>
    private Core.Connections.ConnectionProfile? SelectFailedProfile()
    {
        if (_failedProfileId is { } id && Profiles.FirstOrDefault(p => p.Id == id) is { } profile)
        {
            SelectedProfile = profile;
            return profile;
        }
        // The connection was deleted (or a Team pull dropped it) after it failed: the card no longer describes
        // anything Edit or Retry could act on, so it goes away instead of acting on whatever is selected now.
        ConnectionFailure = null;
        return null;
    }
}
