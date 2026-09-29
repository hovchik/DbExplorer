using DbExplorer.Application.Metadata;
using DbExplorer.Core.Models;

namespace DbExplorer.Tests;

/// <summary>A small shop schema: Customers ← Orders ← OrderLines → Products, plus an isolated Audit table.</summary>
internal static class TestSnapshots
{
    public static MetadataSnapshot Shop() => new()
    {
        Objects =
        [
            Table("dbo", "Customers", 100),
            Table("sales", "Orders", 1000),
            Table("sales", "OrderLines", 5000),
            Table("dbo", "Products", 50),
            Table("dbo", "Audit", 10),
            Table("dbo", "Order Notes", 3),
            new DbObject { Schema = "dbo", Name = "usp_GetCustomer", Type = DbObjectType.Procedure },
            new DbObject { Schema = "sales", Name = "vOrderTotals", Type = DbObjectType.View }
        ],
        Columns =
        [
            Col("dbo", "Customers", "CustomerId", 1, "int", pk: true),
            Col("dbo", "Customers", "Name", 2, "nvarchar(100)"),
            Col("dbo", "Customers", "Email", 3, "nvarchar(200)"),
            Col("sales", "Orders", "OrderId", 1, "int", pk: true),
            Col("sales", "Orders", "CustomerId", 2, "int"),
            Col("sales", "Orders", "OrderDate", 3, "datetime2(7)"),
            Col("sales", "OrderLines", "OrderId", 1, "int", pk: true),
            Col("sales", "OrderLines", "LineNo", 2, "int", pk: true),
            Col("sales", "OrderLines", "ProductId", 3, "int"),
            Col("dbo", "Products", "ProductId", 1, "int", pk: true),
            Col("dbo", "Products", "Title", 2, "nvarchar(100)"),
            Col("dbo", "Audit", "Id", 1, "int", pk: true),
            Col("dbo", "Order Notes", "Note Text", 1, "nvarchar(max)")
        ],
        Modules = [],
        ForeignKeys =
        [
            Fk("FK_Orders_Customers", "sales", "Orders", "CustomerId", "dbo", "Customers", "CustomerId"),
            Fk("FK_OrderLines_Orders", "sales", "OrderLines", "OrderId", "sales", "Orders", "OrderId"),
            Fk("FK_OrderLines_Products", "sales", "OrderLines", "ProductId", "dbo", "Products", "ProductId")
        ],
        Indexes = [],
        RefreshedAt = DateTimeOffset.Now
    };

    /// <summary>A server-level catalog: two databases that both have dbo.Transactions (with different columns), and a
    /// few tables that exist in only one of them.</summary>
    public static MetadataSnapshot TwoDatabases() => new()
    {
        Objects =
        [
            Table("dbo", "Transactions", 10) with { Database = "Sales" },
            Table("dbo", "Customers", 5) with { Database = "Sales" },
            Table("dbo", "Transactions", 20) with { Database = "Billing" },
            Table("dbo", "Invoices", 7) with { Database = "Billing" },
            new DbObject { Database = "Billing", Schema = "dbo", Name = "fn_InvoiceTotal", Type = DbObjectType.ScalarFunction }
        ],
        Columns =
        [
            Col("dbo", "Transactions", "SaleId", 1, "int", pk: true) with { Database = "Sales" },
            Col("dbo", "Customers", "CustomerId", 1, "int", pk: true) with { Database = "Sales" },
            Col("dbo", "Transactions", "InvoiceId", 1, "int", pk: true) with { Database = "Billing" },
            Col("dbo", "Invoices", "InvoiceId", 1, "int", pk: true) with { Database = "Billing" }
        ],
        Modules = [],
        ForeignKeys = [Fk("FK_Tx_Invoices", "dbo", "Transactions", "InvoiceId", "dbo", "Invoices", "InvoiceId") with { Database = "Billing" }],
        Indexes = [],
        RefreshedAt = DateTimeOffset.Now
    };

    private static DbObject Table(string schema, string name, long rows) =>
        new() { Schema = schema, Name = name, Type = DbObjectType.Table, RowCount = rows };

    private static DbColumn Col(string schema, string table, string name, int ordinal, string type, bool pk = false) =>
        new() { Schema = schema, Table = table, Name = name, Ordinal = ordinal, DataType = type, BaseType = type, IsPrimaryKey = pk };

    private static DbForeignKey Fk(string name, string schema, string table, string cols, string rs, string rt, string rcols) =>
        new()
        {
            Name = name, Schema = schema, Table = table, Columns = cols,
            ReferencedSchema = rs, ReferencedTable = rt, ReferencedColumns = rcols
        };
}
