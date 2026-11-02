using DbExplorer.Application;
using DbExplorer.Application.Assistant;
using DbExplorer.Application.Connections;
using DbExplorer.Application.Query;
using DbExplorer.Core.Models;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Tests;

/// <summary>The Preferences window (Ctrl+,): the settings it adds, Reset, and the view model following other changes.</summary>
public class PreferencesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbx-prefs-" + Guid.NewGuid().ToString("N"));

    public PreferencesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private AppSettingsService Settings() => new(new AppPaths(_root));

    private PreferencesViewModel Preferences(AppSettingsService settings) =>
        new(settings, new AssistantSettings(new AppPaths(_root), new NoProtector()), DesktopTabTests.DialogFake.Create(out _));

    [Fact]
    public void New_settings_start_at_their_defaults()
    {
        var settings = Settings();
        Assert.Equal(10_000, settings.DefaultRowLimit);
        Assert.True(settings.DefaultAutoCommit);
        Assert.Equal(10, settings.LongRunNotifySeconds);
        Assert.Equal(Path.Combine(_root, "settings.json"), settings.FilePath);
    }

    [Fact]
    public void New_settings_survive_a_restart_and_are_clamped()
    {
        var settings = Settings();
        settings.SetDefaultRowLimit(500);
        settings.SetDefaultAutoCommit(false);
        settings.SetLongRunNotifySeconds(45);

        var reloaded = Settings();
        Assert.Equal(500, reloaded.DefaultRowLimit);
        Assert.False(reloaded.DefaultAutoCommit);
        Assert.Equal(45, reloaded.LongRunNotifySeconds);

        reloaded.SetDefaultRowLimit(-5);
        reloaded.SetLongRunNotifySeconds(1_000_000);
        Assert.Equal(0, reloaded.DefaultRowLimit);
        Assert.Equal(AppSettingsService.MaxLongRunNotifySeconds, reloaded.LongRunNotifySeconds);
        reloaded.SetDefaultRowLimit(int.MaxValue);
        Assert.Equal(AppSettingsService.MaxRowLimit, reloaded.DefaultRowLimit);
    }

    [Fact]
    public void Reset_puts_preferences_back_but_keeps_navigator_and_recent_commands()
    {
        var settings = Settings();
        settings.SetTheme(AppThemeMode.Dark);
        settings.SetEditorFontSize(20);
        settings.SetEditorWordWrap(true);
        settings.SetSqlInspections(false);
        settings.SetDefaultRowLimit(0);
        settings.SetDefaultAutoCommit(false);
        settings.SetLongRunNotifySeconds(0);
        settings.SetReconnectOnStart(false);
        settings.SetSoftwareRendering(true);
        settings.SetEnabled(ExperimentalFeature.CostLens, false);
        settings.SetNavigatorWidth(400);
        settings.AddRecentCommand("toggle-reconnect");

        var themes = new List<AppThemeMode>();
        var editor = 0;
        var experiments = new List<ExperimentalFeature>();
        settings.ThemeChanged += themes.Add;
        settings.EditorChanged += () => editor++;
        settings.ExperimentChanged += experiments.Add;

        settings.ResetPreferences();

        Assert.Equal([AppThemeMode.System], themes);
        Assert.Equal(1, editor);
        Assert.Equal([ExperimentalFeature.CostLens], experiments);
        var reloaded = Settings();
        Assert.Equal(AppThemeMode.System, reloaded.Theme);
        Assert.Equal(AppSettingsService.DefaultEditorFontSize, reloaded.EditorFontSize);
        Assert.False(reloaded.EditorWordWrap);
        Assert.True(reloaded.SqlInspections);
        Assert.Equal(AppSettingsService.DefaultRowLimitValue, reloaded.DefaultRowLimit);
        Assert.True(reloaded.DefaultAutoCommit);
        Assert.Equal(AppSettingsService.DefaultLongRunNotifySeconds, reloaded.LongRunNotifySeconds);
        Assert.True(reloaded.ReconnectOnStart);
        Assert.False(reloaded.SoftwareRendering);
        Assert.True(reloaded.IsEnabled(ExperimentalFeature.CostLens));
        Assert.Equal(400, reloaded.NavigatorWidth);
        Assert.Equal(["toggle-reconnect"], reloaded.RecentCommands);
    }

    [Theory]
    [InlineData(9, 10, false)]
    [InlineData(10, 10, true)]
    [InlineData(3, 2, true)]
    [InlineData(600, 0, false)]
    public void Long_run_notification_uses_the_preference(int elapsed, int threshold, bool expected) =>
        Assert.Equal(expected, RunNotification.ShouldNotify(TimeSpan.FromSeconds(elapsed), false, false, TimeSpan.FromSeconds(threshold)));

    [Fact]
    public void Turning_notifications_off_and_on_keeps_the_chosen_seconds()
    {
        var settings = Settings();
        var vm = Preferences(settings);
        vm.LongRunNotifySeconds = 30;
        Assert.Equal(30, settings.LongRunNotifySeconds);

        vm.NotifyLongRuns = false;
        Assert.Equal(0, settings.LongRunNotifySeconds);
        Assert.Equal(30, vm.LongRunNotifySeconds);

        vm.LongRunNotifySeconds = 40; // edited while off: remembered, not saved
        Assert.Equal(0, settings.LongRunNotifySeconds);
        vm.NotifyLongRuns = true;
        Assert.Equal(40, settings.LongRunNotifySeconds);
    }

    [Fact]
    public void The_window_follows_changes_made_elsewhere()
    {
        var settings = Settings();
        using var vm = Preferences(settings);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        settings.SetEditorFontSize(18);      // Ctrl+= in an editor
        settings.SetTheme(AppThemeMode.Dark); // the toolbar's theme menu
        settings.SetEnabled(ExperimentalFeature.ResultDiff, false); // a Lab menu

        Assert.Equal(18m, vm.EditorFontSize);
        Assert.Equal(AppThemeMode.Dark, vm.Theme);
        Assert.False(vm.ResultDiff);
        Assert.Contains(nameof(PreferencesViewModel.EditorFontSize), changed);
        Assert.Contains(nameof(PreferencesViewModel.Theme), changed);
        Assert.Contains(nameof(PreferencesViewModel.ResultDiff), changed);
    }

    [Fact]
    public void Editing_in_the_window_saves_at_once()
    {
        var settings = Settings();
        var vm = Preferences(settings);
        vm.Theme = AppThemeMode.Light;
        vm.EditorFontSize = 99; // clamped
        vm.DefaultRowLimit = 2500;
        vm.DefaultAutoCommit = false;
        vm.ReconnectOnStart = false;
        vm.CostLens = false;

        var reloaded = Settings();
        Assert.Equal(AppThemeMode.Light, reloaded.Theme);
        Assert.Equal(AppSettingsService.MaxEditorFontSize, reloaded.EditorFontSize);
        Assert.Equal(2500, reloaded.DefaultRowLimit);
        Assert.False(reloaded.DefaultAutoCommit);
        Assert.False(reloaded.ReconnectOnStart);
        Assert.False(reloaded.IsEnabled(ExperimentalFeature.CostLens));
    }

    [Fact]
    public void Software_rendering_says_a_restart_is_needed_only_when_it_differs_from_this_run()
    {
        var settings = Settings();
        var vm = Preferences(settings);
        Assert.False(vm.RestartNeeded);
        vm.SoftwareRendering = true;
        Assert.True(vm.RestartNeeded);
        vm.SoftwareRendering = false;
        Assert.False(vm.RestartNeeded);
    }

    [Fact]
    public async Task Reset_from_the_window_asks_then_resets()
    {
        var settings = Settings();
        settings.SetDefaultRowLimit(1);
        var vm = Preferences(settings);
        vm.LongRunNotifySeconds = 99;
        await vm.ResetCommand.ExecuteAsync(null);
        Assert.Equal(AppSettingsService.DefaultRowLimitValue, settings.DefaultRowLimit);
        Assert.Equal(AppSettingsService.DefaultLongRunNotifySeconds, (int)vm.LongRunNotifySeconds);
    }

    [Fact]
    public void Assistant_status_shows_whether_a_key_is_saved()
    {
        var assistant = new AssistantSettings(new AppPaths(_root), new NoProtector());
        using var vm = new PreferencesViewModel(Settings(), assistant, DesktopTabTests.DialogFake.Create(out _));
        Assert.StartsWith("No API key", vm.AssistantStatus);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        assistant.SetApiKey("sk-ant-api03-abcdefghijklmnop");
        Assert.StartsWith("API key saved: sk-ant-", vm.AssistantStatus);
        Assert.Contains(nameof(PreferencesViewModel.AssistantStatus), changed);
    }

    private sealed class NoProtector : ISecretProtector
    {
        public bool IsSupported => false;
        public string Protect(string plainText) => throw new NotSupportedException();
        public string? Unprotect(string protectedText) => null;
    }
}
