using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DbExplorer.Application.Query;

/// <summary>Why a version of a script was kept.</summary>
public enum LocalHistoryReason
{
    /// <summary>The tab ran (all or part of) its script.</summary>
    Run,

    /// <summary>The tab was saved to its .sql file.</summary>
    Saved,

    /// <summary>A .sql file was opened.</summary>
    Opened,

    /// <summary>The tab was closed; its last text is kept so it can be brought back.</summary>
    Closed,

    /// <summary>The text the tab had just before an older version was restored over it.</summary>
    BeforeRestore
}

/// <summary>One kept version of a script.</summary>
public sealed class LocalHistoryVersion
{
    public DateTimeOffset At { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public LocalHistoryReason Reason { get; set; }

    public string Text { get; set; } = "";

    /// <summary>The tab's title when the version was kept (a tab renames itself after the queries it runs).</summary>
    public string? Title { get; set; }
}

/// <summary>Every kept version of one script: one query tab, or one .sql file once a tab is saved to it.</summary>
public sealed class LocalHistoryDocument
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string? FilePath { get; set; }

    /// <summary>Newest first.</summary>
    public List<LocalHistoryVersion> Versions { get; set; } = [];

    [JsonIgnore] public DateTimeOffset LastChanged => Versions.Count == 0 ? DateTimeOffset.MinValue : Versions[0].At;
}

/// <summary>
/// Local history for scripts (like DataGrip's and SSMS 21's): every run, save, open and close of a query tab keeps
/// a version of its whole text on disk, so a query that was overwritten, closed without saving or lost to an
/// accident can be compared and brought back. Nothing leaves the machine.
/// <para>One JSON file per script under <c>local-history/</c>: a tab's own key until it is saved, then the file's
/// key (the tab's earlier versions move along). Identical consecutive versions are kept once. Versions older than
/// <see cref="MaxAge"/> or past <see cref="MaxVersionsPerScript"/> are dropped, and so are the oldest scripts past
/// <see cref="MaxScripts"/>. Writes go one at a time; a damaged file reads as empty.</para>
/// </summary>
public sealed class LocalHistoryStore(AppPaths paths)
{
    public const int MaxVersionsPerScript = 100;
    public const int MaxScripts = 300;

    /// <summary>Texts larger than this (generated dumps) are not kept: the history is for hand-written scripts.</summary>
    public const int MaxTextLength = 1_000_000;

    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Overridable clock, for tests.</summary>
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.Now;

    public string Folder => Path.Combine(paths.Root, "local-history");

    /// <summary>The key of a tab's history: its file's when it has one (so reopening the file finds it), else its own id.</summary>
    public static string KeyFor(Guid tabId, string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return "tab-" + tabId.ToString("N");
        var normalized = Path.GetFullPath(filePath);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) normalized = normalized.ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return "file-" + Convert.ToHexString(hash, 0, 12).ToLowerInvariant();
    }

    /// <summary>Keeps <paramref name="text"/> as the newest version of the script <paramref name="key"/>.</summary>
    /// <returns>False when nothing was added: blank or very large text, or the same text as the newest version.</returns>
    public Task<bool> RecordAsync(string key, string title, string? filePath, string text, LocalHistoryReason reason,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength) return Task.FromResult(false);
        return Locked(async () =>
        {
            var doc = await ReadAsync(key, ct) ?? new LocalHistoryDocument { Key = key };
            doc.Title = title;
            doc.FilePath = filePath;
            if (doc.Versions.Count > 0 && doc.Versions[0].Text == text) return false;
            doc.Versions.Insert(0, new LocalHistoryVersion { At = Now(), Reason = reason, Text = text, Title = title });
            Prune(doc);
            await WriteAsync(doc, ct);
            PruneScripts(keep: key);
            return true;
        }, ct);
    }

    /// <summary>The kept versions of one script, newest first; null when there are none.</summary>
    public Task<LocalHistoryDocument?> LoadAsync(string key, CancellationToken ct = default) =>
        Locked(() => ReadAsync(key, ct), ct);

    /// <summary>Every script with kept versions (open, closed and saved), most recently changed first.</summary>
    public Task<IReadOnlyList<LocalHistoryDocument>> LoadAllAsync(CancellationToken ct = default) =>
        Locked<IReadOnlyList<LocalHistoryDocument>>(async () =>
        {
            if (!Directory.Exists(Folder)) return [];
            var docs = new List<LocalHistoryDocument>();
            foreach (var file in Directory.EnumerateFiles(Folder, "*.json"))
                if (await ReadAsync(Path.GetFileNameWithoutExtension(file), ct) is { Versions.Count: > 0 } doc)
                    docs.Add(doc);
            return docs.OrderByDescending(d => d.LastChanged).ToList();
        }, ct);

    /// <summary>A tab was saved to a file (or to another file): its versions so far continue under the file's key.</summary>
    public Task MoveAsync(string fromKey, string toKey, string title, string? filePath, CancellationToken ct = default) =>
        fromKey == toKey ? Task.CompletedTask : Locked(async () =>
        {
            var from = await ReadAsync(fromKey, ct);
            if (from is null) return true;
            var to = await ReadAsync(toKey, ct) ?? new LocalHistoryDocument { Key = toKey };
            to.Title = title;
            to.FilePath = filePath;
            to.Versions = to.Versions.Concat(from.Versions).OrderByDescending(v => v.At).ToList();
            Prune(to);
            await WriteAsync(to, ct);
            TryDelete(PathFor(fromKey));
            return true;
        }, ct);

    /// <summary>Forgets every kept version (the user's "Clear local history").</summary>
    public Task ClearAsync(CancellationToken ct = default) =>
        Locked(() =>
        {
            if (Directory.Exists(Folder))
                foreach (var file in Directory.EnumerateFiles(Folder, "*.json")) TryDelete(file);
            return Task.FromResult(true);
        }, ct);

    private void Prune(LocalHistoryDocument doc)
    {
        var cutoff = Now() - MaxAge;
        // The newest version always stays, however old: it is the last copy of that script.
        doc.Versions = doc.Versions.Where((v, i) => i == 0 || v.At >= cutoff).Take(MaxVersionsPerScript).ToList();
    }

    /// <summary>Drops the scripts not changed for <see cref="MaxAge"/>, and the oldest ones past <see cref="MaxScripts"/>.</summary>
    private void PruneScripts(string keep)
    {
        try
        {
            var files = new DirectoryInfo(Folder).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            var cutoff = (Now() - MaxAge).UtcDateTime;
            for (var i = 0; i < files.Count; i++)
            {
                if (Path.GetFileNameWithoutExtension(files[i].Name) == keep) continue;
                if (i >= MaxScripts || files[i].LastWriteTimeUtc < cutoff) TryDelete(files[i].FullName);
            }
        }
        catch (IOException)
        {
            // Pruning is housekeeping; the next write tries again.
        }
    }

    private string PathFor(string key)
    {
        // Keys are ours ("tab-…" / "file-…"), but never let one reach outside the folder.
        if (key.Length == 0 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("Invalid local history key.", nameof(key));
        return Path.Combine(Folder, key + ".json");
    }

    private async Task<LocalHistoryDocument?> ReadAsync(string key, CancellationToken ct)
    {
        var file = PathFor(key);
        if (!File.Exists(file)) return null;
        try
        {
            await using var fs = File.OpenRead(file);
            var doc = await JsonSerializer.DeserializeAsync<LocalHistoryDocument>(fs, Json, ct);
            if (doc is null) return null;
            doc.Key = key;
            doc.Versions = doc.Versions.Where(v => v.Text is not null).OrderByDescending(v => v.At).ToList();
            return doc;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task WriteAsync(LocalHistoryDocument doc, CancellationToken ct)
    {
        Directory.CreateDirectory(Folder);
        await AtomicFile.WriteJsonAsync(PathFor(doc.Key), doc, Json, ct);
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async Task<T> Locked<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await action(); }
        finally { _gate.Release(); }
    }
}

/// <summary>One row of the local history's unified diff: a line kept, removed (only in the base) or added (only in the version).</summary>
public sealed record LocalHistoryDiffRow(DbExplorer.Core.Models.DiffLineKind Kind, string Text, int? Line)
{
    public string Sign => Kind switch
    {
        DbExplorer.Core.Models.DiffLineKind.Added => "+",
        DbExplorer.Core.Models.DiffLineKind.Removed => "−",
        _ => ""
    };

    public string LineText => Line?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
}

/// <summary>The diff the local history shows: from a base text (the tab's current text, or the previous version)
/// to a kept version, as one column of lines.</summary>
public static class LocalHistoryDiff
{
    /// <summary>Above this many lines on each side multiplied together the line diff (quadratic) is not attempted.</summary>
    public const long MaxCells = 16_000_000;

    /// <returns>The rows, the number of lines added and removed, or null rows when the texts are too large to compare.</returns>
    public static (IReadOnlyList<LocalHistoryDiffRow>? Rows, int Added, int Removed) Build(string baseText, string versionText, bool onlyChanges)
    {
        static int Lines(string s) => s.Length == 0 ? 0 : s.Count(c => c == '\n') + 1;
        if ((long)Lines(baseText) * Lines(versionText) > MaxCells) return (null, 0, 0);

        var diff = Compare.TextDiffer.Diff(baseText.Replace("\r\n", "\n"), versionText.Replace("\r\n", "\n"));
        var stats = Compare.TextDiffer.Statistics(diff);
        if (onlyChanges) diff = Compare.TextDiffer.CollapseUnchanged(diff);
        var rows = diff.Select(d => d.Kind switch
        {
            DbExplorer.Core.Models.DiffLineKind.Removed => new LocalHistoryDiffRow(d.Kind, d.LeftText ?? "", d.LeftLineNumber),
            DbExplorer.Core.Models.DiffLineKind.Skipped => new LocalHistoryDiffRow(d.Kind, d.LeftText ?? "", null),
            _ => new LocalHistoryDiffRow(d.Kind, d.RightText ?? "", d.RightLineNumber)
        }).ToList();
        return (rows, stats.Added, stats.Removed);
    }

    /// <summary>"Today 14:03", "Yesterday 09:12" or the date and time, for the version list.</summary>
    public static string When(DateTimeOffset at, DateTimeOffset now)
    {
        var local = at.ToLocalTime();
        var today = now.ToLocalTime().Date;
        var time = local.ToString("t", System.Globalization.CultureInfo.CurrentCulture);
        if (local.Date == today) return "Today " + time;
        if (local.Date == today.AddDays(-1)) return "Yesterday " + time;
        return local.ToString("g", System.Globalization.CultureInfo.CurrentCulture);
    }

    public static string Label(LocalHistoryReason reason) => reason switch
    {
        LocalHistoryReason.Run => "Run",
        LocalHistoryReason.Saved => "Saved",
        LocalHistoryReason.Opened => "Opened",
        LocalHistoryReason.Closed => "Closed",
        LocalHistoryReason.BeforeRestore => "Before restore",
        _ => reason.ToString()
    };

    /// <summary>The first non-blank line, trimmed, for the version list.</summary>
    public static string Preview(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length > 0) return t.Length > 90 ? t[..90] + "…" : t;
        }
        return "";
    }
}
