using DbExplorer.Application;
using DbExplorer.Application.Query;
using DbExplorer.Core.Abstractions;
using DbExplorer.Core.Models;
using DbExplorer.Desktop;
using DbExplorer.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace DbExplorer.Tests;

/// <summary>Which statement produced each result set (so it can be shown under it in the editor).</summary>
public sealed class InlineResultPlacementTests
{
    private static List<string> Owners(string script, int results) =>
        InlineResultPlacement.Place(script, results)?.Select(s => script[s.Start..s.End]).ToList()
        ?? throw new Xunit.Sdk.XunitException("not placed");

    [Fact]
    public void Each_query_gets_its_own_result()
    {
        const string script = "SELECT 1;\nSELECT 2 FROM t;\nSELECT 3;";
        Assert.Equal(["SELECT 1;", "SELECT 2 FROM t;", "SELECT 3;"], Owners(script, 3));
    }

    [Fact]
    public void Statements_that_return_nothing_are_skipped()
    {
        const string script = "CREATE TABLE t (id int);\nINSERT INTO t VALUES (1);\nUPDATE t SET id = 2;\nSELECT * FROM t;\nDROP TABLE t;";
        Assert.Equal(["SELECT * FROM t;"], Owners(script, 1));
    }

    [Fact]
    public void Blank_lines_separate_T_SQL_statements_without_semicolons()
    {
        const string script = "SELECT name FROM sys.tables\n\nSELECT name\nFROM sys.views";
        Assert.Equal(["SELECT name FROM sys.tables", "SELECT name\nFROM sys.views"], Owners(script, 2));
    }

    [Fact]
    public void A_blank_line_inside_a_statement_falls_back_to_semicolons()
    {
        const string script = "SELECT id\n\nFROM t;\nSELECT 2;";
        Assert.Equal(["SELECT id\n\nFROM t;", "SELECT 2;"], Owners(script, 2));
    }

    [Theory]
    [InlineData("INSERT INTO t (a) VALUES (1) RETURNING id;", StatementOutput.OneResult)]
    [InlineData("DELETE FROM t OUTPUT deleted.id WHERE a = 1;", StatementOutput.OneResult)]
    [InlineData("DELETE FROM t OUTPUT deleted.id INTO @gone WHERE a = 1;", StatementOutput.None)]
    [InlineData("WITH x AS (SELECT 1 AS id) DELETE FROM t USING x WHERE t.id = x.id;", StatementOutput.None)]
    [InlineData("WITH x AS (SELECT 1 AS id) SELECT * FROM x;", StatementOutput.OneResult)]
    [InlineData("SELECT 1 UNION ALL SELECT 2;", StatementOutput.OneResult)]
    [InlineData("SELECT * INTO backup FROM t;", StatementOutput.None)]
    [InlineData("SELECT 1 SELECT 2", StatementOutput.Unknown)]
    [InlineData("EXEC sp_who;", StatementOutput.Unknown)]
    [InlineData("CALL refresh_all();", StatementOutput.Unknown)]
    [InlineData("SET NOCOUNT ON;", StatementOutput.None)]
    [InlineData("BEGIN TRANSACTION;", StatementOutput.None)]
    [InlineData("BEGIN SELECT 1; SELECT 2; END", StatementOutput.Unknown)]
    [InlineData("CREATE PROCEDURE p AS SELECT 1;", StatementOutput.None)]
    [InlineData("SHOW TABLES;", StatementOutput.OneResult)]
    [InlineData("EXPLAIN SELECT * FROM t;", StatementOutput.OneResult)]
    [InlineData("(SELECT 1) UNION (SELECT 2);", StatementOutput.OneResult)]
    public void Tells_what_a_statement_gives_back(string statement, StatementOutput expected) =>
        Assert.Equal(expected, Assert.Single(InlineResultPlacement.Statements(statement)).Output);

    [Fact]
    public void One_procedure_call_takes_the_results_the_queries_leave_over()
    {
        const string script = "SELECT 1;\nEXEC report @year = 2026;\nSELECT 2;";
        Assert.Equal(["SELECT 1;", "EXEC report @year = 2026;", "EXEC report @year = 2026;", "SELECT 2;"], Owners(script, 4));
        Assert.Equal(["SELECT 1;", "SELECT 2;"], Owners(script, 2)); // the call returned nothing
    }

    [Fact]
    public void Nothing_is_placed_when_the_count_cannot_add_up()
    {
        Assert.Null(InlineResultPlacement.Place("EXEC a;\nEXEC b;", 2));       // which call returned which?
        Assert.Null(InlineResultPlacement.Place("SELECT 1;\nSELECT 2;", 3));   // more results than queries
        Assert.Null(InlineResultPlacement.Place("SELECT 1;\nSELECT 2;", 1));   // one failed quietly?
    }

    [Fact]
    public void Comments_alone_are_not_statements()
    {
        const string script = "-- checks\n\nSELECT 1; -- one\n/* two */\nSELECT 2;";
        Assert.Equal(2, InlineResultPlacement.Statements(script).Count);
        Assert.Equal(2, Owners(script, 2).Count);
    }

    [Fact]
    public void Assign_maps_results_in_order() =>
        Assert.Equal([0, 2, 2, 3],
            InlineResultPlacement.Assign([StatementOutput.OneResult, StatementOutput.None, StatementOutput.Unknown, StatementOutput.OneResult], 4));
}

/// <summary>A Query tab shows its results under the statements that produced them, one statement at a time.</summary>
public sealed class QueryTabInlineResultsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbx-inline-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Every SELECT of the script returns one row holding its own text; a script containing "boom" fails.</summary>
    private static IDatabaseProvider Echo(List<string>? ran = null) =>
        DesktopTabTests.ScriptedProvider.Create(new()
        {
            [nameof(IDatabaseProvider.ExecuteScriptAsync)] = args =>
            {
                var sql = (string)args[0]!;
                ran?.Add(sql);
                if (sql.Contains("boom")) throw new InvalidOperationException("boom");
                var sets = sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(s => s.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
                    .Select(s => new QueryResultSet { Columns = ["sql"], Rows = [[s]], TotalRowCount = 1 })
                    .ToList();
                return Task.FromResult(new QueryExecutionResult { ResultSets = sets });
            }
        });

    private ServiceProvider Services()
    {
        var services = App.ConfigureServices();
        services.AddSingleton(new AppPaths(_root));
        services.AddSingleton(DesktopTabTests.DialogFake.Create(out _));
        return services.BuildServiceProvider();
    }

    private static QueryViewModel Tab(ServiceProvider services, IDatabaseProvider provider)
    {
        var tab = services.GetRequiredService<QueryViewModel>();
        tab.Attach(DesktopTabTests.Session(provider));
        return tab;
    }

    private static string Under(QueryViewModel tab, InlineResult card)
    {
        var line = tab.Document.GetLineByOffset(card.Anchor.Offset);
        return tab.Document.GetText(line);
    }

    private static Task RunStatement(QueryViewModel tab, string statement)
    {
        var start = tab.Document.Text.IndexOf(statement, StringComparison.Ordinal);
        return tab.ExecuteCommand.ExecuteAsync(new QueryRun(statement, start));
    }

    [Fact]
    public Task A_script_shows_each_result_under_its_statement() => UiThread.RunAsync(async () =>
    {
        await using var services = Services();
        var tab = Tab(services, Echo());
        tab.Sql = "SELECT 1;\nUPDATE t SET a = 1;\nSELECT 2;";

        await tab.ExecuteCommand.ExecuteAsync(null);

        Assert.Equal(2, tab.ResultSets.Count);
        Assert.Equal(["SELECT 1;", "SELECT 2;"], tab.InlineResults.Select(c => Under(tab, c)));
        Assert.Same(tab.ResultSets[1], tab.InlineResults[1].Result);
        Assert.Equal("1 row", tab.InlineResults[0].Summary);
        Assert.True(tab.CanShowInGrid(tab.InlineResults[1]));
        Assert.True(tab.ShowInGrid(tab.InlineResults[1]));
        Assert.Same(tab.ResultSets[1], tab.SelectedResult);
    });

    [Fact]
    public Task Running_one_statement_keeps_the_cards_of_the_others() => UiThread.RunAsync(async () =>
    {
        await using var services = Services();
        var tab = Tab(services, Echo());
        tab.Sql = "SELECT 1;\nSELECT 2;";
        await tab.ExecuteCommand.ExecuteAsync(null);
        var first = tab.InlineResults[0];

        await RunStatement(tab, "SELECT 2;");

        Assert.Equal(2, tab.InlineResults.Count);
        Assert.Same(first, tab.InlineResults[0]);
        Assert.Equal("SELECT 2;", Under(tab, tab.InlineResults[1]));
        Assert.False(tab.CanShowInGrid(first)); // the grid now shows the second run only
        Assert.True(tab.CanShowInGrid(tab.InlineResults[1]));
    });

    [Fact]
    public Task Cards_follow_edits_and_go_with_their_statement() => UiThread.RunAsync(async () =>
    {
        await using var services = Services();
        var tab = Tab(services, Echo());
        tab.Sql = "SELECT 1;\nSELECT 2;";
        await tab.ExecuteCommand.ExecuteAsync(null);

        tab.Document.Insert(0, "-- checks\n");
        Assert.Equal(["SELECT 1;", "SELECT 2;"], tab.InlineResults.Select(c => Under(tab, c)));

        var second = tab.Document.Text.IndexOf("SELECT 2;", StringComparison.Ordinal);
        tab.Document.Remove(second - 1, "SELECT 2;".Length + 1);
        tab.PruneInlineResults();
        Assert.Equal("SELECT 1;", Under(tab, Assert.Single(tab.InlineResults)));
    });

    [Fact]
    public Task A_failed_run_removes_the_cards_of_its_statements() => UiThread.RunAsync(async () =>
    {
        await using var services = Services();
        var tab = Tab(services, Echo());
        tab.Sql = "SELECT 1;\nSELECT 'boom';";
        await RunStatement(tab, "SELECT 1;");
        Assert.Single(tab.InlineResults);

        await tab.ExecuteCommand.ExecuteAsync(null);

        Assert.Empty(tab.InlineResults);
    });

    [Fact]
    public Task Nothing_is_shown_in_the_editor_when_turned_off() => UiThread.RunAsync(async () =>
    {
        await using var services = Services();
        services.GetRequiredService<AppSettingsService>().SetInlineResults(false);
        var tab = Tab(services, Echo());
        tab.Sql = "SELECT 1;";

        await tab.ExecuteCommand.ExecuteAsync(null);

        Assert.Single(tab.ResultSets);
        Assert.Empty(tab.InlineResults);
    });

    [Fact]
    public Task Close_and_clear_remove_cards_but_not_results() => UiThread.RunAsync(async () =>
    {
        await using var services = Services();
        var tab = Tab(services, Echo());
        tab.Sql = "SELECT 1;\nSELECT 2;\nSELECT 3;";
        await tab.ExecuteCommand.ExecuteAsync(null);

        tab.CloseInlineResultCommand.Execute(tab.InlineResults[1]);
        Assert.Equal(2, tab.InlineResults.Count);
        tab.ClearInlineResults();
        Assert.Empty(tab.InlineResults);
        Assert.Equal(3, tab.ResultSets.Count);
    });

    [Fact]
    public void Summary_says_how_much_is_shown()
    {
        static InlineResult Card(int rows, long total, bool truncated = false, bool exact = true) => new()
        {
            Anchor = new AvaloniaEdit.Document.TextDocument("x").CreateAnchor(0),
            Title = "Result 1", At = DateTime.Now,
            Result = new ResultSetView("Result 1", ["a"], Enumerable.Range(0, rows).Select(i => new ResultRow([i])).ToList())
            { IsTruncated = truncated, TotalRowCount = total, TotalRowCountIsExact = exact }
        };
        Assert.Equal("No rows", Card(0, 0).Summary);
        Assert.Equal("3 rows", Card(3, 3).Summary);
        Assert.Equal("First 10 of 25 rows", Card(25, 25).Summary);
        Assert.Equal("First 10 of 1,001+ rows", Card(1000, 1001, truncated: true, exact: false).Summary);
    }
}
