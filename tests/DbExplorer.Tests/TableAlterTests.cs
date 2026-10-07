using DbExplorer.Application.Copy;
using DbExplorer.Application.Design;
using DbExplorer.Application.Metadata;
using DbExplorer.Core.Models;

namespace DbExplorer.Tests;

/// <summary>Opening an existing table in the Table designer: reading it, the ALTER script for each kind of change, and
/// the warnings for changes that risk existing data.</summary>
public class TableAlterTests
{
    private const string Ss = SqlDialect.SqlServerKey;
    private const string Pg = SqlDialect.PostgresKey;

    private static MetadataSnapshot Shop()
    {
        var shop = TestSnapshots.Shop();
        return new MetadataSnapshot
        {
            Objects = shop.Objects,
            Columns = shop.Columns,
            Modules = shop.Modules,
            ForeignKeys = shop.ForeignKeys,
            Indexes =
            [
                new DbIndex { Schema = "sales", Table = "Orders", Name = "PK_Orders", IsPrimaryKey = true, IsUnique = true, Type = "CLUSTERED", Columns = "OrderId" },
                new DbIndex { Schema = "sales", Table = "Orders", Name = "IX_Orders_CustomerId", Type = "NONCLUSTERED", Columns = "CustomerId" },
                new DbIndex { Schema = "sales", Table = "Orders", Name = "IX_Orders_Date", Type = "NONCLUSTERED", Columns = "OrderDate DESC" }
            ],
            RefreshedAt = shop.RefreshedAt
        };
    }

    private static readonly DbTableConstraints OrderDefaults = new()
    {
        Defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["OrderDate"] = "(sysutcdatetime())" }
    };

    private static DesignContext Context(string provider = Ss) => DesignContext.From(Shop(), provider);

    private static DbObject Table(string schema, string name) => Shop().Objects.Single(o => o.Schema == schema && o.Name == name);

    private static TableDesign Orders(string provider = Ss) =>
        TableDesignLoader.Load(Table("sales", "Orders"), Shop(), OrderDefaults, provider).Design;

    private static DesignSuggestion Find(IReadOnlyList<DesignSuggestion> suggestions, string key) =>
        Assert.Single(suggestions, s => s.Key == key);

    // ----- Reading the table -----

    [Fact]
    public void An_existing_table_is_read_with_its_keys_indexes_and_defaults()
    {
        var loaded = TableDesignLoader.Load(Table("sales", "Orders"), Shop(), OrderDefaults, Ss);
        var design = loaded.Design;

        Assert.Equal(("sales", "Orders", "PK_Orders"), (design.Schema, design.Name, design.PrimaryKeyName));
        Assert.Equal(["OrderId", "CustomerId", "OrderDate"], design.Columns.Select(c => c.Name));
        Assert.All(design.Columns, c => Assert.Equal(c.Name, c.OriginalName));
        Assert.True(design.Column("OrderId")!.IsPrimaryKey);
        Assert.Equal(("datetime2", "7", "sysutcdatetime()"), (design.Column("OrderDate")!.Type, design.Column("OrderDate")!.Size, design.Column("OrderDate")!.Default));
        var fk = Assert.Single(design.ForeignKeys);
        Assert.Equal(("FK_Orders_Customers", "CustomerId", "Customers", "CustomerId"), (fk.Name, fk.Column, fk.ReferencedTable, fk.ReferencedColumn));
        Assert.Equal("IX_Orders_CustomerId", Assert.Single(design.Indexes).Name);
        // A descending index has no row in the designer, so the script never touches it.
        Assert.Equal(["IX_Orders_Date"], loaded.KeptIndexes);
    }

    [Theory]
    [InlineData("((0))", "0")]
    [InlineData("(getdate())", "getdate()")]
    [InlineData("(N'new')", "N'new'")]
    [InlineData("((1)+(2))", "(1)+(2)")]
    public void Sql_server_defaults_lose_their_brackets(string stored, string shown) =>
        Assert.Equal(shown, TableDesignLoader.PlainDefault(stored, Ss));

    [Fact]
    public void An_unchanged_table_has_no_script_and_no_alter_warnings()
    {
        var design = Orders();
        Assert.Equal(TableAlterScriptBuilder.NoChanges, TableCreator.Script(design, Context(), design));
        var review = TableDesignAdvisor.Review(design, Context(), design);
        Assert.DoesNotContain(review, s => s.Key == "exists" || s.Key.StartsWith("alter-", StringComparison.Ordinal));
    }

    [Fact]
    public void Respelling_a_postgres_type_is_not_a_change()
    {
        var original = new TableDesign { Schema = "public", Name = "t", Columns = [new ColumnDesign { Name = "c", OriginalName = "c", Type = "character varying", Size = "100" }] };
        var edited = original.WithColumn("c", c => c with { Type = "varchar" });
        Assert.True(TableAlterScriptBuilder.IsUnchanged(original, edited, Pg));
    }

    // ----- Scripts -----

    [Fact]
    public void Sql_server_script_renames_changes_drops_and_adds_columns_in_batches()
    {
        var original = Orders();
        var edited = original
            .RenameColumn("OrderDate", "PlacedAt")
            .WithColumn("PlacedAt", c => c with { Default = null })
            .AddColumn(new ColumnDesign { Name = "Status", Type = "nvarchar", Size = "20", IsNullable = false, Default = "N'new'" });

        var script = TableAlterScriptBuilder.Build(original, edited, Ss);

        Assert.Contains("EXEC sp_rename N'[sales].[Orders].[OrderDate]', N'PlacedAt', N'COLUMN';\nGO\n", script);
        Assert.Contains("IF @df IS NOT NULL EXEC(N'ALTER TABLE [sales].[Orders] DROP CONSTRAINT ' + QUOTENAME(@df));\nGO\n", script);
        Assert.Contains("ALTER TABLE [sales].[Orders] ADD [Status] nvarchar(20) NOT NULL DEFAULT N'new';\nGO\n", script);
        Assert.True(script.IndexOf("sp_rename", StringComparison.Ordinal) < script.IndexOf("DROP CONSTRAINT", StringComparison.Ordinal));
        Assert.DoesNotContain("ALTER COLUMN", script);
    }

    [Fact]
    public void Dropping_a_column_drops_its_default_first_on_sql_server()
    {
        var original = Orders();
        var edited = original with { Columns = original.Columns.Where(c => c.Name != "OrderDate").ToList() };

        var steps = TableAlterScriptBuilder.Steps(original, edited, Ss);

        Assert.Equal(2, steps.Count);
        Assert.StartsWith("DECLARE @df sysname", steps[0]);
        Assert.Equal("ALTER TABLE [sales].[Orders] DROP COLUMN [OrderDate];", steps[1]);
        var warning = Find(TableDesignAdvisor.Review(edited, Context(), original), "alter-drop:OrderDate");
        Assert.Equal(DesignSeverity.Warning, warning.Severity);
        Assert.Equal(["OrderId", "CustomerId", "OrderDate"], warning.Fix!(edited).Columns.Select(c => c.Name));
    }

    [Fact]
    public void Sql_server_type_change_drops_and_restores_the_default_around_alter_column()
    {
        var original = Orders().WithColumn("OrderDate", c => c with { IsNullable = true });
        var edited = original.WithColumn("OrderDate", c => c with { Type = "datetimeoffset", Size = null, IsNullable = false });

        var steps = TableAlterScriptBuilder.Steps(original, edited, Ss);

        Assert.StartsWith("DECLARE @df", steps[0]);
        Assert.Equal("UPDATE [sales].[Orders] SET [OrderDate] = sysutcdatetime() WHERE [OrderDate] IS NULL;", steps[1]);
        Assert.Equal("ALTER TABLE [sales].[Orders] ALTER COLUMN [OrderDate] datetimeoffset NOT NULL;", steps[2]);
        Assert.Equal("ALTER TABLE [sales].[Orders] ADD DEFAULT sysutcdatetime() FOR [OrderDate];", steps[3]);
    }

    [Fact]
    public void A_changed_foreign_key_is_dropped_and_added_back_but_a_renamed_column_keeps_its_key()
    {
        var original = Orders();
        var renamed = original.RenameColumn("CustomerId", "BuyerId");
        var steps = TableAlterScriptBuilder.Steps(original, renamed, Ss);
        Assert.Equal(["EXEC sp_rename N'[sales].[Orders].[CustomerId]', N'BuyerId', N'COLUMN';"], steps);

        var repointed = original with { ForeignKeys = [original.ForeignKeys[0] with { ReferencedTable = "Products", ReferencedColumn = "ProductId" }] };
        var script = TableAlterScriptBuilder.Build(original, repointed, Ss);
        Assert.Contains("ALTER TABLE [sales].[Orders] DROP CONSTRAINT [FK_Orders_Customers];", script);
        Assert.Contains("ADD CONSTRAINT [FK_Orders_Customers] FOREIGN KEY ([CustomerId])\n    REFERENCES [dbo].[Products] ([ProductId]);", script);
    }

    [Fact]
    public void Indexes_that_go_away_are_dropped_and_new_ones_created()
    {
        var original = Orders();
        var edited = original with { Indexes = [new IndexDesign { Columns = ["OrderDate"], IsUnique = true }] };

        var steps = TableAlterScriptBuilder.Steps(original, edited, Pg);

        Assert.Equal("DROP INDEX \"sales\".\"IX_Orders_CustomerId\";", steps[0]);
        Assert.Equal("CREATE UNIQUE INDEX \"UX_Orders_OrderDate\" ON \"sales\".\"Orders\" (\"OrderDate\");", steps[^1]);
    }

    [Fact]
    public void Postgres_converts_another_kind_of_value_with_using_and_fills_nulls_before_not_null()
    {
        var original = new TableDesign
        {
            Schema = "public", Name = "items",
            Columns =
            [
                new ColumnDesign { Name = "id", OriginalName = "id", Type = "integer", IsPrimaryKey = true, IsNullable = false },
                new ColumnDesign { Name = "qty", OriginalName = "qty", Type = "text" },
                new ColumnDesign { Name = "note", OriginalName = "note", Type = "text" }
            ]
        };
        var edited = original
            .WithColumn("qty", c => c with { Type = "integer" })
            .WithColumn("note", c => c with { IsNullable = false, Default = "''" });

        Assert.Equal(
        [
            "ALTER TABLE \"public\".\"items\" ALTER COLUMN \"qty\" TYPE integer USING \"qty\"::integer;",
            "ALTER TABLE \"public\".\"items\" ALTER COLUMN \"note\" SET DEFAULT '';",
            "UPDATE \"public\".\"items\" SET \"note\" = '' WHERE \"note\" IS NULL;",
            "ALTER TABLE \"public\".\"items\" ALTER COLUMN \"note\" SET NOT NULL;"
        ], TableAlterScriptBuilder.Steps(original, edited, Pg));

        var review = TableDesignAdvisor.Review(edited, DesignContext.Empty(Pg), original);
        Assert.Equal(DesignSeverity.Warning, Find(review, "alter-type:qty").Severity);
        Assert.Equal(DesignSeverity.Tip, Find(review, "alter-not-null:note").Severity);
    }

    [Fact]
    public void Postgres_renames_and_moves_the_table_before_anything_else()
    {
        var original = Orders(Pg);
        var edited = (original with { Schema = "archive", Name = "OldOrders" }).AddColumn(new ColumnDesign { Name = "Note", Type = "text" });

        var steps = TableAlterScriptBuilder.Steps(original, edited, Pg, createSchema: true);

        Assert.Equal("ALTER TABLE \"sales\".\"Orders\" RENAME TO \"OldOrders\";", steps[0]);
        Assert.Equal("CREATE SCHEMA IF NOT EXISTS \"archive\";", steps[1]);
        Assert.Equal("ALTER TABLE \"sales\".\"OldOrders\" SET SCHEMA \"archive\";", steps[2]);
        Assert.Equal("ALTER TABLE \"archive\".\"OldOrders\" ADD COLUMN \"Note\" text NULL;", steps[3]);
        Assert.Equal(DesignSeverity.Tip, Find(TableDesignAdvisor.Review(edited, Context(Pg), original), "alter-rename-table").Severity);
    }

    [Fact]
    public void A_new_primary_key_replaces_the_old_constraint_under_its_name()
    {
        var original = Orders().WithColumn("CustomerId", c => c with { IsNullable = true });
        var edited = original.WithColumn("CustomerId", c => c with { IsPrimaryKey = true });
        var steps = TableAlterScriptBuilder.Steps(original, edited, Ss);
        Assert.Equal("ALTER TABLE [sales].[Orders] DROP CONSTRAINT [PK_Orders];", steps[0]);
        Assert.Contains("ALTER TABLE [sales].[Orders] ALTER COLUMN [CustomerId] int NOT NULL;", steps);
        Assert.Equal("ALTER TABLE [sales].[Orders] ADD CONSTRAINT [PK_Orders] PRIMARY KEY ([OrderId], [CustomerId]);", steps[^1]);
    }

    // ----- Warnings -----

    [Fact]
    public void Changing_a_key_or_dropping_a_column_other_tables_reference_is_an_error()
    {
        var customers = TableDesignLoader.Load(Table("dbo", "Customers"), Shop(), DbTableConstraints.None, Ss).Design;

        var rekeyed = customers.WithColumn("Email", c => c with { IsPrimaryKey = true });
        var key = Find(TableDesignAdvisor.Review(rekeyed, Context(), customers), "alter-key");
        Assert.Equal(DesignSeverity.Error, key.Severity);
        Assert.Equal(["CustomerId"], key.Fix!(rekeyed).PrimaryKey.Select(c => c.Name));

        var dropped = customers with { Columns = customers.Columns.Skip(1).ToList() };
        Assert.Equal(DesignSeverity.Error, Find(TableDesignAdvisor.Review(dropped, Context(), customers), "alter-drop:CustomerId").Severity);
    }

    [Fact]
    public void Shortening_a_column_warns_and_offers_to_keep_the_old_type()
    {
        var customers = TableDesignLoader.Load(Table("dbo", "Customers"), Shop(), DbTableConstraints.None, Ss).Design;
        var edited = customers.WithColumn("Name", c => c with { Size = "50" });

        var warning = Find(TableDesignAdvisor.Review(edited, Context(), customers), "alter-type:Name");

        Assert.Equal(DesignSeverity.Warning, warning.Severity);
        Assert.Equal("nvarchar(100)", warning.Fix!(edited).Column("Name")!.FullType);
    }

    [Fact]
    public void Sql_server_cannot_change_identity_and_a_new_not_null_column_needs_a_default()
    {
        var original = Orders();
        var edited = original.WithColumn("CustomerId", c => c with { IsIdentity = true })
            .AddColumn(new ColumnDesign { Name = "Code", Type = "nvarchar", Size = "10", IsNullable = false });

        var review = TableDesignAdvisor.Review(edited, Context(), original);

        Assert.Equal(DesignSeverity.Error, Find(review, "alter-identity:CustomerId").Severity);
        var notNull = Find(review, "alter-add-not-null:Code");
        Assert.True(notNull.Fix!(edited).Column("Code")!.IsNullable);
    }

    [Fact]
    public void Type_and_naming_tips_skip_existing_columns_nobody_changed()
    {
        var original = Orders() with
        {
            Columns = [.. Orders().Columns, new ColumnDesign { Name = "legacy_date", OriginalName = "legacy_date", Type = "datetime" }]
        };
        var untouched = TableDesignAdvisor.Review(original, Context(), original);
        Assert.DoesNotContain(untouched, s => s.Key.StartsWith("datetime:", StringComparison.Ordinal) || s.Key == "column-style");

        var added = original.AddColumn(new ColumnDesign { Name = "ShippedOn", Type = "datetime" });
        Assert.Contains(TableDesignAdvisor.Review(added, Context(), original), s => s.Key == "datetime:ShippedOn");
    }

    [Theory]
    [InlineData("nvarchar", "100", "nvarchar", "50", true)]
    [InlineData("nvarchar", "50", "nvarchar", "max", false)]
    [InlineData("bigint", null, "int", null, true)]
    [InlineData("int", null, "bigint", null, false)]
    [InlineData("decimal", "18,2", "decimal", "10,2", true)]
    [InlineData("decimal", "18,2", "decimal", "20,4", false)]
    [InlineData("int", null, "nvarchar", "50", false)]
    [InlineData("nvarchar", "50", "int", null, true)]
    [InlineData("date", null, "datetime2", null, false)]
    public void Risk_of_a_type_change(string fromType, string? fromSize, string toType, string? toSize, bool risky)
    {
        var before = new ColumnDesign { Name = "c", Type = fromType, Size = fromSize };
        var after = before with { Type = toType, Size = toSize };
        Assert.Equal(risky, ColumnTypes.RiskOfChange(Ss, before, after) is not null);
    }
}
