using DbExplorer.Application.Metadata;
using DbExplorer.Application.Query;
using DbExplorer.Core.Models;

namespace DbExplorer.Tests;

public class SqlSymbolsTests
{
    private static readonly SqlSymbolFinder Finder = new(TestSnapshots.Shop(), id => "[" + id + "]", "SqlServer");
    private static readonly SqlSymbolFinder Offline = new(null, id => "\"" + id + "\"", "Postgres");

    /// <summary>The caret goes right after the first character of the <paramref name="nth"/> (1-based) match of <paramref name="at"/>.</summary>
    private static SqlSymbol Symbol(string sql, string at, int nth = 1, SqlSymbolFinder? finder = null)
    {
        var lookup = (finder ?? Finder).SymbolAt(sql, Offset(sql, at, nth));
        Assert.True(lookup.Symbol is not null, lookup.Reason);
        return lookup.Symbol!;
    }

    private static int Offset(string sql, string at, int nth)
    {
        var index = -1;
        for (var i = 0; i < nth; i++)
        {
            index = sql.IndexOf(at, index + 1, StringComparison.Ordinal);
            Assert.True(index >= 0, $"'{at}' #{nth} not found");
        }
        return index + 1;
    }

    private static string Renamed(string sql, string at, string newName, int nth = 1, SqlSymbolFinder? finder = null)
    {
        var symbol = Symbol(sql, at, nth, finder);
        var rename = (finder ?? Finder).Rename(symbol, newName);
        Assert.True(rename.Error is null, rename.Error);
        return rename.ApplyTo(sql);
    }

    private static string? Reason(string sql, string at, int nth = 1, SqlSymbolFinder? finder = null) =>
        (finder ?? Finder).SymbolAt(sql, Offset(sql, at, nth)).Reason;

    private static List<string> Texts(string sql, SqlSymbol symbol) =>
        symbol.Occurrences.Select(o => sql.Substring(o.Start, o.Length)).ToList();

    // ----- Table aliases -----

    [Fact]
    public void Renames_an_alias_and_every_qualifier_that_uses_it()
    {
        const string sql = "SELECT o.OrderId, o.OrderDate FROM sales.Orders o WHERE o.CustomerId = 1 ORDER BY o.OrderDate";
        Assert.Equal(
            "SELECT ord.OrderId, ord.OrderDate FROM sales.Orders ord WHERE ord.CustomerId = 1 ORDER BY ord.OrderDate",
            Renamed(sql, "o.", "ord"));
    }

    [Fact]
    public void Renaming_from_the_definition_or_a_use_gives_the_same_result()
    {
        const string sql = "SELECT c.Name FROM dbo.Customers AS c JOIN sales.Orders o ON o.CustomerId = c.CustomerId";
        var fromUse = Renamed(sql, "c.Name", "cust");
        var fromDefinition = Renamed(sql, "c JOIN", "cust");
        Assert.Equal("SELECT cust.Name FROM dbo.Customers AS cust JOIN sales.Orders o ON o.CustomerId = cust.CustomerId", fromUse);
        Assert.Equal(fromUse, fromDefinition);
        var symbol = Symbol(sql, "c JOIN");
        Assert.Equal(SqlSymbolKind.TableAlias, symbol.Kind);
        Assert.Single(symbol.Occurrences, o => o.IsDefinition);
    }

    [Fact]
    public void Same_alias_in_another_statement_or_union_branch_is_left_alone()
    {
        const string sql = "SELECT o.OrderId FROM sales.Orders o;\nSELECT o.Name FROM dbo.Customers o";
        Assert.Equal("SELECT x.OrderId FROM sales.Orders x;\nSELECT o.Name FROM dbo.Customers o", Renamed(sql, "o.", "x"));

        const string union = "SELECT a.OrderId FROM sales.Orders a UNION ALL SELECT a.ProductId FROM dbo.Products a";
        Assert.Equal("SELECT a.OrderId FROM sales.Orders a UNION ALL SELECT p.ProductId FROM dbo.Products p", Renamed(union, "a.", "p", nth: 2));
    }

    [Fact]
    public void Statements_without_semicolons_keep_their_own_aliases()
    {
        const string sql = "SELECT o.OrderId FROM sales.Orders o\nSELECT o.Name FROM dbo.Customers o";
        Assert.Equal("SELECT o.OrderId FROM sales.Orders o\nSELECT c.Name FROM dbo.Customers c", Renamed(sql, "o.", "c", nth: 2));
    }

    [Fact]
    public void A_subquery_sees_the_outer_alias_unless_it_hides_it()
    {
        const string correlated = "SELECT c.Name FROM dbo.Customers c WHERE EXISTS (SELECT 1 FROM sales.Orders o WHERE o.CustomerId = c.CustomerId)";
        Assert.Equal(
            "SELECT k.Name FROM dbo.Customers k WHERE EXISTS (SELECT 1 FROM sales.Orders o WHERE o.CustomerId = k.CustomerId)",
            Renamed(correlated, "c.", "k"));

        const string hidden = "SELECT t.Name FROM dbo.Customers t WHERE t.CustomerId IN (SELECT t.CustomerId FROM sales.Orders t)";
        Assert.Equal(
            "SELECT t.Name FROM dbo.Customers t WHERE t.CustomerId IN (SELECT o.CustomerId FROM sales.Orders o)",
            Renamed(hidden, "t.CustomerId FROM", "o"));
    }

    [Fact]
    public void Sql_server_update_and_delete_by_alias_are_renamed_too()
    {
        const string update = "UPDATE c SET c.Name = 'x' FROM dbo.Customers c WHERE c.CustomerId = 1";
        Assert.Equal("UPDATE cu SET cu.Name = 'x' FROM dbo.Customers cu WHERE cu.CustomerId = 1", Renamed(update, "c.Name", "cu"));

        const string delete = "DELETE o FROM sales.Orders o WHERE o.OrderId = 1";
        Assert.Equal("DELETE ord FROM sales.Orders ord WHERE ord.OrderId = 1", Renamed(delete, "o FROM", "ord"));
    }

    [Fact]
    public void Subquery_and_function_aliases_can_be_renamed()
    {
        const string derived = "SELECT d.CustomerId FROM (SELECT CustomerId FROM sales.Orders) d WHERE d.CustomerId > 1";
        Assert.Equal("SELECT big.CustomerId FROM (SELECT CustomerId FROM sales.Orders) big WHERE big.CustomerId > 1", Renamed(derived, "d.", "big"));

        const string function = "SELECT g.x FROM generate_series(1, 3) AS g(x) WHERE g.x > 1";
        Assert.Equal("SELECT s.x FROM generate_series(1, 3) AS s(x) WHERE s.x > 1", Renamed(function, "g.x", "s", finder: Offline));
    }

    [Fact]
    public void Alias_names_in_strings_and_comments_are_not_touched()
    {
        const string sql = "SELECT o.OrderId, 'o.OrderId' AS label -- o.OrderId\nFROM sales.Orders o";
        Assert.Equal("SELECT x.OrderId, 'o.OrderId' AS label -- o.OrderId\nFROM sales.Orders x", Renamed(sql, "o.", "x"));
    }

    [Fact]
    public void Clashing_alias_is_refused_and_outer_name_warns()
    {
        const string sql = "SELECT o.OrderId FROM sales.Orders o JOIN dbo.Customers c ON c.CustomerId = o.CustomerId";
        var rename = Finder.Rename(Symbol(sql, "o."), "c");
        Assert.Contains("already", rename.Error);
        Assert.Empty(rename.Edits);

        const string nested = "SELECT c.Name FROM dbo.Customers c WHERE EXISTS (SELECT 1 FROM sales.Orders o WHERE o.CustomerId = c.CustomerId)";
        Assert.Null(Reason(nested, "o.CustomerId"));
        var shadow = Finder.Rename(Symbol(nested, "o.CustomerId"), "c");
        Assert.Null(shadow.Error);
        Assert.Contains("outer query", shadow.Warning);
    }

    [Fact]
    public void New_names_are_quoted_when_needed()
    {
        const string sql = "SELECT o.OrderId FROM sales.Orders o";
        Assert.Equal("SELECT [order].OrderId FROM sales.Orders [order]", Renamed(sql, "o.", "order"));
        Assert.Equal("SELECT [my alias].OrderId FROM sales.Orders [my alias]", Renamed(sql, "o.", "my alias"));
        Assert.Equal("SELECT x.OrderId FROM sales.Orders x", Renamed(sql, "o.", "[x]"));
        Assert.Contains("already its name", Finder.Rename(Symbol(sql, "o."), "o").Error);
        Assert.Equal("SELECT O.OrderId FROM sales.Orders O", Renamed(sql, "o.", "O"));
    }

    [Fact]
    public void Aliases_in_insert_select_merge_and_upsert_statements()
    {
        const string insert = "INSERT INTO dbo.Audit (Id) SELECT o.OrderId FROM sales.Orders o WHERE o.CustomerId = 2";
        Assert.Equal("INSERT INTO dbo.Audit (Id) SELECT x.OrderId FROM sales.Orders x WHERE x.CustomerId = 2", Renamed(insert, "o.OrderId", "x"));

        const string merge = "MERGE INTO dbo.Customers AS t USING dbo.Customers AS s ON t.CustomerId = s.CustomerId\n" +
                             "WHEN MATCHED THEN UPDATE SET t.Name = s.Name;";
        Assert.Equal("MERGE INTO dbo.Customers AS target USING dbo.Customers AS s ON target.CustomerId = s.CustomerId\n" +
                     "WHEN MATCHED THEN UPDATE SET target.Name = s.Name;", Renamed(merge, "t.", "target"));

        const string upsert = "INSERT INTO customers AS c (id, name) VALUES (1, 'x') ON CONFLICT (id) DO UPDATE SET name = EXCLUDED.name WHERE c.id = 1";
        Assert.Equal("INSERT INTO customers AS cu (id, name) VALUES (1, 'x') ON CONFLICT (id) DO UPDATE SET name = EXCLUDED.name WHERE cu.id = 1",
            Renamed(upsert, "c.id", "cu", finder: Offline));
    }

    [Fact]
    public void Statements_in_a_routine_body_without_semicolons_keep_their_aliases()
    {
        const string sql = "CREATE PROCEDURE dbo.p AS\nBEGIN\n  SELECT a.Name FROM dbo.Customers a\n  IF @@ROWCOUNT = 0\n    SELECT a.OrderId FROM sales.Orders a\nEND";
        Assert.Equal("CREATE PROCEDURE dbo.p AS\nBEGIN\n  SELECT a.Name FROM dbo.Customers a\n  IF @@ROWCOUNT = 0\n    SELECT o.OrderId FROM sales.Orders o\nEND",
            Renamed(sql, "a.OrderId", "o"));
    }

    [Fact]
    public void MySql_names_are_quoted_with_backticks()
    {
        var mysql = new SqlSymbolFinder(null, SqlSymbolFinder.QuoteFor("MySql"), "MySql");
        Assert.Equal("SELECT `select`.id FROM orders `select`", Renamed("SELECT o.id FROM orders o", "o.", "select", finder: mysql));
        Assert.Equal("SELECT x.id FROM orders x", Renamed("SELECT `o`.id FROM orders `o`", "`o`.", "x", finder: mysql));
    }

    // ----- CTEs -----

    [Fact]
    public void Renames_a_cte_where_it_is_defined_read_and_qualified()
    {
        const string sql = "WITH recent AS (SELECT * FROM sales.Orders)\nSELECT recent.OrderId FROM recent JOIN dbo.Customers c ON c.CustomerId = recent.CustomerId";
        Assert.Equal(
            "WITH latest AS (SELECT * FROM sales.Orders)\nSELECT latest.OrderId FROM latest JOIN dbo.Customers c ON c.CustomerId = latest.CustomerId",
            Renamed(sql, "recent.", "latest"));
        Assert.Equal(SqlSymbolKind.Cte, Symbol(sql, "recent AS").Kind);
    }

    [Fact]
    public void Cte_read_by_later_ctes_and_recursive_ctes_follow()
    {
        const string sql = "WITH a AS (SELECT 1 AS n), b AS (SELECT n FROM a) SELECT * FROM b, a";
        Assert.Equal("WITH first_step AS (SELECT 1 AS n), b AS (SELECT n FROM first_step) SELECT * FROM b, first_step", Renamed(sql, "a AS", "first_step"));

        const string recursive = "WITH RECURSIVE r (n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM r WHERE n < 5) SELECT n FROM r";
        Assert.Equal("WITH RECURSIVE nums (n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM nums WHERE n < 5) SELECT n FROM nums",
            Renamed(recursive, "r (n)", "nums", finder: Offline));
    }

    [Fact]
    public void Cte_with_an_aliased_use_keeps_the_alias()
    {
        const string sql = "WITH t AS (SELECT * FROM sales.Orders) SELECT x.OrderId FROM t x";
        Assert.Equal("WITH orders_cte AS (SELECT * FROM sales.Orders) SELECT x.OrderId FROM orders_cte x", Renamed(sql, "t AS", "orders_cte"));
    }

    [Fact]
    public void Cte_named_like_a_table_warns()
    {
        const string sql = "WITH t AS (SELECT 1 AS n) SELECT n FROM t";
        var rename = Finder.Rename(Symbol(sql, "t AS"), "Customers");
        Assert.Null(rename.Error);
        Assert.Contains("table named Customers", rename.Warning);
    }

    // ----- Output columns -----

    [Fact]
    public void Renames_a_column_alias_in_order_by_and_in_the_queries_that_read_it()
    {
        const string sql =
            "WITH totals AS (SELECT o.CustomerId, COUNT(*) AS order_count FROM sales.Orders o GROUP BY o.CustomerId)\n" +
            "SELECT c.Name, t.order_count FROM dbo.Customers c JOIN totals t ON t.CustomerId = c.CustomerId\n" +
            "WHERE t.order_count > 2 ORDER BY t.order_count DESC";
        var renamed = Renamed(sql, "order_count FROM", "orders");
        Assert.Equal(sql.Replace("order_count", "orders"), renamed);
        var symbol = Symbol(sql, ".order_count");
        Assert.True(4 == symbol.Occurrences.Count, string.Join(" | ", symbol.Occurrences.Select(o => o.Start + ":" + sql.Substring(o.Start, o.Length))));
    }

    [Fact]
    public void Bare_column_of_the_only_source_is_renamed_and_order_by_alias_too()
    {
        const string sql = "SELECT total FROM (SELECT OrderId * 2 AS total FROM sales.Orders) d WHERE total > 1 ORDER BY total";
        Assert.Equal("SELECT doubled FROM (SELECT OrderId * 2 AS doubled FROM sales.Orders) d WHERE doubled > 1 ORDER BY doubled",
            Renamed(sql, "total FROM", "doubled"));

        const string own = "SELECT OrderId * 2 AS total FROM sales.Orders ORDER BY total";
        Assert.Equal("SELECT OrderId * 2 AS t2 FROM sales.Orders ORDER BY t2", Renamed(own, "total FROM", "t2"));
    }

    [Fact]
    public void Column_passed_through_another_cte_is_followed()
    {
        const string sql =
            "WITH a AS (SELECT OrderId AS id FROM sales.Orders), b AS (SELECT a.id FROM a) SELECT b.id FROM b";
        Assert.Equal("WITH a AS (SELECT OrderId AS order_id FROM sales.Orders), b AS (SELECT a.order_id FROM a) SELECT b.order_id FROM b",
            Renamed(sql, "id FROM sales", "order_id"));
    }

    [Fact]
    public void Cte_column_list_entries_are_renamed_with_their_readers()
    {
        const string sql = "WITH t (customer, n) AS (SELECT CustomerId, COUNT(*) FROM sales.Orders GROUP BY CustomerId) SELECT t.customer, n FROM t";
        Assert.Equal("WITH t (cust, n) AS (SELECT CustomerId, COUNT(*) FROM sales.Orders GROUP BY CustomerId) SELECT t.cust, n FROM t",
            Renamed(sql, "customer,", "cust"));
    }

    [Fact]
    public void Alias_without_as_and_sql_server_assignment_style_are_found()
    {
        const string sql = "SELECT d.cnt FROM (SELECT COUNT(*) cnt FROM sales.Orders) d";
        Assert.Equal("SELECT d.n FROM (SELECT COUNT(*) n FROM sales.Orders) d", Renamed(sql, "cnt FROM", "n"));

        const string assign = "SELECT d.cnt FROM (SELECT cnt = COUNT(*) FROM sales.Orders) d";
        Assert.Equal("SELECT d.n FROM (SELECT n = COUNT(*) FROM sales.Orders) d", Renamed(assign, ".cnt", "n"));
    }

    [Fact]
    public void Clashing_output_column_is_refused()
    {
        const string sql = "SELECT d.a FROM (SELECT 1 AS a, 2 AS b) d";
        Assert.Contains("already a column", Finder.Rename(Symbol(sql, "a,"), "b").Error);
    }

    [Fact]
    public void Pass_through_column_explains_where_to_name_it()
    {
        const string sql = "WITH t AS (SELECT CustomerId FROM sales.Orders) SELECT t.CustomerId FROM t";
        Assert.Contains("Give it a name there", Reason(sql, "CustomerId FROM t"));
    }

    // ----- Variables and temp tables -----

    [Fact]
    public void Renames_a_variable_within_its_batch_only()
    {
        const string sql = "DECLARE @id int = 1;\nSELECT * FROM dbo.Customers WHERE CustomerId = @id;\nGO\nDECLARE @id int = 2;";
        Assert.Equal("DECLARE @customer int = 1;\nSELECT * FROM dbo.Customers WHERE CustomerId = @customer;\nGO\nDECLARE @id int = 2;",
            Renamed(sql, "@id", "customer"));
        Assert.Equal("DECLARE @c int = 1;\nSELECT * FROM dbo.Customers WHERE CustomerId = @c;\nGO\nDECLARE @id int = 2;",
            Renamed(sql, "@id", "@c"));
        Assert.Contains("already uses", Finder.Rename(Symbol("DECLARE @a int, @b int; SET @a = @b", "@a"), "b").Error);
        Assert.Contains("built-in", Reason("SELECT @@ROWCOUNT", "@@"));
    }

    [Fact]
    public void Renames_a_temp_table_across_the_script()
    {
        const string sql = "SELECT CustomerId INTO #ids FROM dbo.Customers;\nSELECT i.CustomerId FROM #ids i;\nDROP TABLE #ids;";
        Assert.Equal("SELECT CustomerId INTO #customer_ids FROM dbo.Customers;\nSELECT i.CustomerId FROM #customer_ids i;\nDROP TABLE #customer_ids;",
            Renamed(sql, "#ids", "customer_ids"));
        Assert.True(Symbol(sql, "#ids").Occurrences[0].IsDefinition);
    }

    // ----- Catalog objects and columns -----

    [Fact]
    public void Finds_a_table_in_sources_qualifiers_and_ddl_but_does_not_rename_it()
    {
        const string sql = "SELECT Customers.Name FROM dbo.Customers;\nUPDATE dbo.Customers SET Name = 'x';\nALTER TABLE dbo.Customers ADD Phone int;\nSELECT * FROM sales.Orders";
        var symbol = Symbol(sql, "Customers;");
        Assert.Equal(SqlSymbolKind.Object, symbol.Kind);
        Assert.Equal("table dbo.Customers", symbol.Title);
        Assert.False(symbol.CanRename);
        Assert.Equal(["Customers", "dbo.Customers", "dbo.Customers", "dbo.Customers"], Texts(sql, symbol));
        Assert.Contains("belongs to the database", Finder.Rename(symbol, "Clients").Error);
    }

    [Fact]
    public void Finds_a_column_through_aliases_bare_names_insert_lists_and_update_targets()
    {
        const string sql =
            "SELECT c.Name, o.OrderId FROM dbo.Customers c JOIN sales.Orders o ON o.CustomerId = c.CustomerId WHERE Name LIKE 'A%';\n" +
            "INSERT INTO dbo.Customers (CustomerId, Name) VALUES (1, 'x');\n" +
            "UPDATE dbo.Customers SET Name = 'y' WHERE CustomerId = 1;\n" +
            "SELECT p.Title AS Name FROM dbo.Products p ORDER BY Name";
        var symbol = Symbol(sql, "Name, o");
        Assert.Equal(SqlSymbolKind.Column, symbol.Kind);
        Assert.Equal("column dbo.Customers.Name", symbol.Title);
        Assert.Equal(4, symbol.Occurrences.Count);
        Assert.All(Texts(sql, symbol), t => Assert.Equal("Name", t));
        Assert.True(symbol.Occurrences[^1].Start < sql.IndexOf("SELECT p.", StringComparison.Ordinal));
    }

    [Fact]
    public void Ambiguous_bare_column_is_not_guessed()
    {
        const string sql = "SELECT CustomerId FROM dbo.Customers c JOIN sales.Orders o ON o.CustomerId = c.CustomerId";
        Assert.Contains("Qualify it", Reason(sql, "CustomerId FROM"));
    }

    [Fact]
    public void Without_a_catalog_tables_explain_that_a_connection_is_needed()
    {
        Assert.Contains("needs a connection", Reason("SELECT * FROM orders", "orders", finder: Offline));
        // Script names still work offline.
        Assert.Equal("SELECT x.id FROM orders x", Renamed("SELECT o.id FROM orders o", "o.", "x", finder: Offline));
    }

    // ----- Used by: views, routines and triggers -----

    private static SqlSymbolFinder WithModules(params DbModule[] modules)
    {
        var shop = TestSnapshots.Shop();
        var snapshot = new MetadataSnapshot
        {
            Objects = shop.Objects,
            Columns = shop.Columns,
            Modules = modules,
            ForeignKeys = shop.ForeignKeys,
            Indexes = shop.Indexes,
            RefreshedAt = shop.RefreshedAt
        };
        return new SqlSymbolFinder(snapshot, id => "[" + id + "]", "SqlServer");
    }

    private static DbModule Module(string schema, string name, DbObjectType type, string definition) =>
        new() { Schema = schema, Name = name, Type = type, Definition = definition };

    private static DbObject Table(string schema, string name) => TestSnapshots.Shop().Objects.First(o => o.Schema == schema && o.Name == name);

    [Fact]
    public void Finds_views_routines_and_triggers_that_use_a_table()
    {
        var finder = WithModules(
            Module("sales", "vOrderTotals", DbObjectType.View,
                "CREATE VIEW sales.vOrderTotals AS\nSELECT o.CustomerId, COUNT(*) AS n\nFROM sales.Orders o\nGROUP BY o.CustomerId"),
            Module("dbo", "usp_GetCustomer", DbObjectType.Procedure,
                "CREATE PROCEDURE dbo.usp_GetCustomer @id int AS\nBEGIN\n  -- reads sales.Orders too\n  SELECT * FROM dbo.Customers WHERE CustomerId = @id\nEND"),
            Module("sales", "trg_Orders_Audit", DbObjectType.Trigger,
                "CREATE TRIGGER sales.trg_Orders_Audit ON sales.Orders AFTER INSERT AS\nINSERT INTO dbo.Audit (Id) SELECT OrderId FROM inserted"),
            Module("dbo", "usp_Orders", DbObjectType.Procedure,
                "CREATE PROCEDURE dbo.usp_Orders AS SELECT 'sales.Orders' AS label"));

        var usages = finder.FindInModules(Table("sales", "Orders"));
        Assert.Equal(["trg_Orders_Audit", "vOrderTotals"], usages.Select(u => u.Module.Name).Distinct().Order());
        var view = usages.Single(u => u.Module.Name == "vOrderTotals");
        Assert.Equal(3, view.Line);
        Assert.Equal("FROM sales.Orders o", view.LineText);

        Assert.Single(finder.FindInModules(Table("dbo", "Customers")), u => u.Module.Name == "usp_GetCustomer");
    }

    [Fact]
    public void Finds_routines_that_use_a_column_and_skips_same_named_columns_of_other_tables()
    {
        var finder = WithModules(
            Module("sales", "vOrderTotals", DbObjectType.View,
                "CREATE VIEW sales.vOrderTotals AS SELECT o.CustomerId FROM sales.Orders o"),
            Module("dbo", "vCustomers", DbObjectType.View,
                "CREATE VIEW dbo.vCustomers AS SELECT CustomerId, Name FROM dbo.Customers"));

        var usages = finder.FindInModules(Table("dbo", "Customers"), "CustomerId");
        Assert.Equal("vCustomers", Assert.Single(usages).Module.Name);
        Assert.Equal("vOrderTotals", Assert.Single(finder.FindInModules(Table("sales", "Orders"), "CustomerId")).Module.Name);
        Assert.Empty(finder.FindInModules(Table("dbo", "Customers"), "Email"));
    }

    [Fact]
    public void Reads_postgres_function_bodies_inside_dollar_quotes()
    {
        var shop = TestSnapshots.Shop();
        var finder = new SqlSymbolFinder(new MetadataSnapshot
        {
            Objects = shop.Objects,
            Columns = shop.Columns,
            Modules =
            [
                Module("sales", "order_count", DbObjectType.Function,
                    "CREATE OR REPLACE FUNCTION sales.order_count(p_customer int) RETURNS bigint LANGUAGE plpgsql AS $function$\n" +
                    "BEGIN\n  RETURN (SELECT count(*) FROM Orders o WHERE o.CustomerId = p_customer);\nEND;\n$function$")
            ],
            ForeignKeys = [],
            Indexes = [],
            RefreshedAt = shop.RefreshedAt
        }, id => "\"" + id + "\"", "Postgres");

        var usage = Assert.Single(finder.FindInModules(Table("sales", "Orders")));
        Assert.Equal(3, usage.Line);
        Assert.Single(finder.FindInModules(Table("sales", "Orders"), "CustomerId"));
    }

    [Fact]
    public void Lists_foreign_keys_on_both_sides_of_a_table_or_column()
    {
        var orders = Table("sales", "Orders");
        Assert.Equal(["FK_OrderLines_Orders", "FK_Orders_Customers"], Finder.ForeignKeysOf(orders).Select(k => k.Name).Order());
        Assert.Equal("FK_Orders_Customers", Assert.Single(Finder.ForeignKeysOf(orders, "CustomerId")).Name);
    }
}

public class UsagesListTests
{
    [Fact]
    public void Script_rows_show_the_line_and_copy_as_text()
    {
        const string sql = "SELECT o.OrderId\nFROM sales.Orders o\nWHERE o.OrderId = 1";
        var symbol = new SqlSymbolFinder(TestSnapshots.Shop(), id => "[" + id + "]", "SqlServer").SymbolAt(sql, 8).Symbol!;
        var visited = new List<int>();
        var rows = DbExplorer.Desktop.ViewModels.UsagesViewModel.ScriptRows(sql, symbol.Occurrences, o => visited.Add(o.Start)).ToList();

        Assert.Equal("In this script · 3", rows[0].Text);
        Assert.Equal(["line 1", "line 2 · defined", "line 3"], rows.Skip(1).Select(r => r.Location));
        Assert.Equal("FROM sales.Orders o", rows[2].Text);
        rows[3].Activate!();
        Assert.Equal([sql.LastIndexOf("o.", StringComparison.Ordinal)], visited);

        var list = new DbExplorer.Desktop.ViewModels.UsagesViewModel("Usages of alias o", "3 places", rows);
        Assert.Contains("  line 2 · defined: FROM sales.Orders o", list.AsText());
    }
}
