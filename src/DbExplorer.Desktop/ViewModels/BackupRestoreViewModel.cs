using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application;
using DbExplorer.Application.Connections;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Backup;
using DbExplorer.Core.Connections;
using DbExplorer.Desktop.Services;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>
/// The Back up and restore window for one connection. Backups write one file of a database: SQL Server's BACKUP
/// DATABASE on the server's disk (copy-only, checksummed and verified by default), pg_dump's custom archive or a plain
/// SQL script on this computer, or a mysqldump script. Restores read such a file into a new database or over an existing
/// one after a confirmation that shows exactly what runs (typing the database name, or PRODUCTION on production
/// connections); read-only connections can back up but not restore.
/// </summary>
public sealed partial class BackupRestoreViewModel : ViewModelBase
{
    private const int MaxLogLines = 400;

    private readonly DatabaseSession _session;
    private readonly IDialogService _dialogs;
    private readonly AppSettingsService _settings;
    private readonly IDatabaseBackupProvider? _engine;
    private readonly Action<string> _restored;
    private readonly List<string> _logLines = [];
    private CancellationTokenSource? _cts;
    private string? _lastBackupFile;

    public BackupRestoreViewModel(DatabaseSession session, IDialogService dialogs, AppSettingsService settings,
        string? database, string? restoreTarget, bool restore, Action<string> restored)
    {
        _session = session;
        _dialogs = dialogs;
        _settings = settings;
        _restored = restored;
        _engine = session.Provider as IDatabaseBackupProvider;
        _backupDatabase = database;
        _restoreTargetDatabase = restoreTarget;
        _restoreToNew = restoreTarget is null;
        _selectedTabIndex = restore ? 1 : 0;
        Title = "Back up and restore";
        Subtitle = $"{session.Profile.DisplayName} · {session.Factory.DisplayName}";
    }

    public string Title { get; }
    public string Subtitle { get; }
    public bool IsProduction => _session.Profile.IsProduction;
    public bool IsReadOnly => _session.Profile.ReadOnly;
    public string ProductionBanner => $"PRODUCTION · {_session.Profile.DisplayName} · a restore replaces live data";

    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty] private IReadOnlyList<string> _databases = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAvailable), nameof(ToolText), nameof(ProblemText), nameof(IsServerFile), nameof(FileHint),
        nameof(ShowFormat), nameof(ShowSchemaOnly), nameof(ShowCopyOnly), nameof(ShowCompress), nameof(ShowVerify),
        nameof(ShowSkipOwnership), nameof(ShowCloseConnections), nameof(RestoreOverExisting), nameof(CanPickToolFolder),
        nameof(CanBrowseFiles))]
    [NotifyCanExecuteChangedFor(nameof(StartBackupCommand), nameof(StartRestoreCommand), nameof(ReadFileCommand))]
    private BackupSupport? _support;

    public bool IsAvailable => Support?.IsAvailable == true;
    public string ToolText => Support is null ? "Checking what this connection can use…" : Support.Tool.Length > 0 ? "Uses " + Support.Tool : "";
    public string ProblemText => Support?.Problem is { } p ? p + (Support.Setup is { } s ? " " + s : "") : "";
    public bool IsServerFile => Support?.Location == BackupFileLocation.Server;

    /// <summary>The dump tools are on this computer, so the user can point at their folder.</summary>
    public bool CanPickToolFolder => Support is { Location: BackupFileLocation.ThisComputer };

    /// <summary>A file dialog only makes sense for files on this computer, or a server on this computer.</summary>
    public bool CanBrowseFiles => !IsServerFile || ConnectionSecurity.IsLocalHost(_session.Profile.Host);

    public string FileHint => IsServerFile
        ? "This path is on the database server: the SQL Server service writes and reads the file there, so its service account needs access to the folder."
        : "The file is on this computer.";

    public bool ShowFormat => Support?.Formats.Count > 1;
    public bool ShowSchemaOnly => Support?.SupportsSchemaOnly == true;
    public bool ShowCopyOnly => Support?.SupportsCopyOnly == true;
    public bool ShowCompress => Support?.SupportsCompression == true;
    public bool ShowVerify => Support?.SupportsVerify == true;
    public bool ShowSkipOwnership => Support?.SupportsSkipOwnership == true;
    public bool ShowCloseConnections => Support?.SupportsCloseConnections == true;
    public string RestoreOverExisting => Support?.RestoreOverExisting ?? "";

    // ----- Back up -----

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartBackupCommand))]
    private string? _backupDatabase;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartBackupCommand))]
    private string _backupPath = "";

    /// <summary>pg_dump's custom archive (the default) rather than a plain SQL script.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlainFormat))]
    private bool _isCustomFormat = true;

    public bool IsPlainFormat
    {
        get => !IsCustomFormat;
        set => IsCustomFormat = !value;
    }

    [ObservableProperty] private bool _schemaOnly;
    [ObservableProperty] private bool _copyOnly = true;
    [ObservableProperty] private bool _compress = true;
    [ObservableProperty] private bool _verify = true;

    private BackupFormat Format => Support?.Formats.Count > 1 && IsCustomFormat ? BackupFormat.Custom : Support?.Formats.FirstOrDefault() ?? BackupFormat.PlainSql;

    partial void OnBackupDatabaseChanged(string? value) => SuggestBackupPath();
    partial void OnIsCustomFormatChanged(bool value) => SuggestBackupPath();

    /// <summary>"shop_20261103_0715.dump" in the folder of the current path, the last backup folder, or the
    /// engine's default (SQL Server: the instance's backup folder).</summary>
    private void SuggestBackupPath()
    {
        if (Support is not { } support || string.IsNullOrEmpty(BackupDatabase)) return;
        var separator = support.Location == BackupFileLocation.Server ? support.PathSeparator : Path.DirectorySeparatorChar;
        string? folder = null;
        if (BackupPath.Length > 0)
        {
            var cut = BackupPath.LastIndexOfAny(['\\', '/']);
            if (cut >= 0) folder = BackupPath[..(cut + 1)];
        }
        folder ??= support.Location == BackupFileLocation.ThisComputer ? _settings.LastBackupFolder ?? support.DefaultFolder : support.DefaultFolder;
        if (!string.IsNullOrEmpty(folder) && !folder.EndsWith('\\') && !folder.EndsWith('/')) folder += separator;
        BackupPath = (folder ?? "") + FileName(BackupDatabase, DateTime.Now, support.ExtensionFor(Format));
    }

    public static string FileName(string database, DateTime when, string extension)
    {
        var safe = string.Concat(database.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '\\' or '/' or ':' or ' ' ? '_' : c));
        return $"{safe}_{when.ToString("yyyyMMdd_HHmm", CultureInfo.InvariantCulture)}.{extension}";
    }

    // ----- Restore -----

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartRestoreCommand), nameof(ReadFileCommand))]
    private string _restorePath = "";

    [ObservableProperty] private BackupFileInfo? _fileInfo;
    [ObservableProperty] private string _fileInfoText = "Pick a backup file to see what it holds.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RestoreToExisting))]
    [NotifyCanExecuteChangedFor(nameof(StartRestoreCommand))]
    private bool _restoreToNew;

    public bool RestoreToExisting
    {
        get => !RestoreToNew;
        set => RestoreToNew = !value;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartRestoreCommand))]
    private string _newDatabaseName = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartRestoreCommand))]
    private string? _restoreTargetDatabase;

    [ObservableProperty] private bool _skipOwnership = true;
    [ObservableProperty] private bool _closeConnections = true;

    public string RestoreTarget => RestoreToNew ? NewDatabaseName.Trim() : RestoreTargetDatabase ?? "";

    // ----- Running -----

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartBackupCommand), nameof(StartRestoreCommand), nameof(ReadFileCommand),
        nameof(BrowseBackupPathCommand), nameof(BrowseRestorePathCommand), nameof(PickToolFolderCommand))]
    private bool _isRunning;

    [ObservableProperty] private double _percent;
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private string _log = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _canShowFile;

    public async Task LoadAsync()
    {
        if (_engine is null)
        {
            Support = new BackupSupport
            {
                Problem = $"Backups are not available for {_session.Factory.DisplayName} connections.",
                Setup = _session.Factory.IsFileBased
                    ? "The whole database is the one file: copy it while nothing writes to it, or run VACUUM INTO 'copy.db' in a Query tab for a consistent copy while it is in use."
                    : null
            };
            return;
        }
        try
        {
            var databases = await _session.GetServerDatabasesAsync();
            Databases = databases;
            BackupDatabase ??= string.IsNullOrEmpty(_session.Profile.Database) ? databases.FirstOrDefault() : _session.Profile.Database;
            RestoreTargetDatabase ??= BackupDatabase;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("backup: list databases", ex);
            Databases = _session.Snapshot.Databases;
        }
        await LoadSupportAsync();
    }

    private string? ToolFolder => _settings.BackupToolFolder(_session.Profile.ProviderKey);

    private async Task LoadSupportAsync()
    {
        Support = null;
        try
        {
            Support = await Task.Run(() => _engine!.GetBackupSupportAsync(ToolFolder, _session.Lifetime));
        }
        catch (Exception ex)
        {
            ErrorLog.Write("backup: support", ex);
            Support = new BackupSupport { Problem = "Could not check what the server supports: " + ex.Message };
        }
        if (!Support.SupportsCompression) Compress = false;
        SuggestBackupPath();
        Status = !Support.IsAvailable ? "" : IsReadOnly
            ? "Read-only connection: you can back up, but not restore."
            : SelectedTabIndex == 0 ? "Pick a database and a file, then Back up." : "Pick a backup file and where it goes, then Restore.";
    }

    private bool CanBackup => !IsRunning && IsAvailable && !string.IsNullOrEmpty(BackupDatabase) && BackupPath.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanBackup))]
    private async Task StartBackupAsync()
    {
        var path = BackupPath.Trim();
        if (!IsServerFile)
        {
            if (!Path.IsPathFullyQualified(path))
            {
                Status = "Give the file's full path, or pick it with Browse….";
                return;
            }
            if (File.Exists(path) && !await _dialogs.ConfirmAsync($"{Path.GetFileName(path)} already exists. Replace it with the new backup?", "Replace"))
                return;
            var folder = Path.GetDirectoryName(path);
            if (folder is not null && !Directory.Exists(folder))
            {
                Status = "The folder does not exist: " + folder;
                return;
            }
        }
        else if (!path.Contains('\\') && !path.Contains('/'))
        {
            Status = "Give the full path on the server, for example D:\\Backups\\" + Path.GetFileName(path) + ".";
            return;
        }

        var request = new BackupRequest
        {
            Database = BackupDatabase!, Path = path, Format = Format, SchemaOnly = ShowSchemaOnly && SchemaOnly,
            CopyOnly = !ShowCopyOnly || CopyOnly, Compress = ShowCompress ? Compress : Format == BackupFormat.Custom, Verify = ShowVerify && Verify
        };
        await RunAsync($"Backing up {request.Database}…", ct => _engine!.BackupAsync(request, ToolFolder, Progress(), ct), () =>
        {
            if (!IsServerFile)
            {
                _lastBackupFile = path;
                CanShowFile = true;
                _settings.SetLastBackupFolder(Path.GetDirectoryName(path));
                var size = File.Exists(path) ? $" ({FormatSize(new FileInfo(path).Length)})" : "";
                Status = $"Backed up {request.Database} to {Path.GetFileName(path)}{size}.";
            }
            else
            {
                Status = $"Backed up {request.Database} to {path} on the server.";
            }
        });
    }

    [RelayCommand(CanExecute = nameof(CanBrowse))]
    private async Task BrowseBackupPathAsync()
    {
        if (Support is not { } support || string.IsNullOrEmpty(BackupDatabase)) return;
        var ext = support.ExtensionFor(Format);
        var picked = await _dialogs.PickSaveFilePathAsync("Back up " + BackupDatabase, Path.GetFileNameWithoutExtension(FileName(BackupDatabase, DateTime.Now, ext)),
            ext, Format == BackupFormat.Custom && !IsServerFile ? "pg_dump archive" : IsServerFile ? "SQL Server backup" : "SQL script");
        if (picked is not null) BackupPath = picked;
    }

    private bool CanBrowse => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanBrowse))]
    private async Task BrowseRestorePathAsync()
    {
        var types = IsServerFile
            ? new List<(string, string[])> { ("SQL Server backup", ["*.bak"]), ("All files", ["*"]) }
            : Format == BackupFormat.Custom || Support?.Formats.Count > 1
                ? [("Backups", ["*.dump", "*.backup", "*.sql", "*.tar"]), ("All files", ["*"])]
                : [("SQL scripts", ["*.sql"]), ("All files", ["*"])];
        var picked = await _dialogs.PickFilePathAsync("Restore from", types);
        if (picked is null) return;
        RestorePath = picked;
        await ReadFileAsync();
    }

    private bool CanRead => !IsRunning && IsAvailable && RestorePath.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanRead))]
    private async Task ReadFileAsync()
    {
        var path = RestorePath.Trim();
        FileInfo = null;
        FileInfoText = "Reading the file…";
        try
        {
            var info = await Task.Run(() => _engine!.InspectBackupAsync(path, ToolFolder, _session.Lifetime));
            FileInfo = info;
            FileInfoText = Describe(info);
            if (info.Database is { Length: > 0 } name && NewDatabaseName.Length == 0)
                NewDatabaseName = Databases.Contains(name, StringComparer.OrdinalIgnoreCase) ? name + "_restored" : name;
        }
        catch (Exception ex)
        {
            FileInfoText = ex is BackupException ? ex.Message : "Could not read the file: " + ex.Message;
        }
    }

    public static string Describe(BackupFileInfo info)
    {
        var lines = new List<string> { info.Kind + (info.Database is { } db ? " of " + db : "") };
        var facts = new List<string>();
        if (info.TakenAt is { } t) facts.Add("taken " + t.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture));
        if (info.Server is { } s) facts.Add("from " + s);
        if (info.SizeBytes is { } b) facts.Add(FormatSize(b));
        if (facts.Count > 0) lines.Add(string.Join(" · ", facts));
        lines.AddRange(info.Details);
        return string.Join("\n", lines);
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB"
    };

    private bool CanRestore => !IsRunning && IsAvailable && RestorePath.Trim().Length > 0 && RestoreTarget.Length > 0;

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task StartRestoreAsync()
    {
        if (IsReadOnly)
        {
            Status = ReadOnlyGuard.Refusal(_session.Profile, "RESTORE");
            return;
        }
        var target = RestoreTarget;
        var exists = Databases.Contains(target, StringComparer.OrdinalIgnoreCase);
        if (RestoreToNew && exists)
        {
            Status = $"A database named {target} already exists. Choose \"An existing database\" to replace it, or another name.";
            return;
        }
        var request = new RestoreRequest
        {
            Path = RestorePath.Trim(), Database = target, CreateDatabase = RestoreToNew,
            SkipOwnership = !ShowSkipOwnership || SkipOwnership, CloseConnections = ShowCloseConnections && CloseConnections && !RestoreToNew
        };

        string plan;
        try
        {
            plan = await Task.Run(() => _engine!.DescribeRestoreAsync(request, ToolFolder, _session.Lifetime));
        }
        catch (Exception ex)
        {
            Status = ex is BackupException ? ex.Message : "Could not prepare the restore: " + ex.Message;
            return;
        }

        var production = IsProduction;
        var source = Path.GetFileName(request.Path.Replace('\\', '/'));
        var message = RestoreToNew
            ? $"Create the database {target} and restore {source} into it?"
            : $"Restore {source} over the existing database {target}? {RestoreOverExisting}" +
              (request.CloseConnections ? " Other sessions in it are disconnected first and their open transactions rolled back." : "");
        if (production) message += " You are on a PRODUCTION connection.";
        var typed = production ? "PRODUCTION" : RestoreToNew ? null : target;
        if (!await _dialogs.ConfirmAsync(message, RestoreToNew ? "Restore" : "Replace " + target, typed,
                production ? $"PRODUCTION · {_session.Profile.DisplayName}" : null, plan)) return;

        await RunAsync($"Restoring into {target}…", ct => _engine!.RestoreAsync(request, ToolFolder, Progress(), ct), () =>
        {
            Status = $"Restored {source} into {target}.";
            if (RestoreToNew)
            {
                Databases = [.. Databases.Append(target).Order(StringComparer.OrdinalIgnoreCase)];
                RestoreToNew = false;
                RestoreTargetDatabase = target;
            }
            _restored(target);
        });
    }

    [RelayCommand(CanExecute = nameof(CanBrowse))]
    private async Task PickToolFolderAsync()
    {
        var folder = await _dialogs.PickFolderPathAsync("Folder with the client tools (pg_dump, mysqldump…)");
        if (folder is null) return;
        _settings.SetBackupToolFolder(_session.Profile.ProviderKey, folder);
        await LoadSupportAsync();
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void ShowFile()
    {
        if (_lastBackupFile is not { } path || !File.Exists(path)) return;
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start("explorer.exe", $"/select,\"{path}\"");
            else Process.Start(new ProcessStartInfo(Path.GetDirectoryName(path)!) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ErrorLog.Write("backup: show file", ex);
        }
    }

    private IProgress<BackupProgress> Progress() => new Progress<BackupProgress>(p =>
    {
        foreach (var line in p.Line.Split('\n')) AddLog(line.TrimEnd('\r'));
        if (p.Percent is { } v)
        {
            IsIndeterminate = false;
            Percent = v;
        }
    });

    private void AddLog(string line)
    {
        _logLines.Add(line);
        if (_logLines.Count > MaxLogLines) _logLines.RemoveRange(0, _logLines.Count - MaxLogLines);
        Log = string.Join("\n", _logLines);
    }

    private async Task RunAsync(string starting, Func<CancellationToken, Task> work, Action done)
    {
        IsRunning = true;
        IsIndeterminate = true;
        Percent = 0;
        CanShowFile = false;
        _logLines.Clear();
        Log = "";
        Status = starting;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(_session.Lifetime);
        var watch = Stopwatch.StartNew();
        try
        {
            await Task.Run(() => work(_cts.Token));
            IsIndeterminate = false;
            Percent = 100;
            done();
            Status += $" Took {Elapsed(watch.Elapsed)}.";
        }
        catch (OperationCanceledException)
        {
            Status = IsServerFile && SelectedTabIndex == 1
                ? "Cancelled. A cancelled SQL Server restore leaves the database restoring; restore it again to use it."
                : "Cancelled.";
        }
        catch (Exception ex)
        {
            if (ex is not BackupException) ErrorLog.Write("backup/restore", ex);
            Status = ex.Message;
            if (ex is BackupException { Output: { Length: > 0 } output } && !_logLines.Any(l => output.Contains(l, StringComparison.Ordinal) && l.Length > 0))
                AddLog(output);
        }
        finally
        {
            IsRunning = false;
            IsIndeterminate = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private static string Elapsed(TimeSpan t) => t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min {t.Seconds} s" : $"{t.TotalSeconds:0.#} s";

    /// <summary>Called when the window closes: a backup or restore still running is cancelled.</summary>
    public void Close() => _cts?.Cancel();
}
