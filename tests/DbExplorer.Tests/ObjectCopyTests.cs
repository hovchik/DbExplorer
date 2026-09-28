using DbExplorer.Application.Copy;
using DbExplorer.Application.Metadata;
using DbExplorer.Core.Models;

namespace DbExplorer.Tests;

public class ObjectCopyAnalysisTests
{
    private const string Sql = SqlDialect.SqlServerKey;
    private const string Pg = SqlDialect.PostgresKey;

    private static DbObject Obj(MetadataSnapshot s, string schema, string name) =>
        s.Objects.Single(o => o.Schema == schema && o.Name == name);

    private static MetadataSnapshot Empty() => new()
    {
        Objects = [], Columns = [], Modules = [], ForeignKeys = [], Indexes = [], RefreshedAt = DateTimeOffset.Now
    };

    [Fact]
    public void Missing_table_suggests_create_and_lists_missing_parents_parents_first()
    {
        var shop = TestSnapshots.Shop();
        var lines = Obj(shop, "sales", "OrderLines");

        var a = ObjectCopyService.Analyze(shop, Sql, lines, Empty(), Sql, "", "sales", "OrderLines", sameServer: false);

        Assert.False(a.TargetExists);
        Assert.Equal([CopyAction.CreateObject], a.AvailableActions);
        Assert.Equal(CopyAction.CreateObject, a.RecommendedAction);
        var parents = a.MissingParents.Select(p => p.FullName).ToList();
        Assert.Equal(3, parents.Count);
        Assert.True(parents.IndexOf("dbo.Customers") < parents.IndexOf("sales.Orders"), "Customers must be created before Orders");
        Assert.Contains("dbo.Products", parents);
    }

    [Fact]
    public void Existing_table_with_key_suggests_merge_and_reports_column_differences()
    {
        var left = TestSnapshots.Shop();
        var right = TestSnapshots.Shop();
        right = new MetadataSnapshot
        {
            Objects = right.Objects,
            Columns = right.Columns.Where(c => !(c.Table == "Customers" && c.Name == "Email"))
                .Append(new DbColumn { Schema = "dbo", Table = "Customers", Name = "Phone", Ordinal = 9, DataType = "nvarchar(20)", BaseType = "nvarchar", IsNullable = false })
                .ToList(),
            Modules = [], ForeignKeys = right.ForeignKeys, Indexes = [], RefreshedAt = DateTimeOffset.Now
        };

        var a = ObjectCopyService.Analyze(left, Sql, Obj(left, "dbo", "Customers"), right, Sql, "", "dbo", "Customers", sameServer: false);

        Assert.True(a.TargetExists);
        Assert.Equal(CopyAction.MergeData, a.RecommendedAction);
        Assert.Equal([CopyAction.MergeData, CopyAction.ReplaceData, CopyAction.DropAndRecreate], a.AvailableActions);
        Assert.Equal(["CustomerId"], a.KeyColumns);
        Assert.Equal(["Email"], a.ColumnsMissingOnTarget);
        Assert.Equal(["Phone"], a.ColumnsOnlyOnTarget);
        Assert.Contains(a.Warnings, w => w.Contains("Phone") && w.Contains("NOT NULL"));
        Assert.Contains(a.IncomingReferences, r => r.Contains("sales.Orders"));
    }

    [Fact]
    public void Existing_table_without_key_cannot_merge()
    {
        var left = TestSnapshots.Shop();
        var noKeys = new MetadataSnapshot
        {
            Objects = left.Objects,
            Columns = left.Columns.Select(c => c with { IsPrimaryKey = false }).ToList(),
            Modules = [], ForeignKeys = [], Indexes = [], RefreshedAt = DateTimeOffset.Now
        };

        var a = ObjectCopyService.Analyze(noKeys, Sql, Obj(noKeys, "dbo", "Audit"), noKeys, Sql, "", "dbo", "Audit", sameServer: false);

        Assert.DoesNotContain(CopyAction.MergeData, a.AvailableActions);
        Assert.Equal(CopyAction.ReplaceData, a.RecommendedAction);
    }

    [Fact]
    public void Same_object_on_the_same_server_offers_nothing()
    {
        var shop = TestSnapshots.Shop();
        var a = ObjectCopyService.Analyze(shop, Sql, Obj(shop, "dbo", "Audit"), shop, Sql, "", "dbo", "Audit", sameServer: true);
        Assert.Empty(a.AvailableActions);
    }

    [Fact]
    public void Copying_under_a_new_name_on_the_same_server_creates_it()
    {
        var shop = TestSnapshots.Shop();
        var a = ObjectCopyService.Analyze(shop, Sql, Obj(shop, "dbo", "Audit"), shop, Sql, "", "dbo", "Audit_copy", sameServer: true);
        Assert.Equal([CopyAction.CreateObject], a.AvailableActions);
    }

    [Fact]
    public void Code_objects_need_the_same_engine_and_name()
    {
        var shop = TestSnapshots.Shop();
        var view = Obj(shop, "sales", "vOrderTotals");

        Assert.Empty(ObjectCopyService.Analyze(shop, Sql, view, Empty(), Pg, "", "sales", "vOrderTotals", false).AvailableActions);
        Assert.Empty(ObjectCopyService.Analyze(shop, Sql, view, Empty(), Sql, "", "sales", "vOther", false).AvailableActions);
        Assert.Equal([CopyAction.CreateObject], ObjectCopyService.Analyze(shop, Sql, view, Empty(), Sql, "", "sales", "vOrderTotals", false).AvailableActions);
        Assert.Equal([CopyAction.ReplaceDefinition], ObjectCopyService.Analyze(shop, Sql, view, shop, Sql, "", "sales", "vOrderTotals", false).AvailableActions);
    }

    [Fact]
    public void A_different_object_type_under_the_target_name_blocks_the_copy()
    {
        var shop = TestSnapshots.Shop();
        var a = ObjectCopyService.Analyze(shop, Sql, Obj(shop, "dbo", "Audit"), shop, Sql, "", "dbo", "usp_GetCustomer", sameServer: false);
        Assert.Empty(a.AvailableActions);
        Assert.Contains("already exists", a.Summary);
    }
}

public class ObjectCopyRowDiffTests
{
    [Fact]
    public void Rows_are_split_into_inserts_updates_deletes_and_unchanged()
    {
        IReadOnlyList<IReadOnlyList<object?>> source =
        [
            [1, "a"],
            [2, "b"],
            [3, "c"]
        ];
        IReadOnlyList<IReadOnlyList<object?>> target =
        [
            [1L, "a"],      // same (int vs bigint key)
            [2L, "B"],      // changed
            [4L, "d"]       // only on the right
        ];

        var diff = ObjectCopyService.DiffRows(source, target, [0]);

        Assert.Equal(1, diff.Unchanged);
        Assert.Equal(3, Assert.Single(diff.Inserts)[0]);
        var update = Assert.Single(diff.Updates);
        Assert.Equal([1], update.ChangedColumns);
        Assert.Equal(4L, Assert.Single(diff.Deletes)[0]);
    }

    [Fact]
    public void Composite_keys_match_on_every_part()
    {
        IReadOnlyList<IReadOnlyList<object?>> source = [[1, 1, "x"], [1, 2, "y"]];
        IReadOnlyList<IReadOnlyList<object?>> target = [[1, 2, "y"]];

        var diff = ObjectCopyService.DiffRows(source, target, [0, 1]);

        Assert.Single(diff.Inserts);
        Assert.Equal(1, diff.Unchanged);
        Assert.Empty(diff.Deletes);
    }
}

public class SqlDialectTests
{
    [Fact]
    public void Sql_server_literals()
    {
        var d = SqlDialect.SqlServer;
        Assert.Equal("N'O''Brien'", d.Literal("O'Brien"));
        Assert.Equal("NULL", d.Literal(null));
        Assert.Equal("1", d.Literal(true));
        Assert.Equal("0x0AFF", d.Literal(new byte[] { 0x0A, 0xFF }));
        Assert.Equal("12.50", d.Literal(12.50m));
        Assert.Equal("'2024-05-06T07:08:09.123'", d.Literal(new DateTime(2024, 5, 6, 7, 8, 9, 123), "datetime"));
        Assert.Equal("'2024-05-06'", d.Literal(new DateTime(2024, 5, 6), "date"));
        Assert.Equal("'2024-05-06T07:08:09.0000000'", d.Literal(new DateTime(2024, 5, 6, 7, 8, 9), "datetime2"));
        Assert.Equal("'01:02:03'", d.Literal(new TimeSpan(1, 2, 3)));
        Assert.Throws<InvalidOperationException>(() => d.Literal(double.NaN));
    }

    [Fact]
    public void Postgres_literals()
    {
        var d = SqlDialect.Postgres;
        Assert.Equal("'O''Brien'", d.Literal("O'Brien"));
        Assert.Equal("TRUE", d.Literal(true));
        Assert.Equal("'\\x0AFF'", d.Literal(new byte[] { 0x0A, 0xFF }));
        Assert.Equal("'2024-05-06 07:08:09.000000+00'", d.Literal(new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc), "timestamptz"));
        Assert.Equal("'{1,2,3}'", d.Literal(new[] { 1, 2, 3 }));
        Assert.Equal("'{\"a\",\"b \\\"c\\\"\"}'", d.Literal(new[] { "a", "b \"c\"" }));
        Assert.Equal("'NaN'", d.Literal(double.NaN));
    }

    [Fact]
    public void Quoting_escapes_the_closing_character()
    {
        Assert.Equal("[a]]b]", SqlDialect.SqlServer.Quote("a]b"));
        Assert.Equal("\"a\"\"b\"", SqlDialect.Postgres.Quote("a\"b"));
    }

    [Fact]
    public void Sql_server_definitions_become_create_or_alter_after_leading_comments()
    {
        var definition = "-- header\n/* note */\ncreate   procedure dbo.p AS SELECT 1";
        Assert.Equal("-- header\n/* note */\nCREATE OR ALTER   procedure dbo.p AS SELECT 1",
            SqlDialect.SqlServer.ToReplaceDefinition(definition, DbObjectType.Procedure));
        Assert.Equal("CREATE OR ALTER VIEW v AS SELECT 1",
            SqlDialect.SqlServer.ToReplaceDefinition("CREATE OR ALTER VIEW v AS SELECT 1", DbObjectType.View));
    }

    [Fact]
    public void Postgres_triggers_and_materialized_views_are_replaceable()
    {
        Assert.StartsWith("CREATE OR REPLACE TRIGGER t ",
            SqlDialect.Postgres.ToReplaceDefinition("CREATE TRIGGER t BEFORE INSERT ON public.x FOR EACH ROW EXECUTE FUNCTION f();", DbObjectType.Trigger));
        Assert.StartsWith("DROP MATERIALIZED VIEW IF EXISTS public.mv;\nCREATE MATERIALIZED VIEW public.mv AS",
            SqlDialect.Postgres.ToReplaceDefinition("CREATE MATERIALIZED VIEW public.mv AS\n SELECT 1", DbObjectType.MaterializedView));
        Assert.Equal("CREATE VIEW v AS SELECT 1;", SqlDialect.Postgres.AsStatement("CREATE VIEW v AS SELECT 1\n"));
    }

    [Fact]
    public void Index_column_lists_are_parsed_and_expressions_rejected()
    {
        string[] columns = ["Id", "Created At"];
        Assert.Equal(["[Id]", "[Created At] DESC"],
            ObjectCopyService.ParseIndexColumns("Id, Created At DESC", columns, SqlDialect.SqlServer));
        Assert.Equal(["\"Created At\""],
            ObjectCopyService.ParseIndexColumns("\"Created At\"", columns, SqlDialect.Postgres));
        Assert.Null(ObjectCopyService.ParseIndexColumns("lower(name)", columns, SqlDialect.Postgres));
    }
}

public class ColumnTypeMapperTests
{
    private static string Map(string dataType, string baseType, string from, string to) =>
        ColumnTypeMapper.Map(new DbColumn { Name = "c", DataType = dataType, BaseType = baseType }, from, to).Type;

    [Theory]
    [InlineData("nvarchar(50)", "nvarchar", "varchar(50)")]
    [InlineData("nvarchar(max)", "nvarchar", "text")]
    [InlineData("decimal(18,2)", "decimal", "numeric(18,2)")]
    [InlineData("datetime2(7)", "datetime2", "timestamp(6)")]
    [InlineData("bit", "bit", "boolean")]
    [InlineData("uniqueidentifier", "uniqueidentifier", "uuid")]
    [InlineData("varbinary(max)", "varbinary", "bytea")]
    public void Sql_server_to_postgres(string dataType, string baseType, string expected) =>
        Assert.Equal(expected, Map(dataType, baseType, SqlDialect.SqlServerKey, SqlDialect.PostgresKey));

    [Theory]
    [InlineData("character varying(100)", "varchar", "nvarchar(100)")]
    [InlineData("character varying(5000)", "varchar", "nvarchar(max)")]
    [InlineData("numeric(10,2)", "numeric", "decimal(10,2)")]
    [InlineData("timestamp(3) with time zone", "timestamptz", "datetimeoffset(3)")]
    [InlineData("boolean", "bool", "bit")]
    [InlineData("jsonb", "jsonb", "nvarchar(max)")]
    [InlineData("integer[]", "_int4", "nvarchar(max)")]
    public void Postgres_to_sql_server(string dataType, string baseType, string expected) =>
        Assert.Equal(expected, Map(dataType, baseType, SqlDialect.PostgresKey, SqlDialect.SqlServerKey));

    [Fact]
    public void Same_engine_keeps_the_catalog_type()
    {
        Assert.Equal("nvarchar(50)", Map("nvarchar(50)", "nvarchar", SqlDialect.SqlServerKey, SqlDialect.SqlServerKey));
        Assert.Null(ColumnTypeMapper.Map(new DbColumn { DataType = "x", BaseType = "x" }, SqlDialect.PostgresKey, SqlDialect.PostgresKey).Warning);
    }
}
