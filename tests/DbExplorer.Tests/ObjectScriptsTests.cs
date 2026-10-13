using DbExplorer.Application.Copy;
using DbExplorer.Application.Metadata;
using DbExplorer.Core.Models;

namespace DbExplorer.Tests;

public class ObjectScriptsTests
{
    private static DbObject Obj(DbObjectType type, string schema = "dbo", string name = "Thing", string database = "Shop") =>
        new() { Database = database, Schema = schema, Name = name, Type = type };

    [Fact]
    public void Sql_server_procedure_becomes_create_or_alter_in_its_own_batch()
    {
        var script = ObjectScripts.ModifyScript(SqlDialect.SqlServerKey, Obj(DbObjectType.Procedure, name: "GetOrders"),
            ["create procedure dbo.GetOrders as select 1"]);

        Assert.StartsWith("-- Modify procedure [dbo].[GetOrders] in Shop.", script);
        Assert.Contains("\nCREATE OR ALTER procedure dbo.GetOrders as select 1\nGO\n", script);
    }

    [Fact]
    public void Sql_server_view_with_leading_comment_keeps_the_comment()
    {
        var script = ObjectScripts.ModifyScript(SqlDialect.SqlServerKey, Obj(DbObjectType.View),
            ["-- totals\nCREATE VIEW dbo.Thing AS SELECT 1 AS x"]);

        Assert.Contains("-- totals\nCREATE OR ALTER VIEW dbo.Thing AS SELECT 1 AS x\nGO", script);
    }

    [Fact]
    public void Sql_server_synonym_is_dropped_and_created_again()
    {
        var script = ObjectScripts.ModifyScript(SqlDialect.SqlServerKey, Obj(DbObjectType.Synonym, name: "Cust"),
            ["CREATE SYNONYM [dbo].[Cust] FOR [Sales].[dbo].[Customers];"]);

        Assert.Contains("DROP SYNONYM IF EXISTS [dbo].[Cust];\nGO\nCREATE SYNONYM [dbo].[Cust] FOR [Sales].[dbo].[Customers];\nGO", script);
    }

    [Fact]
    public void Sql_server_sequence_becomes_alter_without_type_or_restart()
    {
        var script = ObjectScripts.ModifyScript(SqlDialect.SqlServerKey, Obj(DbObjectType.Sequence, name: "OrderNo"),
            ["CREATE SEQUENCE [dbo].[OrderNo] AS bigint START WITH 100 INCREMENT BY 1 MINVALUE 1 MAXVALUE 999999 NO CYCLE;"]);

        Assert.Contains("ALTER SEQUENCE [dbo].[OrderNo] INCREMENT BY 1 MINVALUE 1 MAXVALUE 999999 NO CYCLE\n" +
                        "    -- RESTART WITH 100", script);
        Assert.DoesNotContain("bigint", script);
        Assert.DoesNotContain("START WITH 100 INCREMENT", script);
    }

    [Fact]
    public void Postgres_function_overloads_are_each_kept_as_create_or_replace()
    {
        var script = ObjectScripts.ModifyScript(SqlDialect.PostgresKey, Obj(DbObjectType.Function, "public", "total", "shop"),
        [
            "CREATE OR REPLACE FUNCTION public.total(a integer)\n RETURNS integer\n LANGUAGE sql\nAS $function$ select a $function$\n",
            "CREATE OR REPLACE FUNCTION public.total(a numeric)\n RETURNS numeric\n LANGUAGE sql\nAS $function$ select a $function$\n"
        ]);

        Assert.Contains("-- 2 overloads share this name", script);
        Assert.Contains("total(a integer)", script);
        Assert.Contains("total(a numeric)", script);
        Assert.Contains("$function$;\n", script);
    }

    [Fact]
    public void Postgres_trigger_becomes_create_or_replace_and_materialized_view_is_dropped_first()
    {
        var trigger = ObjectScripts.ModifyScript(SqlDialect.PostgresKey, Obj(DbObjectType.Trigger, "public", "audit"),
            ["CREATE TRIGGER audit AFTER INSERT ON public.orders FOR EACH ROW EXECUTE FUNCTION log_it();"]);
        Assert.Contains("\nCREATE OR REPLACE TRIGGER audit AFTER INSERT", trigger);

        var view = ObjectScripts.ModifyScript(SqlDialect.PostgresKey, Obj(DbObjectType.MaterializedView, "public", "mv"),
            ["CREATE MATERIALIZED VIEW public.mv AS\n SELECT 1"]);
        Assert.Contains("DROP MATERIALIZED VIEW IF EXISTS public.mv;\nCREATE MATERIALIZED VIEW public.mv AS\n SELECT 1;", view);
        Assert.Contains("drops its indexes", view);
    }

    [Fact]
    public void Postgres_sequence_becomes_alter_sequence_with_the_same_options()
    {
        var script = ObjectScripts.ModifyScript(SqlDialect.PostgresKey, Obj(DbObjectType.Sequence, "public", "seq"),
            ["CREATE SEQUENCE public.seq AS bigint START 1 INCREMENT 1 MINVALUE 1 MAXVALUE 100 NO CYCLE;"]);

        Assert.Contains("\nALTER SEQUENCE public.seq AS bigint START 1 INCREMENT 1 MINVALUE 1 MAXVALUE 100 NO CYCLE;\n", script);
    }

    [Fact]
    public void MySql_procedure_is_dropped_first_and_view_is_create_or_replace()
    {
        var proc = ObjectScripts.ModifyScript(SqlDialect.MySqlKey, Obj(DbObjectType.Procedure, "shop", "p", "shop"),
            ["CREATE PROCEDURE `shop`.`p`()\nBEGIN\n  SELECT 1;\nEND"]);
        Assert.Contains("DROP PROCEDURE IF EXISTS `shop`.`p`;\nCREATE PROCEDURE `shop`.`p`()\nBEGIN\n  SELECT 1;\nEND;", proc);
        Assert.Contains("grants on it go with it", proc);

        var view = ObjectScripts.ModifyScript(SqlDialect.MySqlKey, Obj(DbObjectType.View, "shop", "v", "shop"),
            ["CREATE OR REPLACE VIEW `shop`.`v` AS\nselect 1"]);
        Assert.Contains("\nCREATE OR REPLACE VIEW `shop`.`v` AS\nselect 1;", view);
    }

    [Fact]
    public void MariaDb_sequence_becomes_alter_without_engine()
    {
        var script = ObjectScripts.ModifyScript(SqlDialect.MySqlKey, Obj(DbObjectType.Sequence, "shop", "s", "shop"),
            ["CREATE SEQUENCE `s` start with 1 minvalue 1 maxvalue 9223372036854775806 increment by 1 cache 1000 nocycle ENGINE=InnoDB"]);

        Assert.Contains("ALTER SEQUENCE `s` start with 1 minvalue 1 maxvalue 9223372036854775806 increment by 1 cache 1000 nocycle;", script);
    }

    [Theory]
    [InlineData(DbObjectType.View, true)]
    [InlineData(DbObjectType.Procedure, true)]
    [InlineData(DbObjectType.TableFunction, true)]
    [InlineData(DbObjectType.Trigger, true)]
    [InlineData(DbObjectType.Sequence, true)]
    [InlineData(DbObjectType.Synonym, true)]
    [InlineData(DbObjectType.MaterializedView, true)]
    [InlineData(DbObjectType.Table, false)]
    [InlineData(DbObjectType.ForeignTable, false)]
    [InlineData(DbObjectType.Other, false)]
    public void Modify_script_covers_every_object_with_a_definition(DbObjectType type, bool expected) =>
        Assert.Equal(expected, ObjectScripts.CanModify(type));

    [Theory]
    [InlineData(SqlDialect.SqlServerKey, "DELETE FROM [dbo].[Orders]\nWHERE [Id] = <Id>\n  AND [Line] = <Line>;")]
    [InlineData(SqlDialect.PostgresKey, "DELETE FROM \"dbo\".\"Orders\"\nWHERE \"Id\" = <Id>\n  AND \"Line\" = <Line>;")]
    [InlineData(SqlDialect.MySqlKey, "DELETE FROM `dbo`.`Orders`\nWHERE `Id` = <Id>\n  AND `Line` = <Line>;")]
    public void Delete_template_filters_on_the_primary_key(string key, string expected)
    {
        DbColumn[] columns =
        [
            new() { Name = "Line", Ordinal = 2, IsPrimaryKey = true },
            new() { Name = "Id", Ordinal = 1, IsPrimaryKey = true },
            new() { Name = "Note", Ordinal = 3 }
        ];
        var script = ObjectScripts.DeleteTemplate(key, Obj(DbObjectType.Table, name: "Orders"), columns);
        Assert.Contains(expected, script);
        Assert.StartsWith("-- Delete rows from", script);
    }

    [Fact]
    public void Delete_template_without_a_key_has_a_condition_placeholder() =>
        Assert.Contains("WHERE <condition>;",
            ObjectScripts.DeleteTemplate(SqlDialect.PostgresKey, Obj(DbObjectType.Table, "public", "log"), []));

    [Theory]
    [InlineData(SqlDialect.SqlServerKey, "DROP TABLE [dbo].[Orders];")]
    [InlineData(SqlDialect.PostgresKey, "DROP TABLE \"dbo\".\"Orders\";")]
    [InlineData(SqlDialect.MySqlKey, "DROP TABLE `dbo`.`Orders`;")]
    public void Drop_table_quotes_for_the_engine(string key, string expected) =>
        Assert.Equal(expected, ObjectScripts.DropTable(key, Obj(DbObjectType.Table, name: "Orders")));
}
