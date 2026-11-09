using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application;
using DbExplorer.Application.Assistant;
using DbExplorer.Application.Query;
using DbExplorer.Application.Updates;
using DbExplorer.Core.Models;
using DbExplorer.Desktop.Services;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>
/// The Preferences window (Ctrl+,): every app-wide setting in one place. Each change is saved and applied at once, like
/// the toolbar menus and shortcuts that change the same settings; those keep working and the window follows them.
/// </summary>
public sealed partial class PreferencesViewModel : ViewModelBase, IDisposable
{
    private readonly AppSettingsService _settings;
    private readonly AssistantSettings _assistant;
    private readonly IDialogService _dialogs;

    public PreferencesViewModel(AppSettingsService settings, AssistantSettings assistant, IDialogService dialogs)
    {
        _settings = settings;
        _assistant = assistant;
        _dialogs = dialogs;
        _settings.ThemeChanged += OnThemeChanged;
        _settings.EditorChanged += OnEditorChanged;
        _settings.ExperimentChanged += OnExperimentChanged;
        _assistant.Changed += OnAssistantChanged;
    }

    public void Dispose()
    {
        _settings.ThemeChanged -= OnThemeChanged;
        _settings.EditorChanged -= OnEditorChanged;
        _settings.ExperimentChanged -= OnExperimentChanged;
        _assistant.Changed -= OnAssistantChanged;
    }

    // ---- Appearance ----

    public IReadOnlyList<AppThemeMode> ThemeModes { get; } = Enum.GetValues<AppThemeMode>();

    public AppThemeMode Theme
    {
        get => _settings.Theme;
        set => _settings.SetTheme(value);
    }

    public bool SoftwareRendering
    {
        get => _settings.SoftwareRendering;
        set
        {
            if (value == _settings.SoftwareRendering) return;
            _settings.SetSoftwareRendering(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(RestartNeeded));
        }
    }

    /// <summary>The rendering choice differs from the one this run started with, and only takes effect after a restart.</summary>
    public bool RestartNeeded => SoftwareRendering != _settings.SoftwareRenderingAtStart;

    // ---- SQL editor ----

    public decimal MinFontSize => (decimal)AppSettingsService.MinEditorFontSize;
    public decimal MaxFontSize => (decimal)AppSettingsService.MaxEditorFontSize;

    public decimal EditorFontSize
    {
        get => (decimal)_settings.EditorFontSize;
        set => _settings.SetEditorFontSize((double)value);
    }

    public bool EditorWordWrap
    {
        get => _settings.EditorWordWrap;
        set => _settings.SetEditorWordWrap(value);
    }

    public bool SqlInspections
    {
        get => _settings.SqlInspections;
        set => _settings.SetSqlInspections(value);
    }

    public bool InlineResults
    {
        get => _settings.InlineResults;
        set => _settings.SetInlineResults(value);
    }

    // ---- SQL formatting ----

    /// <summary>The window's name for the SQL formatting section (Query tab › Tools › Formatting options… scrolls to it).</summary>
    public const string FormattingSection = "FormattingSection";

    /// <summary>A choice in one of the formatting drop-downs.</summary>
    public sealed record Choice<T>(T Value, string Label)
    {
        public override string ToString() => Label;
    }

    public IReadOnlyList<Choice<SqlKeywordCase>> KeywordCases { get; } =
    [
        new(SqlKeywordCase.Upper, "UPPER CASE"), new(SqlKeywordCase.Lower, "lower case"), new(SqlKeywordCase.AsTyped, "As typed")
    ];

    /// <summary>Indent sizes; 0 stands for a tab.</summary>
    public IReadOnlyList<Choice<int>> Indents { get; } =
    [
        new(2, "2 spaces"), new(4, "4 spaces"), new(8, "8 spaces"), new(0, "Tab")
    ];

    public IReadOnlyList<Choice<SqlCommaStyle>> CommaStyles { get; } =
    [
        new(SqlCommaStyle.EndOfLine, "At the end of the line"), new(SqlCommaStyle.StartOfLine, "At the start of the next line")
    ];

    private SqlFormatOptions Format => _settings.SqlFormat;

    private void SetFormat(SqlFormatOptions options)
    {
        _settings.SetSqlFormat(options);
        OnFormatChanged();
    }

    private void OnFormatChanged()
    {
        OnPropertyChanged(nameof(KeywordCase));
        OnPropertyChanged(nameof(Indent));
        OnPropertyChanged(nameof(CommaStyle));
        OnPropertyChanged(nameof(OneItemPerLine));
        OnPropertyChanged(nameof(BreakAndOr));
        OnPropertyChanged(nameof(CompactShortExpressions));
        OnPropertyChanged(nameof(FormatPreview));
    }

    public Choice<SqlKeywordCase> KeywordCase
    {
        get => KeywordCases.First(c => c.Value == Format.KeywordCase);
        set { if (value is not null) SetFormat(Format with { KeywordCase = value.Value }); }
    }

    public Choice<int> Indent
    {
        get => Indents.FirstOrDefault(c => Format.UseTabs ? c.Value == 0 : c.Value == Format.IndentSize)
               ?? new Choice<int>(Format.IndentSize, $"{Format.IndentSize} spaces");
        set { if (value is not null) SetFormat(value.Value == 0 ? Format with { UseTabs = true } : Format with { UseTabs = false, IndentSize = value.Value }); }
    }

    public Choice<SqlCommaStyle> CommaStyle
    {
        get => CommaStyles.First(c => c.Value == Format.Commas);
        set { if (value is not null) SetFormat(Format with { Commas = value.Value }); }
    }

    public bool OneItemPerLine
    {
        get => Format.OneItemPerLine;
        set => SetFormat(Format with { OneItemPerLine = value });
    }

    public bool BreakAndOr
    {
        get => Format.BreakAndOr;
        set => SetFormat(Format with { BreakAndOr = value });
    }

    public bool CompactShortExpressions
    {
        get => Format.CompactShortExpressions;
        set => SetFormat(Format with { CompactShortExpressions = value });
    }

    /// <summary>A sample query laid out with the chosen style, under the options.</summary>
    public string FormatPreview => SqlFormatter.Format(PreviewSql, Format).Replace("\t", "    ");

    internal const string PreviewSql =
        "with recent as (select customer_id, max(ordered_at) as last_order from orders group by customer_id) " +
        "select c.id, c.name, case when r.last_order is null then 'never' when r.last_order < now() - interval '1 year' then 'lapsed' else 'active' end as status " +
        "from customers c left join recent r on r.customer_id = c.id " +
        "where c.country in ('AM', 'FR') and c.deleted_at is null order by c.name";

    // ---- New Query tabs ----

    public decimal MaxRowLimit => AppSettingsService.MaxRowLimit;

    /// <summary>Rows kept per result set in a new tab; 0 = no limit.</summary>
    public decimal DefaultRowLimit
    {
        get => _settings.DefaultRowLimit;
        set
        {
            _settings.SetDefaultRowLimit((int)Math.Clamp(value, 0, AppSettingsService.MaxRowLimit));
            OnPropertyChanged();
        }
    }

    public bool DefaultAutoCommit
    {
        get => _settings.DefaultAutoCommit;
        set
        {
            _settings.SetDefaultAutoCommit(value);
            OnPropertyChanged();
        }
    }

    // ---- Notifications ----

    public decimal MaxNotifySeconds => AppSettingsService.MaxLongRunNotifySeconds;

    /// <summary>Notify when a long run finishes while the user looks elsewhere.</summary>
    public bool NotifyLongRuns
    {
        get => _settings.LongRunNotifySeconds > 0;
        set
        {
            if (value == NotifyLongRuns) return;
            _settings.SetLongRunNotifySeconds(value ? _lastNotifySeconds : 0);
            OnPropertyChanged();
            OnPropertyChanged(nameof(LongRunNotifySeconds));
        }
    }

    private int _lastNotifySeconds = AppSettingsService.DefaultLongRunNotifySeconds;

    public decimal LongRunNotifySeconds
    {
        get => NotifyLongRuns ? _settings.LongRunNotifySeconds : _lastNotifySeconds;
        set
        {
            var seconds = (int)Math.Clamp(value, 1, AppSettingsService.MaxLongRunNotifySeconds);
            _lastNotifySeconds = seconds;
            if (NotifyLongRuns) _settings.SetLongRunNotifySeconds(seconds);
            OnPropertyChanged();
        }
    }

    // ---- Start-up ----

    public bool ReconnectOnStart
    {
        get => _settings.ReconnectOnStart;
        set
        {
            _settings.SetReconnectOnStart(value);
            OnPropertyChanged();
        }
    }

    // ---- Updates ----

    /// <summary>The running version, shown next to the update settings.</summary>
    public string CurrentVersion { get; init; } = "";

    /// <summary>Reads the releases now and shows a newer one in the main window's update bar (null in tests without it).</summary>
    public Func<Task<UpdateCheckResult?>>? CheckForUpdatesNow { get; init; }

    public bool CheckForUpdates
    {
        get => _settings.CheckForUpdates;
        set
        {
            _settings.SetCheckForUpdates(value);
            OnPropertyChanged();
        }
    }

    public bool IncludePreReleaseUpdates
    {
        get => _settings.IncludePreReleaseUpdates;
        set
        {
            _settings.SetIncludePreReleaseUpdates(value);
            OnPropertyChanged();
        }
    }

    [ObservableProperty] private string? _updateCheckResult;
    [ObservableProperty] private bool _isCheckingForUpdates;

    public string UpdateStatus
    {
        get
        {
            var version = string.IsNullOrEmpty(CurrentVersion) ? "This is DB Explorer." : $"This is DB Explorer {CurrentVersion}.";
            var last = _settings.LastUpdateCheck is { } at ? $" Last checked {at.ToLocalTime():g}." : " Not checked yet.";
            var skipped = _settings.SkippedUpdateVersion is { } s ? $" Skipped: {s}." : "";
            return version + last + skipped;
        }
    }

    [RelayCommand]
    private async Task CheckUpdatesNowAsync()
    {
        if (CheckForUpdatesNow is null || IsCheckingForUpdates) return;
        IsCheckingForUpdates = true;
        UpdateCheckResult = "Checking…";
        try
        {
            var result = await CheckForUpdatesNow();
            UpdateCheckResult = result is null ? null : result.Status switch
            {
                UpdateCheckStatus.Available => result.Describe() + " See the bar at the top of the main window.",
                _ => result.Describe()
            };
        }
        finally
        {
            IsCheckingForUpdates = false;
            OnPropertyChanged(nameof(UpdateStatus));
        }
    }

    // ---- Experimental features ----

    public bool ResultDiff
    {
        get => _settings.IsEnabled(ExperimentalFeature.ResultDiff);
        set => _settings.SetEnabled(ExperimentalFeature.ResultDiff, value);
    }

    public bool Expectations
    {
        get => _settings.IsEnabled(ExperimentalFeature.Expectations);
        set => _settings.SetEnabled(ExperimentalFeature.Expectations, value);
    }

    public bool CostLens
    {
        get => _settings.IsEnabled(ExperimentalFeature.CostLens);
        set => _settings.SetEnabled(ExperimentalFeature.CostLens, value);
    }

    // ---- AI assistant ----

    public string AssistantStatus => _assistant.IsConfigured
        ? "API key saved: " + AssistantSettings.Mask(_assistant.ApiKey)
        : "No API key yet: the assistant is hidden in the Query tabs.";

    public string AssistantButtonText => _assistant.IsConfigured ? "Change or remove key…" : "Add API key…";

    [RelayCommand]
    private Task EditAssistantKeyAsync() => _dialogs.EditAssistantSettingsAsync(_assistant);

    // ---- Files ----

    public string SettingsFile => _settings.FilePath;

    [RelayCommand]
    private void OpenSettingsFolder()
    {
        try
        {
            var folder = Path.GetDirectoryName(_settings.FilePath)!;
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch
        {
            // Nothing to open on this system; the path is shown in the window anyway.
        }
    }

    /// <summary>Puts every setting in this window back to its default, after asking.</summary>
    [RelayCommand]
    private async Task ResetAsync()
    {
        if (!await _dialogs.ConfirmAsync(
                "Put every preference back to its default? Theme, editor zoom and wrap, SQL warnings, SQL formatting, new tab defaults, " +
                "notifications, start-up, updates, rendering and the experimental features are reset. Connections, saved queries, " +
                "snippets and the AI key are kept.",
                "Reset")) return;
        _settings.ResetPreferences();
        _lastNotifySeconds = AppSettingsService.DefaultLongRunNotifySeconds;
        OnPropertyChanged(string.Empty);
        OnFormatChanged();
    }

    private void OnThemeChanged(AppThemeMode _) => OnPropertyChanged(nameof(Theme));

    private void OnEditorChanged()
    {
        OnPropertyChanged(nameof(EditorFontSize));
        OnPropertyChanged(nameof(EditorWordWrap));
        OnPropertyChanged(nameof(SqlInspections));
        OnPropertyChanged(nameof(InlineResults));
    }

    private void OnExperimentChanged(ExperimentalFeature feature) => OnPropertyChanged(feature.ToString());

    private void OnAssistantChanged()
    {
        OnPropertyChanged(nameof(AssistantStatus));
        OnPropertyChanged(nameof(AssistantButtonText));
    }
}
