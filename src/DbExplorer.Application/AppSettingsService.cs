using System.Text.Json;
using DbExplorer.Application.Query;
using DbExplorer.Core.Models;

namespace DbExplorer.Application;

/// <summary>Experimental Query tab features, each of which can be turned off (all are on by default).</summary>
public enum ExperimentalFeature
{
    /// <summary>Re-running a query shows which rows are new, gone or changed since the previous run.</summary>
    ResultDiff,

    /// <summary>The statement at the caret shows the planner's estimate (rows, cost, full scans) before it is run.</summary>
    CostLens,

    /// <summary><c>-- expect: …</c> comments are checked against the results after each run.</summary>
    Expectations
}

/// <summary>Small app-wide preferences persisted as JSON: the UI theme, the SQL editor's zoom, word wrap and live SQL
/// warnings, the defaults of new Query tabs, when a long run notifies, and which experimental features are on. Most of
/// them are edited in the Preferences window (Ctrl+,).</summary>
public sealed class AppSettingsService
{
    public const double DefaultEditorFontSize = 13;
    public const double MinEditorFontSize = 8;
    public const double MaxEditorFontSize = 36;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _file;

    public AppSettingsService(AppPaths paths)
    {
        _file = Path.Combine(paths.Root, "settings.json");
        var stored = Load();
        Theme = stored.Theme;
        EditorFontSize = ClampFontSize(stored.EditorFontSize ?? DefaultEditorFontSize);
        EditorWordWrap = stored.EditorWordWrap;
        SqlInspections = !stored.SqlInspectionsOff;
        InlineResults = !stored.InlineResultsOff;
        SoftwareRendering = SoftwareRenderingAtStart = stored.SoftwareRendering;
        ShowNavigator = !stored.NavigatorHidden;
        ReconnectOnStart = !stored.ReconnectOnStartOff;
        NavigatorWidth = ClampNavigatorWidth(stored.NavigatorWidth ?? DefaultNavigatorWidth);
        DefaultRowLimit = ClampRowLimit(stored.DefaultRowLimit ?? DefaultRowLimitValue);
        DefaultAutoCommit = !stored.DefaultAutoCommitOff;
        LongRunNotifySeconds = ClampNotifySeconds(stored.LongRunNotifySeconds ?? DefaultLongRunNotifySeconds);
        SqlFormat = (stored.SqlFormat ?? SqlFormatOptions.Default).Normalized();
        _backupToolFolders = new Dictionary<string, string>(stored.BackupToolFolders ?? [], StringComparer.OrdinalIgnoreCase);
        LastBackupFolder = stored.LastBackupFolder;
        CheckForUpdates = !stored.UpdateCheckOff;
        IncludePreReleaseUpdates = stored.PreReleaseUpdates;
        LastUpdateCheck = stored.LastUpdateCheck;
        SkippedUpdateVersion = string.IsNullOrWhiteSpace(stored.SkippedUpdateVersion) ? null : stored.SkippedUpdateVersion.Trim();
        _recentCommands = (stored.RecentCommands ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().Take(MaxRecentCommands).ToList();
        _disabled = (stored.DisabledExperiments ?? [])
            .Select(name => Enum.TryParse<ExperimentalFeature>(name, out var f) ? f : (ExperimentalFeature?)null)
            .OfType<ExperimentalFeature>().ToHashSet();
    }

    private readonly HashSet<ExperimentalFeature> _disabled;

    /// <summary>Where the preferences are saved (settings.json in the app's data folder).</summary>
    public string FilePath => _file;

    public AppThemeMode Theme { get; private set; }

    /// <summary>Font size of the SQL editors (zoomed with Ctrl+wheel, Ctrl+= and Ctrl+-), the same in every query tab.</summary>
    public double EditorFontSize { get; private set; }

    /// <summary>Whether the SQL editors wrap long lines (Alt+Z).</summary>
    public bool EditorWordWrap { get; private set; }

    /// <summary>Whether the SQL editors underline unknown tables and columns, ambiguous names and GROUP BY mistakes while
    /// typing (checked against the cached catalog, never on the server).</summary>
    public bool SqlInspections { get; private set; }

    /// <summary>Whether a Query tab also shows the first rows of each result right under the statement that produced it,
    /// in the editor (the grid below still gets every result).</summary>
    public bool InlineResults { get; private set; }

    /// <summary>Whether the window is drawn by the CPU instead of the graphics card (read at start-up, so a change
    /// applies after a restart). A way out when the GPU path stops repainting, for example after a long minimize.</summary>
    public bool SoftwareRendering { get; private set; }

    /// <summary>The software rendering setting as it was read when the app started, i.e. the one in use now.</summary>
    public bool SoftwareRenderingAtStart { get; }

    public const double DefaultNavigatorWidth = 260;
    public const double MinNavigatorWidth = 180;
    public const double MaxNavigatorWidth = 700;

    /// <summary>Whether the database navigator (the tree of connections, databases and objects on the left of the
    /// main window) is shown; toggled with its toolbar button or Ctrl+B.</summary>
    public bool ShowNavigator { get; private set; }

    /// <summary>The navigator's width in pixels, as the user last dragged it.</summary>
    public double NavigatorWidth { get; private set; }

    /// <summary>Whether a connection that was open when DbExplorer closed is opened again at the next start (production
    /// connections are only selected, never opened by themselves).</summary>
    public bool ReconnectOnStart { get; private set; }

    public void SetReconnectOnStart(bool on)
    {
        if (ReconnectOnStart == on) return;
        ReconnectOnStart = on;
        Save();
    }

    public const int DefaultRowLimitValue = 10_000;
    public const int MaxRowLimit = 10_000_000;

    /// <summary>The row limit a new Query tab starts with (0 = no limit); each tab can still change its own.</summary>
    public int DefaultRowLimit { get; private set; }

    public void SetDefaultRowLimit(int rows)
    {
        rows = ClampRowLimit(rows);
        if (DefaultRowLimit == rows) return;
        DefaultRowLimit = rows;
        Save();
    }

    public static int ClampRowLimit(int rows) => Math.Clamp(rows, 0, MaxRowLimit);

    /// <summary>Whether a new Query tab starts with Auto-commit on (off: runs stay in a transaction until Commit or
    /// Rollback); each tab can still switch its own.</summary>
    public bool DefaultAutoCommit { get; private set; }

    public void SetDefaultAutoCommit(bool on)
    {
        if (DefaultAutoCommit == on) return;
        DefaultAutoCommit = on;
        Save();
    }

    public const int DefaultLongRunNotifySeconds = 10;
    public const int MaxLongRunNotifySeconds = 3600;

    /// <summary>A run that took at least this many seconds shows a notification (and flashes the taskbar button) when
    /// it finishes while the user looks elsewhere; 0 turns the notification off.</summary>
    public int LongRunNotifySeconds { get; private set; }

    public void SetLongRunNotifySeconds(int seconds)
    {
        seconds = ClampNotifySeconds(seconds);
        if (LongRunNotifySeconds == seconds) return;
        LongRunNotifySeconds = seconds;
        Save();
    }

    public static int ClampNotifySeconds(int seconds) => Math.Clamp(seconds, 0, MaxLongRunNotifySeconds);

    /// <summary>The SQL formatter's style (Format, Ctrl+Shift+F), the same in every Query tab.</summary>
    public SqlFormatOptions SqlFormat { get; private set; }

    public void SetSqlFormat(SqlFormatOptions options)
    {
        options = options.Normalized();
        if (SqlFormat == options) return;
        SqlFormat = options;
        Save();
    }

    /// <summary>Whether DB Explorer looks for a newer release on GitHub at start-up (at most once a day).</summary>
    public bool CheckForUpdates { get; private set; }

    public void SetCheckForUpdates(bool on)
    {
        if (CheckForUpdates == on) return;
        CheckForUpdates = on;
        Save();
    }

    /// <summary>Whether pre-releases (1.2.0-rc1) are offered as updates too.</summary>
    public bool IncludePreReleaseUpdates { get; private set; }

    public void SetIncludePreReleaseUpdates(bool on)
    {
        if (IncludePreReleaseUpdates == on) return;
        IncludePreReleaseUpdates = on;
        Save();
    }

    /// <summary>When the releases were last read successfully (automatic or "Check now").</summary>
    public DateTimeOffset? LastUpdateCheck { get; private set; }

    public void SetLastUpdateCheck(DateTimeOffset when)
    {
        LastUpdateCheck = when;
        Save();
    }

    /// <summary>A version the user chose to skip: the start-up check stays quiet about it (not about newer ones).</summary>
    public string? SkippedUpdateVersion { get; private set; }

    public void SetSkippedUpdateVersion(string? version)
    {
        version = string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        if (SkippedUpdateVersion == version) return;
        SkippedUpdateVersion = version;
        Save();
    }

    /// <summary>Puts every preference shown in the Preferences window back to its default (theme, editor, new tab
    /// defaults, notifications, start-up, updates, rendering and experiments). The navigator's width and visibility and the
    /// command palette's recent list are left alone.</summary>
    public void ResetPreferences()
    {
        SetTheme(AppThemeMode.System);
        var editorChanged = !EditorFontSize.Equals(DefaultEditorFontSize) || EditorWordWrap || !SqlInspections || !InlineResults;
        EditorFontSize = DefaultEditorFontSize;
        EditorWordWrap = false;
        SqlInspections = true;
        InlineResults = true;
        if (editorChanged) EditorChanged?.Invoke();
        DefaultRowLimit = DefaultRowLimitValue;
        DefaultAutoCommit = true;
        LongRunNotifySeconds = DefaultLongRunNotifySeconds;
        SqlFormat = SqlFormatOptions.Default;
        ReconnectOnStart = true;
        SoftwareRendering = false;
        CheckForUpdates = true;
        IncludePreReleaseUpdates = false;
        SkippedUpdateVersion = null;
        foreach (var feature in _disabled.ToList()) SetEnabled(feature, true);
        Save();
    }

    private readonly Dictionary<string, string> _backupToolFolders;

    /// <summary>The folder the user picked for an engine's dump tools (pg_dump, mysqldump), or null to search for them.</summary>
    public string? BackupToolFolder(string providerKey) => _backupToolFolders.GetValueOrDefault(providerKey);

    public void SetBackupToolFolder(string providerKey, string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) _backupToolFolders.Remove(providerKey);
        else _backupToolFolders[providerKey] = folder.Trim();
        Save();
    }

    /// <summary>The folder on this computer the last backup was saved to.</summary>
    public string? LastBackupFolder { get; private set; }

    public void SetLastBackupFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || folder == LastBackupFolder) return;
        LastBackupFolder = folder;
        Save();
    }

    public const int MaxRecentCommands = 8;
    private List<string> _recentCommands;

    /// <summary>Command palette commands, most recently used first.</summary>
    public IReadOnlyList<string> RecentCommands => _recentCommands;

    public void AddRecentCommand(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        if (_recentCommands.Count > 0 && _recentCommands[0] == id) return;
        _recentCommands.Remove(id);
        _recentCommands.Insert(0, id);
        if (_recentCommands.Count > MaxRecentCommands) _recentCommands.RemoveRange(MaxRecentCommands, _recentCommands.Count - MaxRecentCommands);
        Save();
    }

    public void SetShowNavigator(bool show)
    {
        if (ShowNavigator == show) return;
        ShowNavigator = show;
        Save();
    }

    public void SetNavigatorWidth(double width)
    {
        var clamped = ClampNavigatorWidth(width);
        if (Math.Abs(clamped - NavigatorWidth) < 1) return;
        NavigatorWidth = clamped;
        Save();
    }

    public static double ClampNavigatorWidth(double width) =>
        double.IsFinite(width) ? Math.Clamp(Math.Round(width), MinNavigatorWidth, MaxNavigatorWidth) : DefaultNavigatorWidth;

    public event Action<AppThemeMode>? ThemeChanged;

    /// <summary>Raised when an experimental feature was turned on or off.</summary>
    public event Action<ExperimentalFeature>? ExperimentChanged;

    public bool IsEnabled(ExperimentalFeature feature) => !_disabled.Contains(feature);

    public void SetEnabled(ExperimentalFeature feature, bool enabled)
    {
        if (IsEnabled(feature) == enabled) return;
        if (enabled) _disabled.Remove(feature);
        else _disabled.Add(feature);
        ExperimentChanged?.Invoke(feature);
        Save();
    }

    /// <summary>Raised when the editor font size, word wrap or live SQL warnings changed, so every open editor follows.</summary>
    public event Action? EditorChanged;

    public void SetTheme(AppThemeMode theme)
    {
        if (Theme == theme) return;
        Theme = theme;
        ThemeChanged?.Invoke(theme);
        Save();
    }

    public void SetEditorFontSize(double size)
    {
        size = ClampFontSize(size);
        if (EditorFontSize.Equals(size)) return;
        EditorFontSize = size;
        EditorChanged?.Invoke();
        Save();
    }

    public void SetEditorWordWrap(bool wrap)
    {
        if (EditorWordWrap == wrap) return;
        EditorWordWrap = wrap;
        EditorChanged?.Invoke();
        Save();
    }

    public void SetSqlInspections(bool on)
    {
        if (SqlInspections == on) return;
        SqlInspections = on;
        EditorChanged?.Invoke();
        Save();
    }

    public void SetInlineResults(bool on)
    {
        if (InlineResults == on) return;
        InlineResults = on;
        EditorChanged?.Invoke();
        Save();
    }

    public void SetSoftwareRendering(bool on)
    {
        if (SoftwareRendering == on) return;
        SoftwareRendering = on;
        Save();
    }

    public static double ClampFontSize(double size) =>
        double.IsFinite(size) ? Math.Clamp(Math.Round(size), MinEditorFontSize, MaxEditorFontSize) : DefaultEditorFontSize;

    private StoredSettings Load()
    {
        try
        {
            if (!File.Exists(_file)) return new StoredSettings();
            return JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(_file), Json) ?? new StoredSettings();
        }
        catch
        {
            return new StoredSettings();
        }
    }

    private void Save()
    {
        try
        {
            var stored = new StoredSettings
            {
                Theme = Theme, EditorFontSize = EditorFontSize, EditorWordWrap = EditorWordWrap, SqlInspectionsOff = !SqlInspections,
                InlineResultsOff = !InlineResults,
                SoftwareRendering = SoftwareRendering,
                NavigatorHidden = !ShowNavigator,
                ReconnectOnStartOff = !ReconnectOnStart,
                NavigatorWidth = Math.Abs(NavigatorWidth - DefaultNavigatorWidth) < 1 ? null : NavigatorWidth,
                DefaultRowLimit = DefaultRowLimit == DefaultRowLimitValue ? null : DefaultRowLimit,
                DefaultAutoCommitOff = !DefaultAutoCommit,
                LongRunNotifySeconds = LongRunNotifySeconds == DefaultLongRunNotifySeconds ? null : LongRunNotifySeconds,
                SqlFormat = SqlFormat == SqlFormatOptions.Default ? null : SqlFormat,
                UpdateCheckOff = !CheckForUpdates,
                PreReleaseUpdates = IncludePreReleaseUpdates,
                LastUpdateCheck = LastUpdateCheck,
                SkippedUpdateVersion = SkippedUpdateVersion,
                DisabledExperiments = _disabled.Count == 0 ? null : _disabled.Order().Select(f => f.ToString()).ToList(),
                RecentCommands = _recentCommands.Count == 0 ? null : _recentCommands.ToList(),
                BackupToolFolders = _backupToolFolders.Count == 0 ? null : new Dictionary<string, string>(_backupToolFolders),
                LastBackupFolder = LastBackupFolder
            };
            AtomicFile.WriteAllText(_file, JsonSerializer.Serialize(stored, Json));
        }
        catch
        {
            // Best-effort; a failed save just means the preference resets to default next launch.
        }
    }

    private sealed class StoredSettings
    {
        public AppThemeMode Theme { get; set; }
        public double? EditorFontSize { get; set; }
        public bool EditorWordWrap { get; set; }

        /// <summary>Stored as "off" so the warnings start on.</summary>
        public bool SqlInspectionsOff { get; set; }

        /// <summary>Stored as "off" so results under statements start on.</summary>
        public bool InlineResultsOff { get; set; }

        public bool SoftwareRendering { get; set; }

        /// <summary>Null while the formatter uses its default style.</summary>
        public SqlFormatOptions? SqlFormat { get; set; }

        /// <summary>Stored as "hidden" so the navigator starts shown.</summary>
        public bool NavigatorHidden { get; set; }

        public double? NavigatorWidth { get; set; }

        /// <summary>Stored as "off" so reconnecting starts on.</summary>
        public bool ReconnectOnStartOff { get; set; }

        public int? DefaultRowLimit { get; set; }

        /// <summary>Stored as "off" so new tabs start with auto-commit on.</summary>
        public bool DefaultAutoCommitOff { get; set; }

        public int? LongRunNotifySeconds { get; set; }

        /// <summary>Stored as "off" so the update check starts on.</summary>
        public bool UpdateCheckOff { get; set; }

        public bool PreReleaseUpdates { get; set; }
        public DateTimeOffset? LastUpdateCheck { get; set; }
        public string? SkippedUpdateVersion { get; set; }

        /// <summary>Experimental features turned off; stored as the off ones so new experiments start on.</summary>
        public List<string>? DisabledExperiments { get; set; }

        /// <summary>Command palette commands, most recent first.</summary>
        public List<string>? RecentCommands { get; set; }

        /// <summary>Dump tool folder per engine key, only for engines where the user picked one.</summary>
        public Dictionary<string, string>? BackupToolFolders { get; set; }

        public string? LastBackupFolder { get; set; }
    }
}
