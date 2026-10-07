using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DbExplorer.Application;
using DbExplorer.Application.Query;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>Editor conveniences of a query tab: the user's snippets, the shared zoom / word wrap, and the run clock.</summary>
public partial class QueryViewModel
{
    private readonly Stopwatch _runClock = new();
    private DispatcherTimer? _clockTimer;

    /// <summary>App-wide editor preferences (font size, word wrap) that every query tab follows.</summary>
    public AppSettingsService Settings { get; }

    /// <summary>The user's saved snippets, shared by every query tab.</summary>
    public SnippetLibrary Snippets { get; }

    /// <summary>"0:07" while a run is going, like the clock in the status bar of SSMS.</summary>
    [ObservableProperty] private string _runningElapsed = "";

    private void StartElapsedClock()
    {
        _runClock.Restart();
        RunningElapsed = FormatElapsed(TimeSpan.Zero);
        _clockTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background,
            (_, _) => RunningElapsed = FormatElapsed(_runClock.Elapsed));
        _clockTimer.Start();
    }

    private void StopElapsedClock()
    {
        _clockTimer?.Stop();
        _runClock.Stop();
        RunningElapsed = "";
    }

    public static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalHours >= 1
            ? elapsed.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : elapsed.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    /// <summary>Asks for a name and saves <paramref name="sql"/> as one of the user's snippets.</summary>
    public async Task SaveSnippetAsync(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            Status = "Select the SQL to save as a snippet first.";
            return;
        }

        var name = await dialogs.PromptTextAsync("Save snippet",
            "The snippet is offered by Ctrl+Space at the start of a statement and in the Snippets menu of every query tab.",
            "Name", SuggestSnippetName(sql), "e.g. Active customers");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (Snippets.Contains(name) &&
            !await dialogs.ConfirmAsync($"A snippet called \"{name.Trim()}\" already exists. Replace it?", "Replace"))
            return;

        try
        {
            Snippets.Save(name, sql);
            Status = $"Saved snippet \"{name.Trim()}\".";
        }
        catch (Exception ex)
        {
            Status = "Could not save the snippet: " + ex.Message;
        }
    }

    public async Task DeleteSnippetAsync(UserSnippet snippet)
    {
        if (!await dialogs.ConfirmAsync($"Delete the snippet \"{snippet.Name}\"?", "Delete")) return;
        try
        {
            Snippets.Remove(snippet.Name);
            Status = $"Deleted snippet \"{snippet.Name}\".";
        }
        catch (Exception ex)
        {
            Status = "Could not delete the snippet: " + ex.Message;
        }
    }

    /// <summary>The first line of the SQL, shortened: a starting point the user usually edits.</summary>
    private static string SuggestSnippetName(string sql)
    {
        var first = sql.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        first = first.TrimStart('-', ' ').Trim();
        return first.Length > 40 ? first[..40].TrimEnd() + "…" : first;
    }
}
