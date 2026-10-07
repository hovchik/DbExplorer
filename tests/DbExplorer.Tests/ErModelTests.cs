using DbExplorer.Application.Copy;
using DbExplorer.Application.Design;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.Modeling;
using DbExplorer.Core.Models;

namespace DbExplorer.Tests;

/// <summary>The editable ER model: reading it from a catalog, editing tables and relationships, saving it, and the
/// CREATE/ALTER script that makes a database match it.</summary>
public class ErModelTests
{
    private const string Ss = SqlDialect.SqlServerKey;
    private const string Pg = SqlDialect.PostgresKey;

    private static MetadataSnapshot Shop() => TestSnapshots.Shop();

    private static ErModel ShopModel(string provider = Ss) => ErModelReader.Read(Shop(), provider, "", schema: null);

    private static ModelTable T(ErModel model, string schema, string name) => model.FindByName(schema, name)!;

    private static ModelTable Drawn(string schema, string name, params ColumnDesign[] columns) => new()
    {
        Design = new TableDesign { Schema = schema, Name = name, Columns = columns }
    };

    private static ColumnDesign Key(string name = "Id") => new() { Name = name, Type = "int", IsPrimaryKey = true, IsIdentity = true, IsNullable = false };

    private static ColumnDesign Col(string name, string type = "int", bool nullable = false) => new() { Name = name, Type = type, IsNullable = nullable };

    // ----- Reading -----

    [Fact]
    public void Reads_every_table_with_its_keys_and_puts_parents_left_of_children()
    {
        var model = ShopModel();

        Assert.Equal(6, model.Tables.Count);
        var orders = T(model, "sales", "Orders");
        Assert.Equal(orders.Baseline, orders.Design);
        Assert.Contains(orders.Design.ForeignKeys, f => f.ReferencedTable == "Customers" && f.Column == "CustomerId");
        Assert.True(T(model, "dbo", "Customers").X < orders.X);
        Assert.True(orders.X < T(model, "sales", "OrderLines").X);
        Assert.Equal(3, model.Relationships().Count());
    }

    [Fact]
    public void Model_read_from_the_database_needs_no_script()
    {
        var script = ErModelScriptBuilder.Build(ShopModel(), Shop(), Ss);

        Assert.False(script.HasChanges);
        Assert.Equal(ErModelScriptBuilder.NothingToDo, script.Script);
        Assert.Equal(6, script.Unchanged);
    }

    [Fact]
    public void Diagram_has_a_box_per_table_and_a_curve_per_relationship()
    {
        var model = ShopModel().Add(Drawn("dbo", "Invoices", Key()));
        var (diagram, ids) = ErModelLayout.ToDiagram(model);

        Assert.Equal(7, diagram.Tables.Count);
        Assert.Equal(3, diagram.Edges.Count);
        var invoices = Assert.Single(diagram.Tables, t => t.Object.Name == "Invoices");
        Assert.True(invoices.IsFocus);
        Assert.Equal(model.FindByName("dbo", "Invoices")!.Id, ids[invoices]);
        Assert.True(diagram.Width > diagram.Tables.Max(t => t.X + t.Width));
    }

    // ----- Editing -----

    [Fact]
    public void Dragging_a_table_onto_another_adds_the_key_column_and_the_foreign_key()
    {
        var model = ShopModel().Add(Drawn("dbo", "Invoices", Key("InvoiceId")));
        var invoices = T(model, "dbo", "Invoices");

        model = model.LinkToTable(invoices.Id, null, T(model, "dbo", "Customers").Id)!;

        var design = T(model, "dbo", "Invoices").Design;
        var column = design.Column("CustomerId")!;
        Assert.Equal("int", column.Type);
        Assert.False(column.IsNullable);
        var fk = Assert.Single(design.ForeignKeys);
        Assert.Equal(("CustomerId", "dbo", "Customers", "CustomerId"), (fk.Column, fk.ReferencedSchema, fk.ReferencedTable, fk.ReferencedColumn));
    }

    [Fact]
    public void A_new_key_column_in_an_existing_table_is_nullable()
    {
        var model = ShopModel();
        model = model.LinkToTable(T(model, "dbo", "Audit").Id, null, T(model, "dbo", "Customers").Id)!;

        Assert.True(T(model, "dbo", "Audit").Design.Column("CustomerId")!.IsNullable);
    }

    [Fact]
    public void Dragging_a_column_onto_a_column_links_them_and_copies_a_missing_type()
    {
        var model = ShopModel().Add(Drawn("dbo", "Reviews", Key(), Col("ProductRef", type: "")));
        var reviews = T(model, "dbo", "Reviews");

        model = model.Link(reviews.Id, "ProductRef", T(model, "dbo", "Products").Id, "ProductId")!;

        var design = T(model, "dbo", "Reviews").Design;
        Assert.Equal("int", design.Column("ProductRef")!.Type);
        Assert.Single(design.ForeignKeys);
        Assert.Null(model.Link(reviews.Id, "ProductRef", T(model, "dbo", "Products").Id, "ProductId"));
    }

    [Theory]
    [InlineData("Customers", "Id", "Orders", "CustomerId")]
    [InlineData("customers", "id", "orders", "customer_id")]
    [InlineData("Customers", "CustomerNo", "Orders", "CustomerNo")]
    [InlineData("Categories", "Id", "Products", "CategoryId")]
    public void Names_the_new_key_column_after_the_parent(string parent, string key, string child, string expected)
    {
        var parentDesign = new TableDesign { Name = parent, Columns = [Key(key)] };
        var childDesign = new TableDesign { Name = child, Columns = [Key("Number")] };

        Assert.Equal(expected, ErModel.ForeignKeyColumnName(childDesign, parentDesign, parentDesign.Columns[0]));
    }

    [Fact]
    public void Renaming_a_table_or_a_key_column_carries_the_references_along()
    {
        var model = ShopModel();
        var customers = T(model, "dbo", "Customers");

        model = model.Update(customers.Id, customers.Design with { Name = "Clients" });
        model = model.RenameColumn(customers.Id, "CustomerId", "ClientId");

        var fk = Assert.Single(T(model, "sales", "Orders").Design.ForeignKeys, f => f.Column == "CustomerId");
        Assert.Equal(("Clients", "ClientId"), (fk.ReferencedTable, fk.ReferencedColumn));
        Assert.Equal(3, model.Relationships().Count());
    }

    [Fact]
    public void Removing_a_table_removes_the_keys_to_it_and_never_drops_it()
    {
        var model = ShopModel();
        model = model.Remove(T(model, "dbo", "Products").Id);

        Assert.DoesNotContain(T(model, "sales", "OrderLines").Design.ForeignKeys, f => f.ReferencedTable == "Products");
        var script = ErModelScriptBuilder.Build(model, Shop(), Ss);
        Assert.DoesNotContain("DROP TABLE", script.Script);
        Assert.Contains("DROP CONSTRAINT [FK_OrderLines_Products]", script.Script);
    }

    // ----- Script -----

    [Fact]
    public void New_tables_are_created_before_any_foreign_key_so_order_and_cycles_do_not_matter()
    {
        var model = ErModel.Empty(Ss)
            .Add(Drawn("dbo", "Employees", Key("EmployeeId"), Col("DepartmentId", nullable: true)))
            .Add(Drawn("dbo", "Departments", Key("DepartmentId"), Col("ManagerId", nullable: true)));
        model = model.Link(T(model, "dbo", "Employees").Id, "DepartmentId", T(model, "dbo", "Departments").Id, "DepartmentId")!;
        model = model.Link(T(model, "dbo", "Departments").Id, "ManagerId", T(model, "dbo", "Employees").Id, "EmployeeId")!;

        var script = ErModelScriptBuilder.Build(model, Shop(), Ss);

        Assert.True(script.CanRun, string.Join("; ", script.Errors));
        Assert.Equal(2, script.Created);
        var lastCreate = script.Script.LastIndexOf("CREATE TABLE", StringComparison.Ordinal);
        var firstKey = script.Script.IndexOf("FOREIGN KEY", StringComparison.Ordinal);
        Assert.True(lastCreate < firstKey);
        Assert.Contains("ALTER TABLE [dbo].[Employees] ADD CONSTRAINT [FK_Employees_Departments] FOREIGN KEY ([DepartmentId])", script.Script);
        Assert.Contains("ALTER TABLE [dbo].[Departments] ADD CONSTRAINT [FK_Departments_Employees] FOREIGN KEY ([ManagerId])", script.Script);
        Assert.Contains("\nGO\n", script.Script);
    }

    [Fact]
    public void Changed_tables_get_alter_statements_and_untouched_ones_nothing()
    {
        var model = ShopModel();
        var products = T(model, "dbo", "Products");
        model = model.Update(products.Id, products.Design.AddColumn(Col("Price", "decimal(18,2)", nullable: true)));

        var script = ErModelScriptBuilder.Build(model, Shop(), Ss);

        Assert.Equal(1, script.Altered);
        Assert.Equal(5, script.Unchanged);
        Assert.Contains("ALTER TABLE [dbo].[Products] ADD [Price] decimal(18,2) NULL;", script.Script);
        Assert.DoesNotContain("Customers", script.Script);
    }

    [Fact]
    public void Re_pointing_a_key_at_a_renamed_table_drops_it_before_the_rename_and_adds_it_after()
    {
        var model = ShopModel().Add(Drawn("dbo", "Accounts", Key("CustomerId")));
        var customers = T(model, "dbo", "Customers");
        model = model.Update(customers.Id, customers.Design with { Name = "Clients" });
        var orders = T(model, "sales", "Orders");
        model = model.Update(orders.Id, orders.Design with { ForeignKeys = orders.Design.ForeignKeys.Select(f => f with { ReferencedTable = "Accounts" }).ToList() });

        var script = ErModelScriptBuilder.Build(model, Shop(), Ss).Script;

        var drop = script.IndexOf("ALTER TABLE [sales].[Orders] DROP CONSTRAINT [FK_Orders_Customers];", StringComparison.Ordinal);
        var rename = script.IndexOf("EXEC sp_rename N'[dbo].[Customers]', N'Clients';", StringComparison.Ordinal);
        var add = script.IndexOf("REFERENCES [dbo].[Accounts] ([CustomerId])", StringComparison.Ordinal);
        Assert.True(drop >= 0 && rename > drop && add > rename, script);
    }

    [Fact]
    public void Keys_to_a_renamed_table_or_key_column_are_left_alone_since_the_server_carries_them()
    {
        var model = ShopModel();
        var customers = T(model, "dbo", "Customers");
        model = model.Update(customers.Id, customers.Design with { Name = "Clients" });
        model = model.RenameColumn(customers.Id, "CustomerId", "ClientId");

        var script = ErModelScriptBuilder.Build(model, Shop(), Ss);

        Assert.Equal(1, script.Altered);
        Assert.DoesNotContain("FK_Orders_Customers", script.Script);
        Assert.Contains("N'ClientId', N'COLUMN'", script.Script);
    }

    [Fact]
    public void A_drawn_table_with_the_name_of_an_existing_one_is_an_error()
    {
        var model = ErModel.Empty(Ss).Add(Drawn("dbo", "Customers", Key()));

        var script = ErModelScriptBuilder.Build(model, Shop(), Ss);

        Assert.False(script.CanRun);
        Assert.Contains(script.Errors, e => e.Contains("already exists", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_tables_of_the_model_with_one_name_clash()
    {
        var model = ErModel.Empty(Ss).Add(Drawn("dbo", "Invoices", Key())).Add(Drawn("dbo", "Invoices", Key()));

        Assert.False(ErModelScriptBuilder.Build(model, Shop(), Ss).CanRun);
    }

    [Fact]
    public void A_key_drawn_again_by_hand_is_matched_to_the_existing_one()
    {
        var model = ShopModel();
        var orders = T(model, "sales", "Orders");
        var unnamed = orders.Design with { ForeignKeys = orders.Design.ForeignKeys.Select(f => f with { Name = "" }).ToList() };
        model = model.Update(orders.Id, unnamed);

        Assert.False(ErModelScriptBuilder.Build(model, Shop(), Ss).HasChanges);
    }

    [Fact]
    public void Generating_for_another_database_creates_what_it_lacks()
    {
        var model = ShopModel();
        var other = new MetadataSnapshot
        {
            Objects = Shop().Objects.Where(o => o.Name is "Customers").ToList(),
            Columns = Shop().Columns.Where(c => c.Table is "Customers").ToList(),
            Modules = [], ForeignKeys = [], Indexes = [], RefreshedAt = DateTimeOffset.Now
        };

        var script = ErModelScriptBuilder.Build(model, other, Ss);

        Assert.Equal(5, script.Created);
        Assert.Equal(1, script.Unchanged);
        Assert.Contains("IF SCHEMA_ID(N'sales') IS NULL", script.Script);
    }

    [Fact]
    public void Postgres_script_has_no_batch_separators_and_warns_about_types_of_another_engine()
    {
        var model = ErModel.Empty(Ss).Add(Drawn("public", "invoices", Key("id"), Col("total", "money")));

        var script = ErModelScriptBuilder.Build(model, new MetadataSnapshot
        {
            Objects = [], Columns = [], Modules = [], ForeignKeys = [], Indexes = [], RefreshedAt = DateTimeOffset.Now
        }, Pg);

        Assert.DoesNotContain("GO", script.Script);
        Assert.Contains("CREATE TABLE \"public\".\"invoices\"", script.Script);
        Assert.Contains(script.Warnings, w => w.Contains("SQL Server", StringComparison.Ordinal));
    }

    [Fact]
    public void Dropping_a_column_is_flagged_as_risky()
    {
        var model = ShopModel();
        var customers = T(model, "dbo", "Customers");
        model = model.Update(customers.Id, customers.Design with { Columns = customers.Design.Columns.Where(c => c.Name != "Email").ToList() });

        var script = ErModelScriptBuilder.Build(model, Shop(), Ss);

        Assert.Contains("DROP COLUMN [Email]", script.Script);
        Assert.NotEmpty(script.Warnings);
    }

    // ----- Context for the editor's suggestions -----

    [Fact]
    public void Editor_context_knows_the_model_tables_so_keys_to_them_are_valid()
    {
        var model = ErModel.Empty(Ss).Add(Drawn("dbo", "Departments", Key("DepartmentId")))
            .Add(Drawn("dbo", "Employees", Key("EmployeeId"), Col("DepartmentId")));
        model = model.Link(T(model, "dbo", "Employees").Id, "DepartmentId", T(model, "dbo", "Departments").Id, "DepartmentId")!;
        var employees = T(model, "dbo", "Employees");

        var review = TableDesignAdvisor.Review(employees.Design, ErModelContext.For(model, null, Ss, employees.Id));

        Assert.DoesNotContain(review, s => s.Severity == DesignSeverity.Error);
    }

    // ----- File -----

    [Fact]
    public void Saves_and_reads_back_the_same_model()
    {
        var model = ShopModel().Add(Drawn("dbo", "Invoices", Key(), Col("Note", "nvarchar(200)", nullable: true) with { Default = "N''" }));
        model = model.Move(model.Tables[0].Id, 400, 120);

        var read = ErModelFile.Read(ErModelFile.Write(model));

        Assert.Equal(model.ProviderKey, read.ProviderKey);
        Assert.Equal(model.Tables.Select(t => (t.Id, t.X, t.Y, t.Baseline is null)), read.Tables.Select(t => (t.Id, t.X, t.Y, t.Baseline is null)));
        Assert.Equal(ErModelFile.Write(model), ErModelFile.Write(read));
        Assert.False(ErModelScriptBuilder.Build(read with { Tables = read.Tables.Where(t => t.Baseline is not null).ToList() }, Shop(), Ss).HasChanges);
    }

    [Fact]
    public void Refuses_a_file_that_is_not_a_model()
    {
        Assert.Throws<FormatException>(() => ErModelFile.Read("{\"format\":\"something-else\"}"));
        Assert.Throws<FormatException>(() => ErModelFile.Read("not json"));
    }
}
