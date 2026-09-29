using DbExplorer.Application.Query;

namespace DbExplorer.Tests;

/// <summary>Suggestions on a server-level connection, where several databases have objects with the same name.</summary>
public class DatabaseScopedCompletionTests
{
    private static SqlCompletionEngine Engine(string? database = null)
    {
        var snapshot = TestSnapshots.TwoDatabases();
        return new SqlCompletionEngine(database is null ? snapshot : snapshot.ForDatabase(database), id => "[" + id + "]");
    }

    private static CompletionResult CompleteAtEnd(SqlCompletionEngine engine, string sql, bool explicitRequest = false) =>
        engine.Complete(sql, sql.Length, explicitRequest);

    [Fact]
    public void Picked_database_offers_only_its_own_objects()
    {
        var result = CompleteAtEnd(Engine("Billing"), "SELECT * FROM ", explicitRequest: true);

        var tables = result.Items.Where(i => i.Kind == CompletionKind.Table).Select(i => i.InsertText).ToList();
        Assert.Contains("dbo.Transactions", tables);
        Assert.Contains("dbo.Invoices", tables);
        Assert.DoesNotContain("dbo.Customers", tables);
        Assert.Contains(CompleteAtEnd(Engine("Billing"), "SELECT fn_").Items, i => i.Label == "fn_InvoiceTotal");
        Assert.DoesNotContain(CompleteAtEnd(Engine("Sales"), "SELECT fn_").Items, i => i.Label == "fn_InvoiceTotal");
    }

    [Fact]
    public void Columns_come_from_the_picked_databases_table()
    {
        var result = CompleteAtEnd(Engine("Sales"), "SELECT * FROM dbo.Transactions t WHERE t.");

        Assert.Equal(["SaleId"], result.Items.Select(i => i.Label));
    }

    [Fact]
    public void Same_named_tables_of_every_database_are_kept_apart_when_none_is_picked()
    {
        var result = CompleteAtEnd(Engine(), "SELECT * FROM Trans");

        var inserts = result.Items.Where(i => i.Label == "Transactions").Select(i => i.InsertText).Order().ToList();
        Assert.Equal(["Billing.dbo.Transactions", "Sales.dbo.Transactions"], inserts);
    }

    [Fact]
    public void Three_part_names_resolve_to_the_named_database()
    {
        var result = CompleteAtEnd(Engine(), "SELECT * FROM Billing.dbo.Transactions t WHERE t.");

        Assert.Equal(["InvoiceId"], result.Items.Select(i => i.Label));
    }

    [Fact]
    public void Database_dot_lists_its_schemas_and_database_schema_dot_its_objects()
    {
        var engine = Engine();

        Assert.Contains(CompleteAtEnd(engine, "SELECT * FROM Sales.").Items, i => i.Kind == CompletionKind.Schema && i.Label == "dbo");
        var objects = CompleteAtEnd(engine, "SELECT * FROM Sales.dbo.").Items.Select(i => i.Label).Order().ToList();
        Assert.Equal(["Customers", "Transactions"], objects);
    }

    [Fact]
    public void Database_view_of_a_snapshot_keeps_only_that_database()
    {
        var snapshot = TestSnapshots.TwoDatabases();

        Assert.Equal(["Billing", "Sales"], snapshot.Databases);
        var billing = snapshot.ForDatabase("billing");
        Assert.All(billing.Objects, o => Assert.Equal("Billing", o.Database));
        Assert.All(billing.Columns, c => Assert.Equal("Billing", c.Database));
        Assert.Single(billing.ForeignKeys);
        Assert.True(snapshot.ContainsDatabase("SALES"));
        Assert.False(snapshot.ContainsDatabase("Other"));
    }
}
