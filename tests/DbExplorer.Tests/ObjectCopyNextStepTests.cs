using DbExplorer.Application.Copy;
using DbExplorer.Core.Models;

namespace DbExplorer.Tests;

public class DefaultTranslatorTests
{
    private const string Sql = SqlDialect.SqlServerKey;
    private const string Pg = SqlDialect.PostgresKey;

    [Theory]
    [InlineData("((0))", Sql, Pg, "integer", "0")]
    [InlineData("((1))", Sql, Pg, "boolean", "TRUE")]
    [InlineData("((0))", Sql, Pg, "boolean", "FALSE")]
    [InlineData("(N'new')", Sql, Pg, "varchar", "'new'")]
    [InlineData("(getdate())", Sql, Pg, "timestamp", "CURRENT_TIMESTAMP")]
    [InlineData("(newid())", Sql, Pg, "uuid", "gen_random_uuid()")]
    [InlineData("'draft'::character varying", Pg, Sql, "nvarchar", "N'draft'")]
    [InlineData("now()", Pg, Sql, "datetime2", "SYSDATETIME()")]
    [InlineData("now()", Pg, Sql, "datetime", "GETDATE()")]
    [InlineData("true", Pg, Sql, "bit", "1")]
    [InlineData("gen_random_uuid()", Pg, Sql, "uniqueidentifier", "NEWID()")]
    public void Portable_defaults_are_translated(string expression, string from, string to, string targetType, string expected) =>
        Assert.Equal(expected, DefaultTranslator.Translate(expression, from, to, targetType));

    [Fact]
    public void Same_engine_keeps_the_expression_and_unknown_ones_are_dropped()
    {
        Assert.Equal("(dateadd(day,(1),getdate()))", DefaultTranslator.Translate("(dateadd(day,(1),getdate()))", Sql, Sql, "datetime"));
        Assert.Null(DefaultTranslator.Translate("(dateadd(day,(1),getdate()))", Sql, Pg, "timestamp"));
        Assert.Null(DefaultTranslator.Translate("nextval('orders_id_seq'::regclass)", Pg, Pg, "int4"));
        Assert.True(DefaultTranslator.IsSequenceDefault("nextval('orders_id_seq'::regclass)"));
    }
}

public class ObjectCopyStreamingAndBatchTests
{
    private static DbColumn Col(string name, string baseType) => new() { Name = name, BaseType = baseType };

    [Fact]
    public void Keyset_predicate_spells_out_composite_keys()
    {
        var predicate = ObjectCopyService.KeysetPredicate(SqlDialect.SqlServer,
            [Col("OrderId", "int"), Col("LineNo", "int")], [10, 3]);
        Assert.Equal("[OrderId] > 10 OR ([OrderId] = 10 AND [LineNo] > 3)", predicate);

        Assert.Equal("\"Code\" > 'A''B'",
            ObjectCopyService.KeysetPredicate(SqlDialect.Postgres, [Col("Code", "text")], ["A'B"]));
    }

    [Fact]
    public void Insert_statements_are_batched_and_wrap_identity_inserts()
    {
        IReadOnlyList<IReadOnlyList<object?>> rows = [[1, "a"], [2, "b"], [3, "c"]];
        var columns = new[] { Col("Id", "int") with { IsIdentity = true }, Col("Name", "nvarchar") };

        var statements = ObjectCopyService.BuildInsertStatements(SqlDialect.SqlServer, "dbo", "T", ["Id", "Name"], columns, rows, batchSize: 2);

        Assert.Equal(2, statements.Count);
        Assert.StartsWith("SET IDENTITY_INSERT [dbo].[T] ON;", statements[0]);
        Assert.Contains("(1, N'a'),\n(2, N'b');", statements[0]);
        Assert.EndsWith("SET IDENTITY_INSERT [dbo].[T] OFF;", statements[0]);

        var pg = ObjectCopyService.BuildInsertStatements(SqlDialect.Postgres, "public", "t", ["Id", "Name"], columns, rows, batchSize: 10);
        Assert.StartsWith("INSERT INTO \"public\".\"t\" (\"Id\", \"Name\") OVERRIDING SYSTEM VALUE VALUES", Assert.Single(pg));
    }

    [Fact]
    public void Batch_order_puts_parents_first_then_views_routines_and_triggers()
    {
        var shop = TestSnapshots.Shop();
        DbObject Find(string name) => shop.Objects.Single(o => o.Name == name);
        var trigger = new DbObject { Schema = "dbo", Name = "trg", Type = DbObjectType.Trigger };

        var ordered = ObjectCopyService.OrderForCopy(
            [trigger, Find("vOrderTotals"), Find("usp_GetCustomer"), Find("OrderLines"), Find("Orders"), Find("Customers"), Find("Products")],
            shop).Select(o => o.Name).ToList();

        Assert.True(ordered.IndexOf("Customers") < ordered.IndexOf("Orders"));
        Assert.True(ordered.IndexOf("Orders") < ordered.IndexOf("OrderLines"));
        Assert.True(ordered.IndexOf("Products") < ordered.IndexOf("OrderLines"));
        Assert.Equal(["vOrderTotals", "usp_GetCustomer", "trg"], ordered.Skip(4));
    }
}

public class ObjectCopySchemaMappingTests
{
    [Fact]
    public void Default_schemas_map_between_engines()
    {
        Assert.Equal("dbo", ObjectCopyService.MapSchema("public", SqlDialect.PostgresKey, SqlDialect.SqlServerKey));
        Assert.Equal("public", ObjectCopyService.MapSchema("dbo", SqlDialect.SqlServerKey, SqlDialect.PostgresKey));
        Assert.Equal("sales", ObjectCopyService.MapSchema("sales", SqlDialect.PostgresKey, SqlDialect.SqlServerKey));
        Assert.Equal("public", ObjectCopyService.MapSchema("public", SqlDialect.PostgresKey, SqlDialect.PostgresKey));
    }
}
