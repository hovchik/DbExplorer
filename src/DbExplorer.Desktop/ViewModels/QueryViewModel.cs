using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Query;
using DbExplorer.Application.Sessions;
using DbExplorer.Desktop.Services;

namespace DbExplorer.Desktop.ViewModels;

public partial class QueryViewModel(QueryExecutionService queryService, ScriptStore scripts, IDialogService dialogs)
    : ViewModelBase, ISessionAware
{
    private static readonly string[] SqlKeywords =
    [
        "SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "IN", "EXISTS", "BETWEEN", "LIKE", "IS", "NULL",
        "ORDER BY", "GROUP BY", "HAVING", "JOIN", "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL JOIN",
        "ON", "AS", "DISTINCT", "TOP", "LIMIT", "OFFSET", "UNION", "UNION ALL", "INSERT INTO", "VALUES",
        "UPDATE", "SET", "DELETE FROM", "CREATE TABLE", "ALTER TABLE", "DROP TABLE", "CREATE INDEX",
        "CREATE PROCEDURE", "CREATE FUNCTION", "BEGIN", "END", "IF", "ELSE", "DECLARE", "CAST", "CONVERT",
        "COUNT", "SUM", "AVG", "MIN", "MAX", "CASE", "WHEN", "THEN", "OVER", "PARTITION BY", "WITH"
    ];

    /// <summary>Matches identifiers (optionally schema-qualified) that follow FROM/JOIN/UPDATE/INTO.</summary>
    private static readonly Regex TableReferenceRegex = new(
        @"\b(?:FROM|JOIN|UPDATE|INTO)\s+(?:(?<schema>[A-Za-z_][\w]*)\s*\.\s*)?(?<table>[A-Za-z_][\w]*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private DatabaseSession? _session;
    private IReadOnlyList<string> _allNames = [];
    private ILookup<string, string> _columnsByTable = Enumerable.Empty<string>().ToLookup(x => x);

    [ObservableProperty] private string _sql = "";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private IReadOnlyList<ResultSetView> _resultSets = [];

    public ObservableCollection<string> Messages { get; } = [];

    public ObservableCollection<string> Suggestions { get; } = [];

    public void Attach(DatabaseSession? session)
    {
        _session = session;
        ResultSets = [];
        Messages.Clear();
        Status = "";
        ExecuteCommand.NotifyCanExecuteChanged();

        if (session is null)
        {
            _allNames = [];
            _columnsByTable = Enumerable.Empty<string>().ToLookup(x => x);
            return;
        }

        _allNames = session.Snapshot.Objects.Select(o => o.FullName)
            .Concat(session.Snapshot.Objects.Select(o => o.Name))
            .Concat(session.Snapshot.Columns.Select(c => c.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s)
            .ToList();

        // Indexed by table name (and schema.table) so WHERE/ON/SELECT clauses can suggest real columns
        // of the tables actually referenced in the current script.
        _columnsByTable = session.Snapshot.Columns
            .SelectMany(c => new[] { (Key: c.Table, c.Name), (Key: $"{c.Schema}.{c.Table}", c.Name) })
            .ToLookup(x => x.Key, x => x.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Suggestions for the word at the caret. SQL keywords always come first; when the caret is
    /// past a WHERE/ON/SELECT/AND/OR clause, columns of the tables referenced by FROM/JOIN in the
    /// current script are ranked above generic schema/column names.
    /// </summary>
    public IReadOnlyList<string> GetSuggestions(string prefix, string textBeforeCaret)
    {
        if (string.IsNullOrEmpty(prefix)) return [];

        var keywordMatches = SqlKeywords
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        var referencedTables = TableReferenceRegex.Matches(textBeforeCaret)
            .Select(m => m.Groups["table"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var contextualColumns = referencedTables
            .SelectMany(t => _columnsByTable[t])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(c => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        var genericMatches = _allNames
            .Where(s => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        return keywordMatches
            .Concat(contextualColumns)
            .Concat(genericMatches)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(15)
            .ToList();
    }

    private bool CanExecute => _session is not null && !IsRunning && !string.IsNullOrWhiteSpace(Sql);

    partial void OnSqlChanged(string value) => ExecuteCommand.NotifyCanExecuteChanged();

    partial void OnIsRunningChanged(bool value) => ExecuteCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanExecute))]
    private async Task ExecuteAsync()
    {
        if (_session is null) return;

        if (queryService.IsPotentiallyDestructive(Sql))
        {
            var proceed = await dialogs.ConfirmAsync(
                "This script may modify data or schema (INSERT/UPDATE/DELETE/DROP/ALTER/...). Run it anyway?",
                "Run anyway");
            if (!proceed) return;
        }

        IsRunning = true;
        Status = "Running…";
        Messages.Clear();
        var sql = Sql;
        try
        {
            var result = await queryService.ExecuteScriptAsync(_session, sql, database: null, timeoutSeconds: 60);

            ResultSets = result.ResultSets
                .Select((rs, i) => new ResultSetView(
                    $"Result set {i + 1}", rs.Columns, rs.Rows.Select(r => new ResultRow(r)).ToList()))
                .ToList();

            foreach (var m in result.Messages) Messages.Add(m);

            Status = $"Completed in {result.Elapsed.TotalMilliseconds:N0} ms" +
                      (result.RowsAffected > 0 ? $" · {result.RowsAffected} row(s) affected" : "") +
                      (result.ResultSets.Count > 0 ? $" · {result.ResultSets.Count} result set(s)" : "");

            await SafeAppendHistoryAsync(sql, succeeded: true, error: null);
        }
        catch (Exception ex)
        {
            Status = "Error: " + ex.Message;
            await SafeAppendHistoryAsync(sql, succeeded: false, error: ex.Message);
        }
        finally
        {
            IsRunning = false;
        }
    }

    private async Task SafeAppendHistoryAsync(string sql, bool succeeded, string? error)
    {
        try
        {
            await scripts.AppendHistoryAsync(new ScriptHistoryEntry
            {
                RanAt = DateTimeOffset.Now,
                Sql = sql,
                Succeeded = succeeded,
                Error = error
            });
        }
        catch
        {
            // History persistence is best-effort; failures here should not surface to the user.
        }
    }
}
