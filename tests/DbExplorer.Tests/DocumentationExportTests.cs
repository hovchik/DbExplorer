using System.Reflection;
using System.Text.RegularExpressions;
using DbExplorer.Application.Copy;
using DbExplorer.Application.Documentation;
using DbExplorer.Application.Metadata;
using DbExplorer.Core.Abstractions;
using DbExplorer.Core.Models;

namespace DbExplorer.Tests;

public class DocumentationExportTests
{
    private static readonly DateTimeOffset When = new(2026, 11, 2, 10, 30, 0, TimeSpan.Zero);

    /// <summary>The shop schema plus a view and procedure with source, a trigger, a sequence, a synonym and indexes.</summary>
    private static MetadataSnapshot Shop()
    {
        var shop = TestSnapshots.Shop();
        return new MetadataSnapshot
        {
            Objects =
            [
                .. shop.Objects,
                new DbObject { Schema = "sales", Name = "trg_Orders_Audit", Type = DbObjectType.Trigger },
                new DbObject { Schema = "dbo", Name = "OrderNumbers", Type = DbObjectType.Sequence },
                new DbObject { Schema = "dbo", Name = "Clients", Type = DbObjectType.Synonym }
            ],
            Columns = shop.Columns,
            Modules =
            [
                new DbModule { Schema = "sales", Name = "vOrderTotals", Type = DbObjectType.View, Definition = "CREATE VIEW sales.vOrderTotals AS SELECT 1 AS x" },
                new DbModule { Schema = "dbo", Name = "usp_GetCustomer", Type = DbObjectType.Procedure, Definition = "CREATE PROCEDURE dbo.usp_GetCustomer AS SELECT '<b>'" },
                new DbModule { Schema = "sales", Name = "trg_Orders_Audit", Type = DbObjectType.Trigger, Definition = "CREATE TRIGGER trg_Orders_Audit ..." }
            ],
            ForeignKeys = shop.ForeignKeys,
            Indexes =
            [
                new DbIndex { Schema = "sales", Table = "Orders", Name = "PK_Orders", Columns = "OrderId", IsPrimaryKey = true, IsUnique = true, Type = "CLUSTERED" },
                new DbIndex { Schema = "sales", Table = "Orders", Name = "IX_Orders_Date", Columns = "OrderDate", Type = "NONCLUSTERED", Filter = "[OrderDate] > '2020'" }
            ],
            RefreshedAt = When
        };
    }

    private static CatalogComments Comments() => new(
    [
        (null, null, null, "The shop's main database."),
        ("sales", null, null, "Orders and order lines"),
        ("dbo", "Customers", null, "People who buy things | including companies"),
        ("dbo", "Customers", "Email", "Where invoices go.\nMay be empty."),
        ("sales", "Orders", "CustomerId", "<script>alert(1)</script>"),
        ("dbo", "usp_GetCustomer", null, "Looks up one customer"),
        ("dbo", "Products", null, "   ")
    ]);

    private static Dictionary<DbObject, DbTableConstraints> Constraints(MetadataSnapshot snapshot) => new()
    {
        [snapshot.Objects.Single(o => o.Name == "Orders")] = new DbTableConstraints
        {
            Defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["OrderDate"] = "(getdate())" },
            Checks = [new DbCheckConstraint("CK_Orders_Date", "([OrderDate]>'2000-01-01')")]
        }
    };

    private static DatabaseDocument Doc(DocumentationOptions? options = null)
    {
        var snapshot = Shop();
        return DocumentationBuilder.Build(snapshot, "Shop", options ?? new DocumentationOptions(), Comments(), Constraints(snapshot), When);
    }

    [Fact]
    public void Builder_groups_objects_and_attaches_comments_keys_and_constraints()
    {
        var doc = Doc();

        Assert.Equal("Shop", doc.Title);
        Assert.Equal("The shop's main database.", doc.Comment);
        Assert.Equal("Orders and order lines", doc.SchemaComments["sales"]);
        Assert.Equal(["Audit", "Customers", "Order Notes", "Products", "OrderLines", "Orders"], doc.Tables.Select(t => t.Object.Name));
        Assert.Equal(["vOrderTotals"], doc.Views.Select(v => v.Object.Name));
        Assert.Equal(["usp_GetCustomer"], doc.Routines.Select(r => r.Object.Name));
        Assert.Single(doc.Triggers);
        Assert.Single(doc.Sequences);
        Assert.Single(doc.Synonyms);

        var customers = doc.Tables.Single(t => t.Object.Name == "Customers");
        Assert.Equal("People who buy things | including companies", customers.Comment);
        Assert.Equal("Where invoices go.\nMay be empty.", customers.Columns.Single(c => c.Column.Name == "Email").Comment);
        Assert.Equal(["sales.Orders"], customers.ReferencedBy.Select(f => $"{f.Schema}.{f.Table}"));
        Assert.Null(doc.Tables.Single(t => t.Object.Name == "Products").Comment); // blank comments are dropped

        var orders = doc.Tables.Single(t => t.Object.Name == "Orders");
        Assert.Equal(new DocReference("dbo", "Customers", "CustomerId"), orders.Columns.Single(c => c.Column.Name == "CustomerId").References);
        Assert.Equal("(getdate())", orders.Columns.Single(c => c.Column.Name == "OrderDate").Default);
        Assert.Equal(["PK_Orders", "IX_Orders_Date"], orders.Indexes.Select(i => i.Name));
        Assert.Equal("CK_Orders_Date", Assert.Single(orders.Checks).Name);

        Assert.StartsWith("CREATE VIEW", doc.Views.Single().Definition);
        Assert.Equal("Looks up one customer", doc.Routines.Single().Comment);
        Assert.NotNull(doc.Diagram);
        Assert.Equal(6, doc.Diagram!.Tables.Count);
        Assert.True(doc.MultipleSchemas);
    }

    [Fact]
    public void Options_leave_out_source_diagram_and_other_schemas()
    {
        var doc = Doc(new DocumentationOptions { Schema = "sales", IncludeSource = false, IncludeDiagram = false });

        Assert.Equal("Shop · sales", doc.Title);
        Assert.All(doc.Tables, t => Assert.Equal("sales", t.Object.Schema));
        Assert.Null(doc.Views.Single().Definition);
        Assert.All(doc.Triggers, t => Assert.Null(t.Definition));
        Assert.Null(doc.Diagram);
        Assert.Empty(doc.Routines);
        Assert.False(doc.MultipleSchemas);
    }

    [Fact]
    public void Server_wide_catalog_is_narrowed_to_the_chosen_database()
    {
        var doc = DocumentationBuilder.Build(TestSnapshots.TwoDatabases(), "Billing", new DocumentationOptions(), generatedAt: When);

        Assert.Equal(["Invoices", "Transactions"], doc.Tables.Select(t => t.Object.Name));
        Assert.Equal(["InvoiceId"], doc.Tables.Single(t => t.Object.Name == "Transactions").Columns.Select(c => c.Column.Name));
        Assert.Equal(["fn_InvoiceTotal"], doc.Routines.Select(r => r.Object.Name));
        Assert.Equal(2, doc.Diagram!.Tables.Count);
    }

    [Fact]
    public void Large_diagrams_are_capped_with_a_note()
    {
        var tables = Enumerable.Range(1, DocumentationBuilder.MaxDiagramTables + 5)
            .Select(i => new DbObject { Schema = "dbo", Name = $"T{i:000}", Type = DbObjectType.Table }).ToList();
        var snapshot = new MetadataSnapshot
        {
            Objects = tables, Modules = [], ForeignKeys = [], Indexes = [], RefreshedAt = When,
            Columns = tables.Select(t => new DbColumn { Schema = "dbo", Table = t.Name, Name = "Id", Ordinal = 1, DataType = "int", IsPrimaryKey = true }).ToList()
        };

        var doc = DocumentationBuilder.Build(snapshot, null, new DocumentationOptions(), generatedAt: When);

        Assert.Equal(DocumentationBuilder.MaxDiagramTables + 5, doc.Tables.Count);
        Assert.Equal(DocumentationBuilder.MaxDiagramTables, doc.Diagram!.Tables.Count);
        Assert.Contains(doc.Notes, n => n.Contains("5 more"));
    }

    [Fact]
    public void Markdown_has_sections_links_and_a_mermaid_diagram()
    {
        var md = DocumentationWriter.ToMarkdown(Doc() with { Engine = "SQL Server", ServerVersion = "16.0.1000", Connection = "dev" });

        Assert.StartsWith("# Shop\n", md);
        Assert.Contains("SQL Server 16.0.1000 · connection dev · generated 2026-11-02", md);
        Assert.Contains("> The shop's main database.", md);
        Assert.Contains("```mermaid\nerDiagram", md);
        Assert.Contains("<a id=\"table-dbo-order-notes\"></a>", md);
        Assert.Contains("### dbo.Order Notes", md);
        // Pipes in comments must not split the table row; line breaks become <br>.
        Assert.Contains("People who buy things \\| including companies", md);
        Assert.Contains("Where invoices go.<br>May be empty.", md);
        Assert.Contains("FK → [dbo.Customers](#table-dbo-customers) (CustomerId)", md);
        Assert.Contains("| Default |", md);
        Assert.Contains("`(getdate())`", md);
        Assert.Contains("- `CK_Orders_Date`: `([OrderDate]>'2000-01-01')`", md);
        Assert.Contains("```sql\nCREATE VIEW sales.vOrderTotals", md);
        Assert.Contains("\\<script\\>", md); // HTML in a comment stays text

        // Every in-page link points at an anchor the file defines.
        var anchors = Regex.Matches(md, "<a id=\"([^\"]+)\"></a>").Select(m => m.Groups[1].Value).ToHashSet();
        foreach (Match link in Regex.Matches(md, @"\]\(#([^)]+)\)"))
            Assert.Contains(link.Groups[1].Value, anchors);
    }

    [Fact]
    public void Html_is_self_contained_escaped_and_every_link_resolves()
    {
        var html = DocumentationWriter.ToHtml(Doc());

        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.Contains("SELECT &#39;&lt;b&gt;&#39;", html);
        Assert.Contains("Where invoices go.<br>May be empty.", html);
        Assert.Contains("<svg xmlns=\"http://www.w3.org/2000/svg\" class=\"er\"", html);
        Assert.DoesNotContain("http://", html.Replace("http://www.w3.org/2000/svg", ""));
        Assert.DoesNotContain("https://", html);
        Assert.Contains("prefers-color-scheme:dark", html);

        var ids = Regex.Matches(html, " id=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        foreach (Match link in Regex.Matches(html, "href=\"#([^\"]+)\""))
            Assert.Contains(link.Groups[1].Value, ids);
    }

    [Theory]
    [InlineData("PostgreSQL", "PostgreSQL 16.4 (Ubuntu 16.4-1) on x86_64-pc-linux-gnu, compiled by gcc 13.2.0, 64-bit", "PostgreSQL 16.4 ·")]
    [InlineData("SQL Server", "Microsoft SQL Server 2022 (RTM-CU12) (KB5033663) - 16.0.4120.1 (X64)", "Microsoft SQL Server 2022 ·")]
    [InlineData("MySQL", "8.0.36", "MySQL 8.0.36 ·")]
    public void Subtitle_shortens_the_server_version(string engine, string version, string expected)
    {
        var md = DocumentationWriter.ToMarkdown(Doc() with { Engine = engine, ServerVersion = version });
        Assert.Contains("\n" + expected, md);
    }

    [Fact]
    public void Empty_database_still_writes_a_page()
    {
        var empty = new MetadataSnapshot { Objects = [], Columns = [], Modules = [], ForeignKeys = [], Indexes = [], RefreshedAt = When };
        var doc = DocumentationBuilder.Build(empty, "Empty", new DocumentationOptions(), generatedAt: When);

        Assert.Null(doc.Diagram);
        Assert.Contains("no objects to document", DocumentationWriter.ToHtml(doc));
        Assert.Contains("no objects to document", DocumentationWriter.ToMarkdown(doc));
    }

    [Fact]
    public void Markdown_code_spans_survive_backticks()
    {
        var snapshot = new MetadataSnapshot
        {
            Objects = [new DbObject { Schema = "s", Name = "t", Type = DbObjectType.Table }],
            Columns = [new DbColumn { Schema = "s", Table = "t", Name = "a`b", Ordinal = 1, DataType = "enum('x`y')" }],
            Modules = [], ForeignKeys = [], Indexes = [], RefreshedAt = When
        };
        var md = DocumentationWriter.ToMarkdown(DocumentationBuilder.Build(snapshot, null, new DocumentationOptions(), generatedAt: When));

        Assert.Contains("``enum('x`y')``", md);
        Assert.Contains("a\\`b", md);
    }

    [Theory]
    [InlineData(SqlDialect.SqlServerKey, "MS_Description")]
    [InlineData(SqlDialect.PostgresKey, "pg_description")]
    [InlineData(SqlDialect.MySqlKey, "COLUMN_COMMENT")]
    public void Comment_queries_exist_for_every_engine(string provider, string expected) =>
        Assert.Contains(expected, CatalogComments.Sql(provider, "shop"));

    [Fact]
    public void MySql_comment_query_quotes_the_database_name()
    {
        var sql = CatalogComments.Sql(SqlDialect.MySqlKey, "o'brien\\db")!;
        Assert.Contains("TABLE_SCHEMA = 'o''brien\\\\db'", sql);
        Assert.Null(CatalogComments.Sql(SqlDialect.MySqlKey, null));
        Assert.Null(CatalogComments.Sql("Other", "x"));
    }

    [Fact]
    public void Comment_rows_map_nulls_and_db_nulls()
    {
        var comments = CatalogComments.FromRows(new QueryResultSet
        {
            Columns = ["schema", "object", "column", "comment"],
            Rows =
            [
                [DBNull.Value, DBNull.Value, DBNull.Value, "db"],
                ["dbo", "T", DBNull.Value, "table"],
                ["dbo", "T", "C", "column"],
                ["dbo", "U", DBNull.Value, DBNull.Value]
            ]
        });

        Assert.Equal(3, comments.Count);
        Assert.Equal("db", comments.Database);
        Assert.Equal("table", comments.ForObject("DBO", "t"));
        Assert.Equal("column", comments.ForColumn("dbo", "T", "c"));
        Assert.Null(comments.ForObject("dbo", "U"));
    }

    [Fact]
    public async Task Loading_reads_comments_and_constraints_and_notes_failures()
    {
        var provider = DocProvider.Create(failComments: true, failTable: "Audit");
        var progress = new List<string>();

        var doc = await DocumentationBuilder.LoadAsync(provider, Shop(), "Shop", new DocumentationOptions(), new SyncProgress(progress.Add));

        Assert.Contains(doc.Notes, n => n.StartsWith("Comments could not be read: no permission"));
        Assert.Contains(doc.Notes, n => n.StartsWith("Defaults and checks of 1 table(s) could not be read: boom"));
        Assert.Equal("(now())", doc.Tables.Single(t => t.Object.Name == "Customers").Columns.Single(c => c.Column.Name == "Name").Default);
        Assert.Contains("Reading defaults and checks (6 of 6)…", progress);

        var noConstraints = DocProvider.Create();
        var quick = await DocumentationBuilder.LoadAsync(noConstraints, Shop(), "Shop", new DocumentationOptions { IncludeConstraints = false });
        Assert.Equal(0, ((DocProvider)(object)noConstraints).ConstraintReads);
        Assert.Equal("People who buy things", quick.Tables.Single(t => t.Object.Name == "Customers").Comment);
    }

    [Fact]
    public async Task Loading_stops_when_cancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DocumentationBuilder.LoadAsync(DocProvider.Create(), Shop(), "Shop", new DocumentationOptions(), ct: cts.Token));
    }

    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    /// <summary>Answers the comments query and the per-table constraint reads; can fail either.</summary>
    public class DocProvider : DispatchProxy
    {
        private bool _failComments;
        private string? _failTable;
        public int ConstraintReads { get; private set; }

        public static IDatabaseProvider Create(bool failComments = false, string? failTable = null)
        {
            var proxy = Create<IDatabaseProvider, DocProvider>();
            var self = (DocProvider)(object)proxy;
            self._failComments = failComments;
            self._failTable = failTable;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            args ??= [];
            switch (method!.Name)
            {
                case "get_ProviderKey":
                    return SqlDialect.PostgresKey;
                case nameof(IDatabaseProvider.QueryReadOnlyAsync):
                    if (_failComments) return Task.FromException<QueryResultSet>(new InvalidOperationException("no permission\nmore detail"));
                    return Task.FromResult(new QueryResultSet
                    {
                        Columns = ["schema", "object", "column", "comment"],
                        Rows = [["dbo", "Customers", null, "People who buy things"]]
                    });
                case nameof(IDatabaseProvider.GetTableConstraintsAsync):
                    ConstraintReads++;
                    var table = (DbObject)args[0]!;
                    if (table.Name == _failTable) return Task.FromException<DbTableConstraints>(new InvalidOperationException("boom"));
                    return Task.FromResult(new DbTableConstraints
                    {
                        Defaults = table.Name == "Customers"
                            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Name"] = "(now())" }
                            : new Dictionary<string, string>()
                    });
                default:
                    throw new NotSupportedException(method.Name);
            }
        }
    }
}
