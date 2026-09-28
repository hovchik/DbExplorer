using DbExplorer.Application.Query;

namespace DbExplorer.Tests;

public class SqlCompletionContextTests
{
    private readonly SqlCompletionEngine _engine = new(TestSnapshots.Shop(), id => "[" + id + "]", "SqlServer");

    private CompletionResult At(string sqlWithMarker, bool explicitRequest = true)
    {
        var caret = sqlWithMarker.IndexOf('|');
        return _engine.Complete(sqlWithMarker.Remove(caret, 1), caret, explicitRequest, max: 100);
    }

    [Fact]
    public void After_from_only_table_sources_are_offered()
    {
        var items = At("SELECT * FROM |").Items;
        Assert.Contains(items, i => i.InsertText == "sales.Orders");
        Assert.DoesNotContain(items, i => i.Kind is CompletionKind.Keyword or CompletionKind.Column or CompletionKind.Function);
        Assert.DoesNotContain(items, i => i.Label == "usp_GetCustomer");
    }

    [Fact]
    public void After_join_related_tables_come_first_with_their_on_condition()
    {
        var first = At("SELECT * FROM sales.Orders o JOIN |").Items[0];
        Assert.Equal(CompletionKind.Join, first.Kind);
        Assert.Contains(new[] { "dbo.Customers c ON o.CustomerId = c.CustomerId", "sales.OrderLines ol ON ol.OrderId = o.OrderId" },
            s => s == first.InsertText);
    }

    [Fact]
    public void After_on_whole_foreign_key_conditions_are_offered()
    {
        var items = At("SELECT * FROM sales.Orders o JOIN dbo.Customers c ON |").Items;
        Assert.Equal("o.CustomerId = c.CustomerId", items[0].InsertText);
        Assert.Equal(CompletionKind.Join, items[0].Kind);
    }

    [Fact]
    public void Columns_are_qualified_when_several_tables_are_in_scope()
    {
        var items = At("SELECT Na| FROM sales.Orders o JOIN dbo.Customers c ON o.CustomerId = c.CustomerId", explicitRequest: false).Items;
        Assert.Equal("c.Name", items[0].InsertText);
    }

    [Fact]
    public void Where_clause_offers_condition_keywords_and_functions()
    {
        var items = At("SELECT * FROM dbo.Customers WHERE |").Items;
        Assert.Equal(CompletionKind.Column, items[0].Kind);
        Assert.Contains(items, i => i.Label == "IS NOT NULL");
        var fn = Assert.Single(At("SELECT * FROM dbo.Customers WHERE ISN|", explicitRequest: false).Items, i => i.Kind == CompletionKind.Function);
        Assert.Equal("ISNULL()", fn.InsertText);
        Assert.Equal("ISNULL(".Length, fn.CaretOffset);
    }

    [Fact]
    public void After_a_table_name_keywords_follow_and_typing_an_alias_stays_quiet()
    {
        var items = At("SELECT * FROM dbo.Customers |").Items;
        Assert.All(items, i => Assert.Equal(CompletionKind.Keyword, i.Kind));
        Assert.Contains(items, i => i.Label == "WHERE");
        Assert.Empty(At("SELECT Name AS Cu|", explicitRequest: false).Items);
    }

    [Fact]
    public void Insert_column_list_offers_all_insertable_columns()
    {
        var items = At("INSERT INTO dbo.Customers (|").Items;
        Assert.Equal("CustomerId, Name, Email", items[0].InsertText);
        Assert.Contains(items, i => i.Label == "Email" && i.Kind == CompletionKind.Column);
    }

    [Fact]
    public void Exec_offers_routines()
    {
        var items = At("EXEC |").Items;
        Assert.Equal("dbo.usp_GetCustomer", items[0].InsertText);
    }

    [Fact]
    public void Star_expands_into_the_column_list()
    {
        var sql = "SELECT o.* FROM sales.Orders o";
        var result = _engine.Complete(sql, "SELECT o.*".Length, explicitRequest: true);
        var item = Assert.Single(result.Items);
        Assert.Equal("o.OrderId, o.CustomerId, o.OrderDate", item.InsertText);
        Assert.Equal("SELECT ".Length, result.ReplaceStart);
    }

    [Fact]
    public void Statement_start_offers_snippets_and_word_start_matching_works()
    {
        Assert.Contains(At("|").Items, i => i.Kind == CompletionKind.Snippet && i.Label.StartsWith("SELECT TOP 100"));
        Assert.Contains(At("SELECT * FROM sales.Orders WHERE Dat|", explicitRequest: false).Items, i => i.Label == "OrderDate");
        Assert.Contains(At("SELECT * FROM ol|", explicitRequest: false).Items, i => i.Label == "OrderLines");
    }

    [Fact]
    public void Nothing_is_suggested_inside_strings_and_comments()
    {
        Assert.Empty(At("SELECT * FROM dbo.Customers WHERE Name = 'Cu|", explicitRequest: false).Items);
        Assert.Empty(At("-- Cu|", explicitRequest: false).Items);
    }

    [Fact]
    public void Ctes_and_variables_are_offered()
    {
        Assert.Contains(At("WITH recent AS (SELECT 1 AS x) SELECT * FROM |").Items, i => i.Label == "recent");
        Assert.Contains(At("DECLARE @since date; SELECT * FROM sales.Orders WHERE OrderDate > @si|", explicitRequest: false).Items,
            i => i.Label == "@since");
    }

    [Fact]
    public void A_select_list_ending_in_a_qualified_column_does_not_hide_the_from_clause()
    {
        const string sql = "-- note\nSELECT o.OrderId, o.OrderDate\nFROM sales.Orders o\nWHERE o.";
        var items = _engine.Complete(sql, sql.Length).Items;
        Assert.Equal(["OrderId", "CustomerId", "OrderDate"], items.Select(i => i.Label));
    }

    [Fact]
    public void Hover_describes_aliases_columns_and_objects()
    {
        const string sql = "SELECT o.OrderDate FROM sales.Orders o";
        Assert.StartsWith("sales.Orders.OrderDate  datetime2(7)", _engine.Describe(sql, "SELECT o.Ord".Length));
        Assert.StartsWith("alias o → Table sales.Orders", _engine.Describe(sql, sql.Length - 1));
        Assert.Contains("→ dbo.Customers", _engine.Describe(sql, "SELECT o.OrderDate FROM sales.Ord".Length));
    }
}

public class SqlScriptToolsTests
{
    [Fact]
    public void Statements_split_on_semicolons_go_and_blank_lines_but_not_inside_blocks()
    {
        const string sql = "SELECT 1;\nSELECT 2\n\nSELECT 3\nGO\nCREATE PROCEDURE p AS\nBEGIN\n  SELECT 4;\n\n  SELECT 5;\nEND\n";
        var parts = SqlScriptTools.SplitStatements(sql).Select(r => r.Of(sql)).ToList();
        Assert.Equal(["SELECT 1;", "SELECT 2", "SELECT 3", "CREATE PROCEDURE p AS\nBEGIN\n  SELECT 4;\n\n  SELECT 5;\nEND"], parts);
    }

    [Fact]
    public void Semicolons_in_strings_and_comments_do_not_split()
    {
        const string sql = "SELECT ';' AS a -- ; here\nFROM t;";
        Assert.Single(SqlScriptTools.SplitStatements(sql));
    }

    [Fact]
    public void Statement_at_caret_picks_the_surrounding_statement()
    {
        const string sql = "SELECT 1;\n\nSELECT *\nFROM t\nWHERE x = 1;\n";
        Assert.Equal("SELECT *\nFROM t\nWHERE x = 1;", SqlScriptTools.StatementAt(sql, sql.IndexOf("FROM", StringComparison.Ordinal))!.Value.Of(sql));
    }

    [Fact]
    public void Toggle_comments_round_trips()
    {
        const string lines = "  SELECT *\n  FROM t";
        var commented = SqlScriptTools.ToggleLineComments(lines);
        Assert.Equal("  -- SELECT *\n  -- FROM t", commented);
        Assert.Equal(lines, SqlScriptTools.ToggleLineComments(commented));
    }

    [Fact]
    public void Format_puts_clauses_and_list_items_on_their_own_lines()
    {
        var formatted = SqlScriptTools.Format(
            "select o.OrderId, c.Name, count(*) as n from sales.Orders o left join dbo.Customers c on c.CustomerId = o.CustomerId " +
            "where o.OrderDate between '2024-01-01' and '2024-12-31' and c.Name like 'A%' group by o.OrderId, c.Name order by n desc");
        Assert.Equal("""
            SELECT
                o.OrderId,
                c.Name,
                COUNT(*) AS n
            FROM sales.Orders o
            LEFT JOIN dbo.Customers c
                ON c.CustomerId = o.CustomerId
            WHERE o.OrderDate BETWEEN '2024-01-01' AND '2024-12-31'
                AND c.Name LIKE 'A%'
            GROUP BY
                o.OrderId,
                c.Name
            ORDER BY
                n DESC
            """, formatted);
    }

    [Fact]
    public void Format_keeps_strings_comments_and_procedural_code()
    {
        Assert.Contains("'select from where'", SqlScriptTools.Format("select 'select from where' as x"));
        const string proc = "create procedure p as\nbegin\n  select 1\nend";
        Assert.Equal("CREATE PROCEDURE p AS\nBEGIN\n  SELECT 1\nEND", SqlScriptTools.Format(proc));
    }

    [Fact]
    public void Lexer_handles_dialect_quoting()
    {
        var tokens = SqlLexer.Tokenize("SELECT [a]]b], \"c\", N'x''y', $f$ body; $f$, @v::int -- end").Where(t => !t.IsTrivia).ToList();
        Assert.Contains(tokens, t => t.Kind == SqlTokenKind.QuotedIdentifier && t.Identifier == "a]]b");
        Assert.Contains(tokens, t => t.Kind == SqlTokenKind.String && t.Text == "N'x''y'");
        Assert.Contains(tokens, t => t.Kind == SqlTokenKind.String && t.Text == "$f$ body; $f$");
        Assert.Contains(tokens, t => t.Kind == SqlTokenKind.Variable && t.Text == "@v");
        Assert.Contains(tokens, t => t.Kind == SqlTokenKind.Operator && t.Text == "::");
    }
}
