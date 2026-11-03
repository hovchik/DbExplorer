using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DbExplorer.Application.Updates;

/// <summary>A published release, as the update check sees it.</summary>
public sealed record ReleaseInfo(
    AppVersion Version,
    string Tag,
    string Title,
    string PageUrl,
    string? InstallerUrl,
    long? InstallerSize,
    DateTimeOffset? PublishedAt,
    string Notes,
    bool IsPreRelease);

/// <summary>Where releases come from; GitHub in the app, a fixed list in tests.</summary>
public interface IReleaseSource
{
    Task<IReadOnlyList<ReleaseInfo>> GetReleasesAsync(CancellationToken ct);
}

/// <summary>
/// Reads the public releases of hovchik/DbExplorer from the GitHub API: one anonymous HTTPS request, with nothing but
/// the app's version in the User-Agent. Draft releases are not visible to it, so a tag only reaches users once its
/// draft release is published.
/// </summary>
public sealed class GitHubReleaseSource(HttpClient http, string currentVersion, string repository = GitHubReleaseSource.DefaultRepository)
    : IReleaseSource
{
    public const string DefaultRepository = "hovchik/DbExplorer";

    /// <summary>The page users land on when the update check can't pick a release (it lists all of them).</summary>
    public static string ReleasesPage(string repository = DefaultRepository) => $"https://github.com/{repository}/releases";

    public async Task<IReadOnlyList<ReleaseInfo>> GetReleasesAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases?per_page=30");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("DbExplorer", SafeVersion(currentVersion)));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var reason = (int)response.StatusCode is 403 or 429
                ? "GitHub's limit for anonymous requests was reached; try again in an hour"
                : $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}";
            throw new HttpRequestException(reason, null, response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var releases = await JsonSerializer.DeserializeAsync<List<GitHubRelease>>(stream, cancellationToken: ct).ConfigureAwait(false);
        return Parse(releases ?? []);
    }

    /// <summary>Turns the API's release list into <see cref="ReleaseInfo"/>s, leaving out drafts and tags that aren't
    /// versions. The installer is the asset named DbExplorer-Setup-*.exe.</summary>
    public static IReadOnlyList<ReleaseInfo> Parse(string json) =>
        Parse(JsonSerializer.Deserialize<List<GitHubRelease>>(json) ?? []);

    private static IReadOnlyList<ReleaseInfo> Parse(IEnumerable<GitHubRelease> releases)
    {
        var result = new List<ReleaseInfo>();
        foreach (var r in releases)
        {
            if (r.Draft || r.TagName is null || !AppVersion.TryParse(r.TagName, out var version)) continue;
            var installer = r.Assets?.FirstOrDefault(a =>
                a.Name is { } n && n.StartsWith("DbExplorer-Setup", StringComparison.OrdinalIgnoreCase)
                                && n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            result.Add(new ReleaseInfo(
                version,
                r.TagName,
                string.IsNullOrWhiteSpace(r.Name) ? $"DB Explorer {version}" : r.Name.Trim(),
                r.HtmlUrl ?? "",
                installer?.BrowserDownloadUrl,
                installer?.Size,
                r.PublishedAt,
                r.Body ?? "",
                r.Prerelease || version.IsPreRelease));
        }

        return result;
    }

    private static string SafeVersion(string version) =>
        AppVersion.TryParse(version, out var v) ? v.Text : "0.0.0";

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
        [JsonPropertyName("size")] public long? Size { get; set; }
    }
}

public enum UpdateCheckStatus
{
    /// <summary>No release is newer than the running version.</summary>
    UpToDate,

    /// <summary><see cref="UpdateCheckResult.Release"/> is newer than the running version.</summary>
    Available,

    /// <summary>The releases couldn't be read (offline, proxy, rate limit); <see cref="UpdateCheckResult.Error"/> says why.</summary>
    Failed
}

public sealed record UpdateCheckResult(UpdateCheckStatus Status, AppVersion Current, ReleaseInfo? Release = null, string? Error = null)
{
    public string Describe() => Status switch
    {
        UpdateCheckStatus.Available => $"DB Explorer {Release!.Version} is available (you have {Current}).",
        UpdateCheckStatus.UpToDate => $"DB Explorer {Current} is the latest version.",
        _ => $"Couldn't check for updates: {Error}."
    };
}

/// <summary>
/// Decides whether a newer DB Explorer is out. It only reads the release list and never downloads or installs
/// anything by itself: the app offers the installer link and the user runs it.
/// </summary>
public sealed class UpdateChecker(IReleaseSource source, AppSettingsService settings, string currentVersion, Func<DateTimeOffset>? clock = null)
{
    /// <summary>How long after a check the automatic one at start-up waits before asking again.</summary>
    public static readonly TimeSpan AutomaticInterval = TimeSpan.FromHours(24);

    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);

    public AppVersion Current { get; } = AppVersion.TryParse(currentVersion, out var v) ? v : AppVersion.Parse("0.0.0");

    /// <summary>The check at start-up runs when it is turned on, the running app is a real release (not a 0.0.x test
    /// build) and the last check was at least a day ago.</summary>
    public bool ShouldCheckAutomatically()
    {
        if (!settings.CheckForUpdates || Current.IsTestBuild) return false;
        return settings.LastUpdateCheck is not { } last || _now() - last >= AutomaticInterval || last > _now();
    }

    /// <summary>Reads the releases and returns the newest one that is newer than the running version. Pre-releases
    /// count when Preferences says so, or when the running version is itself a pre-release. With
    /// <paramref name="manual"/> false, a version the user chose to skip is not offered again (a newer one still is).</summary>
    public async Task<UpdateCheckResult> CheckAsync(bool manual, CancellationToken ct = default)
    {
        IReadOnlyList<ReleaseInfo> releases;
        try
        {
            releases = await source.GetReleasesAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, Current, Error: Reason(ex).Trim().TrimEnd('.'));
        }

        settings.SetLastUpdateCheck(_now());
        var newest = Newest(releases, Current, settings.IncludePreReleaseUpdates);
        if (newest is null) return new UpdateCheckResult(UpdateCheckStatus.UpToDate, Current);
        if (!manual && AppVersion.TryParse(settings.SkippedUpdateVersion, out var skipped) && !(newest.Version > skipped))
            return new UpdateCheckResult(UpdateCheckStatus.UpToDate, Current);
        return new UpdateCheckResult(UpdateCheckStatus.Available, Current, newest);
    }

    /// <summary>The newest release above <paramref name="current"/>, or null.</summary>
    public static ReleaseInfo? Newest(IEnumerable<ReleaseInfo> releases, AppVersion current, bool includePreReleases)
    {
        var withPre = includePreReleases || current.IsPreRelease;
        return releases
            .Where(r => withPre || !r.IsPreRelease)
            .Where(r => r.Version > current)
            .OrderByDescending(r => r.Version)
            .FirstOrDefault();
    }

    private static string Reason(Exception ex) => ex switch
    {
        TaskCanceledException or TimeoutException => "GitHub didn't answer in time",
        HttpRequestException { StatusCode: null } h => h.InnerException?.Message ?? h.Message,
        JsonException => "GitHub's answer couldn't be read",
        _ => ex.Message
    };
}
