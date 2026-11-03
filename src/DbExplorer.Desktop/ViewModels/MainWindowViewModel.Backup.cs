using CommunityToolkit.Mvvm.Input;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>Back up and restore databases of the connection (navigator database rows, the palette).</summary>
public partial class MainWindowViewModel
{
    [RelayCommand(CanExecute = nameof(CanUseSession))]
    private void BackupDatabase() => OpenBackupRestore(null, restore: false);

    [RelayCommand(CanExecute = nameof(CanUseSession))]
    private void RestoreDatabase() => OpenBackupRestore(null, restore: true);

    /// <summary>Opens the Back up and restore window on <paramref name="database"/> (or the connection's own), on its
    /// Restore tab when <paramref name="restore"/> is set.</summary>
    public void OpenBackupRestore(string? database, bool restore)
    {
        if (Session is not { } session) return;
        // A restore aims at an existing database only when the user picked one (a navigator row); otherwise it starts
        // on "a new database", so nothing is overwritten by default.
        var restoreTarget = restore ? database : null;
        database ??= string.IsNullOrEmpty(session.Profile.Database) ? Query.SelectedDocument?.CurrentDatabase : session.Profile.Database;
        _dialogs.ShowBackupRestore(new BackupRestoreViewModel(session, _dialogs, _settings, database, restoreTarget, restore, target =>
        {
            if (!ReferenceEquals(session, Session)) return;
            // A new database shows up in every picker; a restored one has a new catalog.
            session.InvalidateDatabases();
            StatusText = $"Restored {target}. Refreshing the catalog…";
            if (RefreshMetadataCommand.CanExecute(null)) _ = RefreshMetadataCommand.ExecuteAsync(null);
        }));
    }
}
