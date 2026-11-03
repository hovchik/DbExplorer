using DbExplorer.Application;
using DbExplorer.Application.Assistant;
using DbExplorer.Application.Connections;
using DbExplorer.Application.Updates;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Tests;

/// <summary>The update check: version order, reading GitHub's release list, which release is offered and when the
/// start-up check runs.</summary>
public class UpdateCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbx-updates-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    public UpdateCheckTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private AppSettingsService Settings() => new(new AppPaths(_root));

    [Theory]
    [InlineData("1.0.0", "1.0.1")]
    [InlineData("1.0.9", "1.0.10")]
    [InlineData("1.9.0", "1.10.0")]
    [InlineData("1.2.0-rc1", "1.2.0")]
    [InlineData("1.2.0-rc2", "1.2.0-rc10")]
    [InlineData("1.2.0-beta", "1.2.0-rc1")]
    [InlineData("1.2.0-1", "1.2.0-alpha")]
    [InlineData("1.2.0-rc.1", "1.2.0-rc.1.1")]
    [InlineData("1.2.0", "1.2.0.1")]
    [InlineData("0.0.57", "1.0.0")]
    public void Versions_are_ordered_like_semver(string older, string newer)
    {
        Assert.True(AppVersion.Parse(older) < AppVersion.Parse(newer));
        Assert.True(AppVersion.Parse(newer) > AppVersion.Parse(older));
    }

    [Theory]
    [InlineData("v1.2.0", "1.2.0")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("1.2.0+abc123", "1.2.0")]
    [InlineData("1.2.0.0", "1.2.0")]
    public void Equal_versions_are_equal(string a, string b)
    {
        Assert.Equal(AppVersion.Parse(a), AppVersion.Parse(b));
        Assert.Equal(AppVersion.Parse(a).GetHashCode(), AppVersion.Parse(b).GetHashCode());
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("1.x")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1.2.0-")]
    [InlineData("1..2")]
    public void Non_versions_are_rejected(string text) => Assert.False(AppVersion.TryParse(text, out _));

    [Fact]
    public void Test_builds_are_recognised()
    {
        Assert.True(AppVersion.Parse("0.0.57").IsTestBuild);
        Assert.False(AppVersion.Parse("1.0.0").IsTestBuild);
        Assert.False(AppVersion.Parse("0.1.0").IsTestBuild);
    }

    private const string GitHubJson = """
        [
          { "tag_name": "v1.3.0-rc1", "name": "DB Explorer 1.3.0-rc1", "html_url": "https://github.com/hovchik/DbExplorer/releases/tag/v1.3.0-rc1",
            "draft": false, "prerelease": true, "published_at": "2026-11-01T10:00:00Z", "body": "Release candidate",
            "assets": [ { "name": "DbExplorer-Setup-1.3.0-rc1.exe", "browser_download_url": "https://github.com/x/rc1.exe", "size": 1000 } ] },
          { "tag_name": "v1.4.0", "name": "Not published yet", "html_url": "https://github.com/x", "draft": true, "prerelease": false, "assets": [] },
          { "tag_name": "v1.2.0", "name": "", "html_url": "https://github.com/hovchik/DbExplorer/releases/tag/v1.2.0",
            "draft": false, "prerelease": false, "published_at": "2026-10-20T10:00:00Z", "body": "## Changes",
            "assets": [
              { "name": "checksums.txt", "browser_download_url": "https://github.com/x/checksums.txt", "size": 10 },
              { "name": "DbExplorer-Setup-1.2.0.exe", "browser_download_url": "https://github.com/x/setup-1.2.0.exe", "size": 52428800 } ] },
          { "tag_name": "nightly", "name": "Nightly", "html_url": "https://github.com/x", "draft": false, "prerelease": true, "assets": [] },
          { "tag_name": "v1.1.0", "name": "DB Explorer 1.1.0", "html_url": "https://github.com/x/1.1.0", "draft": false, "prerelease": false }
        ]
        """;

    [Fact]
    public void GitHub_release_list_is_read_without_drafts_or_non_version_tags()
    {
        var releases = GitHubReleaseSource.Parse(GitHubJson);

        Assert.Equal(["1.3.0-rc1", "1.2.0", "1.1.0"], releases.Select(r => r.Version.Text));
        var stable = releases[1];
        Assert.Equal("DB Explorer 1.2.0", stable.Title);
        Assert.Equal("https://github.com/x/setup-1.2.0.exe", stable.InstallerUrl);
        Assert.Equal(52428800, stable.InstallerSize);
        Assert.False(stable.IsPreRelease);
        Assert.Equal("## Changes", stable.Notes);
        Assert.True(releases[0].IsPreRelease);
        Assert.Null(releases[2].InstallerUrl);
    }

    private sealed class FakeSource(Func<IReadOnlyList<ReleaseInfo>> releases) : IReleaseSource
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<ReleaseInfo>> GetReleasesAsync(CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(releases());
        }
    }

    private (UpdateChecker Checker, FakeSource Source, AppSettingsService Settings) Checker(string current, Func<IReadOnlyList<ReleaseInfo>>? releases = null)
    {
        var settings = Settings();
        var source = new FakeSource(releases ?? (() => GitHubReleaseSource.Parse(GitHubJson)));
        return (new UpdateChecker(source, settings, current, () => _now), source, settings);
    }

    [Fact]
    public async Task Newest_stable_release_is_offered()
    {
        var (checker, _, settings) = Checker("1.0.0");
        var result = await checker.CheckAsync(manual: false);

        Assert.Equal(UpdateCheckStatus.Available, result.Status);
        Assert.Equal("1.2.0", result.Release!.Version.Text);
        Assert.Equal(_now, settings.LastUpdateCheck);
        Assert.Equal("DB Explorer 1.2.0 is available (you have 1.0.0).", result.Describe());
    }

    [Fact]
    public async Task Pre_releases_are_offered_only_when_asked_or_when_running_one()
    {
        var (checker, _, settings) = Checker("1.2.0");
        Assert.Equal(UpdateCheckStatus.UpToDate, (await checker.CheckAsync(manual: false)).Status);

        settings.SetIncludePreReleaseUpdates(true);
        var result = await checker.CheckAsync(manual: false);
        Assert.Equal("1.3.0-rc1", result.Release!.Version.Text);

        var (rcUser, _, _) = Checker("1.3.0-beta2");
        Assert.Equal("1.3.0-rc1", (await rcUser.CheckAsync(manual: false)).Release!.Version.Text);
    }

    [Fact]
    public async Task Running_the_newest_version_is_up_to_date()
    {
        var (checker, _, _) = Checker("1.2.0");
        var result = await checker.CheckAsync(manual: true);
        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Null(result.Release);
        Assert.Equal("DB Explorer 1.2.0 is the latest version.", result.Describe());
    }

    [Fact]
    public async Task Skipped_version_is_quiet_at_start_but_shown_by_check_now_and_newer_ones_still_appear()
    {
        var releases = GitHubReleaseSource.Parse(GitHubJson).ToList();
        var (checker, _, settings) = Checker("1.0.0", () => releases);
        settings.SetSkippedUpdateVersion("1.2.0");

        Assert.Equal(UpdateCheckStatus.UpToDate, (await checker.CheckAsync(manual: false)).Status);
        Assert.Equal(UpdateCheckStatus.Available, (await checker.CheckAsync(manual: true)).Status);

        releases.Add(releases[1] with { Version = AppVersion.Parse("1.2.1"), Tag = "v1.2.1" });
        Assert.Equal("1.2.1", (await checker.CheckAsync(manual: false)).Release!.Version.Text);
    }

    [Fact]
    public async Task A_failed_read_is_reported_and_does_not_count_as_a_check()
    {
        var (checker, _, settings) = Checker("1.0.0", () => throw new HttpRequestException("No such host is known."));
        var result = await checker.CheckAsync(manual: true);

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Equal("Couldn't check for updates: No such host is known.", result.Describe());
        Assert.Null(settings.LastUpdateCheck);
    }

    [Fact]
    public async Task Start_up_check_runs_at_most_once_a_day_and_not_for_test_builds_or_when_off()
    {
        var (checker, _, settings) = Checker("1.0.0");
        Assert.True(checker.ShouldCheckAutomatically());

        await checker.CheckAsync(manual: false);
        Assert.False(checker.ShouldCheckAutomatically());
        _now = _now.AddHours(23);
        Assert.False(checker.ShouldCheckAutomatically());
        _now = _now.AddHours(1);
        Assert.True(checker.ShouldCheckAutomatically());

        // A clock that went backwards (the last check is "in the future") checks again rather than never.
        settings.SetLastUpdateCheck(_now.AddDays(10));
        Assert.True(checker.ShouldCheckAutomatically());

        settings.SetCheckForUpdates(false);
        Assert.False(checker.ShouldCheckAutomatically());

        var (testBuild, _, _) = Checker("0.0.57");
        Assert.False(testBuild.ShouldCheckAutomatically());
    }

    [Fact]
    public void Update_settings_survive_a_restart_and_reset()
    {
        var settings = Settings();
        Assert.True(settings.CheckForUpdates);
        Assert.False(settings.IncludePreReleaseUpdates);

        settings.SetCheckForUpdates(false);
        settings.SetIncludePreReleaseUpdates(true);
        settings.SetSkippedUpdateVersion("1.2.0");
        settings.SetLastUpdateCheck(_now);

        var reloaded = Settings();
        Assert.False(reloaded.CheckForUpdates);
        Assert.True(reloaded.IncludePreReleaseUpdates);
        Assert.Equal("1.2.0", reloaded.SkippedUpdateVersion);
        Assert.Equal(_now, reloaded.LastUpdateCheck);

        reloaded.ResetPreferences();
        Assert.True(reloaded.CheckForUpdates);
        Assert.False(reloaded.IncludePreReleaseUpdates);
        Assert.Null(reloaded.SkippedUpdateVersion);
        Assert.Equal(_now, reloaded.LastUpdateCheck);
    }

    [Fact]
    public async Task Preferences_check_now_shows_the_outcome()
    {
        var settings = Settings();
        var (checker, _, _) = Checker("1.0.0");
        var prefs = new PreferencesViewModel(settings, new AssistantSettings(new AppPaths(_root), new NoSecretProtector()),
            DesktopTabTests.DialogFake.Create(out _))
        {
            CurrentVersion = "1.0.0",
            CheckForUpdatesNow = async () => await checker.CheckAsync(manual: true)
        };
        Assert.Contains("Not checked yet", prefs.UpdateStatus);

        await prefs.CheckUpdatesNowCommand.ExecuteAsync(null);

        Assert.Equal("DB Explorer 1.2.0 is available (you have 1.0.0). See the bar at the top of the main window.", prefs.UpdateCheckResult);
        Assert.False(prefs.IsCheckingForUpdates);

        prefs.CheckForUpdates = false;
        Assert.False(settings.CheckForUpdates);
    }
}
