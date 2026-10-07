using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Assistant;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>
/// The AI assistant panel: write SQL from a sentence, explain a query, fix the last error. Only the catalog's names and
/// types and the query text are sent (see <see cref="SqlAssistant"/>); the answer's SQL is inserted on request and never run.
/// </summary>
public partial class QueryViewModel
{
    private CancellationTokenSource? _assistantCts;

    /// <summary>The query and error of the last failed run in this tab, for "Fix the error".</summary>
    private (string Sql, string Error)? _lastError;

    public AssistantSettings AssistantSettings { get; }

    /// <summary>The assistant is hidden until the user has set an API key.</summary>
    public bool IsAssistantAvailable => AssistantSettings.IsConfigured;

    public bool IsAssistantSetupNeeded => !AssistantSettings.IsConfigured;

    [ObservableProperty] private bool _isAssistantOpen;
    [ObservableProperty] private string _assistantRequest = "";
    [ObservableProperty] private bool _isAssistantBusy;
    [ObservableProperty] private string _assistantStatus = "";

    /// <summary>The answer's prose (the proposed SQL is shown separately in <see cref="AssistantSql"/>).</summary>
    [ObservableProperty] private string _assistantAnswer = "";

    /// <summary>The SQL the assistant proposes; inserted into the editor only when the user asks.</summary>
    [ObservableProperty] private string? _assistantSql;

    public bool HasAssistantSql => !string.IsNullOrWhiteSpace(AssistantSql);
    public bool HasLastError => _lastError is not null;

    partial void OnAssistantSqlChanged(string? value) => OnPropertyChanged(nameof(HasAssistantSql));

    partial void OnIsAssistantBusyChanged(bool value)
    {
        AskAssistantCommand.NotifyCanExecuteChanged();
        ExplainWithAssistantCommand.NotifyCanExecuteChanged();
        FixWithAssistantCommand.NotifyCanExecuteChanged();
        CancelAssistantCommand.NotifyCanExecuteChanged();
    }

    partial void OnAssistantRequestChanged(string value) => AskAssistantCommand.NotifyCanExecuteChanged();

    /// <summary>The key was set or removed (the view listens to <see cref="AssistantSettings.Changed"/>).</summary>
    public void OnAssistantSettingsChanged()
    {
        OnPropertyChanged(nameof(IsAssistantAvailable));
        OnPropertyChanged(nameof(IsAssistantSetupNeeded));
        if (!IsAssistantAvailable) IsAssistantOpen = false;
    }

    private void RememberError(string sql, string? error)
    {
        _lastError = error is null ? null : (sql, error);
        OnPropertyChanged(nameof(HasLastError));
        FixWithAssistantCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task OpenAssistantSettingsAsync()
    {
        var wasConfigured = AssistantSettings.IsConfigured;
        await dialogs.EditAssistantSettingsAsync(AssistantSettings);
        if (!wasConfigured && AssistantSettings.IsConfigured) IsAssistantOpen = true;
    }

    [RelayCommand]
    private void ToggleAssistant() => IsAssistantOpen = IsAssistantAvailable && !IsAssistantOpen;

    private bool CanAsk => !IsAssistantBusy && !string.IsNullOrWhiteSpace(AssistantRequest);
    private bool CanAskAboutQuery => !IsAssistantBusy;
    private bool CanFix => !IsAssistantBusy && _lastError is not null;

    /// <summary>Writes SQL for the sentence in the request box, with the editor's text as context.</summary>
    [RelayCommand(CanExecute = nameof(CanAsk))]
    private Task AskAssistantAsync() =>
        AskAsync("Writing SQL…", (context, ct) => assistant.WriteAsync(context, AssistantRequest, Sql, ct));

    /// <summary>Explains the selection, the statement at the caret, or the whole script.</summary>
    [RelayCommand(CanExecute = nameof(CanAskAboutQuery))]
    private Task ExplainWithAssistantAsync(QueryRun? run)
    {
        var sql = RunText(run);
        if (string.IsNullOrWhiteSpace(sql))
        {
            IsAssistantOpen = true;
            AssistantStatus = "Write or select a query to explain.";
            return Task.CompletedTask;
        }
        return AskAsync("Explaining the query…", (context, ct) => assistant.ExplainAsync(context, sql, ct));
    }

    /// <summary>Asks why the last run failed and for a corrected query.</summary>
    [RelayCommand(CanExecute = nameof(CanFix))]
    private Task FixWithAssistantAsync()
    {
        if (_lastError is not { } failed) return Task.CompletedTask;
        return AskAsync("Looking at the error…", (context, ct) => assistant.FixAsync(context, failed.Sql, failed.Error, ct));
    }

    [RelayCommand]
    private async Task CopyAssistantSqlAsync()
    {
        if (AssistantSql is { Length: > 0 } sql) await dialogs.CopyTextAsync(sql);
    }

    [RelayCommand(CanExecute = nameof(IsAssistantBusy))]
    private void CancelAssistant() => _assistantCts?.Cancel();

    private async Task AskAsync(string busyText, Func<AssistantContext, CancellationToken, Task<AssistantAnswer>> ask)
    {
        IsAssistantOpen = true;
        if (!AssistantSettings.IsConfigured)
        {
            AssistantStatus = "Set your Anthropic API key first (AI ▾ › Settings).";
            return;
        }
        if (_session is not { } session)
        {
            AssistantStatus = "Connect to a database first: the assistant needs its tables and columns.";
            return;
        }

        _assistantCts?.Dispose();
        _assistantCts = new CancellationTokenSource();
        var ct = _assistantCts.Token;
        var context = new AssistantContext(session.Provider.ProviderKey, _completionSnapshot ?? session.Snapshot,
            TargetDatabase ?? NullIfEmpty(session.Profile.Database));
        IsAssistantBusy = true;
        AssistantStatus = busyText;
        try
        {
            var answer = await Task.Run(() => ask(context, ct), ct);
            AssistantSql = answer.Sql;
            AssistantAnswer = WithoutSql(answer.Text, answer.Sql);
            AssistantStatus = answer.Sql is null ? "" : "Review the SQL, then Insert it. Nothing is run until you run it.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            AssistantStatus = "Cancelled.";
        }
        catch (AssistantException ex)
        {
            AssistantStatus = ex.Message;
        }
        catch (Exception ex)
        {
            AssistantStatus = "The assistant failed: " + ex.Message;
        }
        finally
        {
            IsAssistantBusy = false;
        }
    }

    /// <summary>The prose of an answer with its SQL block taken out, since the SQL is shown in its own box.</summary>
    private static string WithoutSql(string text, string? sql)
    {
        if (sql is null) return text;
        var start = text.IndexOf("```", StringComparison.Ordinal);
        var end = start < 0 ? -1 : text.IndexOf("```", start + 3, StringComparison.Ordinal);
        if (start < 0 || end < 0) return text;
        return (text[..start].TrimEnd() + "\n\n" + text[(end + 3)..].TrimStart()).Trim();
    }
}
