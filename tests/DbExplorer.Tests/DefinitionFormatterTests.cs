using DbExplorer.Application.Metadata;
using DbExplorer.Core.Models;

namespace DbExplorer.Tests;

public class DefinitionFormatterTests
{
    [Fact]
    public void Tidy_normalises_line_ends_tabs_margins_and_blank_lines()
    {
        var text = "\r\n\r\n    create view v as\r\n\tselect 1   \r\n\r\n\r\n\r\n    -- end\r\n\r\n";
        Assert.Equal("create view v as\nselect 1\n\n-- end", DefinitionFormatter.Tidy(text));
    }

    [Fact]
    public void Sql_server_procedure_gets_one_parameter_per_line_and_upper_case_keywords()
    {
        var text = "create procedure dbo.GetOrders @customerId int, @from date = null, @top int output as\n" +
                   "begin\n    set nocount on;\n    select top (@top) * from sales.Orders where CustomerId = @customerId;\nend";

        Assert.Equal(
            "CREATE PROCEDURE dbo.GetOrders\n" +
            "    @customerId int,\n" +
            "    @from date = NULL,\n" +
            "    @top int OUTPUT\n" +
            "AS\n" +
            "BEGIN\n    SET NOCOUNT ON;\n    SELECT TOP (@top) * FROM sales.Orders WHERE CustomerId = @customerId;\nEND",
            DefinitionFormatter.Format(text, DbObjectType.Procedure));
    }

    [Fact]
    public void Function_parameters_in_parentheses_go_one_per_line()
    {
        var text = "CREATE FUNCTION dbo.Total(@orderId int, @withTax bit = 1) RETURNS decimal(18, 2) AS BEGIN RETURN 0 END";

        Assert.Equal(
            "CREATE FUNCTION dbo.Total(\n    @orderId int,\n    @withTax bit = 1\n) RETURNS decimal(18, 2) AS BEGIN RETURN 0 END",
            DefinitionFormatter.Format(text, DbObjectType.ScalarFunction));
    }

    [Fact]
    public void A_single_short_parameter_stays_on_the_header_line()
    {
        Assert.StartsWith("CREATE FUNCTION f(@id int) RETURNS",
            DefinitionFormatter.Format("create function f( @id  int ) returns int as begin return @id end", DbObjectType.Function));
    }

    [Fact]
    public void Postgres_function_options_line_up_and_plpgsql_body_is_keyword_cased()
    {
        var text = "CREATE OR REPLACE FUNCTION public.add_order(customer integer, amount numeric)\n" +
                   " RETURNS integer\n LANGUAGE plpgsql\nAS $function$\nbegin\n  insert into orders(customer_id, amount) values (customer, amount);\n  return 1;\nend;\n$function$";

        Assert.Equal(
            "CREATE OR REPLACE FUNCTION public.add_order(\n    customer integer,\n    amount numeric\n)\n" +
            "RETURNS integer\nLANGUAGE plpgsql\nAS $function$\nBEGIN\n  INSERT INTO orders(customer_id, amount) VALUES (customer, amount);\n  RETURN 1;\nEND;\n$function$",
            DefinitionFormatter.Format(text, DbObjectType.Function));
    }

    [Fact]
    public void Bodies_in_other_languages_are_left_alone()
    {
        var text = "CREATE FUNCTION f()\n RETURNS text\n LANGUAGE plpython3u\nAS $$\nreturn 'select from'\n$$";
        Assert.EndsWith("AS $$\nreturn 'select from'\n$$", DefinitionFormatter.Format(text, DbObjectType.Function));
    }

    [Fact]
    public void View_query_is_laid_out_one_clause_per_line()
    {
        var text = "create view sales.BigOrders as select o.Id, o.Total, c.Name from sales.Orders o join dbo.Customers c on c.Id = o.CustomerId where o.Total > 100 and o.Status = 'open'";

        Assert.Equal(
            "CREATE VIEW sales.BigOrders AS\n" +
            "SELECT\n    o.Id,\n    o.Total,\n    c.Name\n" +
            "FROM sales.Orders o\n" +
            "JOIN dbo.Customers c\n    ON c.Id = o.CustomerId\n" +
            "WHERE o.Total > 100\n    AND o.Status = 'open'",
            DefinitionFormatter.Format(text, DbObjectType.View));
    }

    [Fact]
    public void Strings_comments_and_quoted_names_are_kept_as_written()
    {
        var text = "create view v as select [select] as \"from\", 'where and or' as s -- select from\nfrom t";
        var formatted = DefinitionFormatter.Format(text, DbObjectType.View);

        Assert.Contains("[select]", formatted);
        Assert.Contains("\"from\"", formatted);
        Assert.Contains("'where and or'", formatted);
        Assert.Contains("-- select from", formatted);
    }

    [Fact]
    public void One_line_postgres_trigger_is_split_into_clauses()
    {
        var text = "CREATE TRIGGER orders_audit AFTER INSERT OR UPDATE ON public.orders FOR EACH ROW EXECUTE FUNCTION audit_orders();";

        Assert.Equal(
            "CREATE TRIGGER orders_audit\n    AFTER INSERT OR UPDATE\n    ON public.orders\n    FOR EACH ROW\n    EXECUTE FUNCTION audit_orders();",
            DefinitionFormatter.Format(text, DbObjectType.Trigger));
    }

    [Fact]
    public void One_line_sequence_gets_one_option_per_line()
    {
        var text = "CREATE SEQUENCE public.order_seq AS bigint START 1 INCREMENT 1 MINVALUE 1 MAXVALUE 9223372036854775807 NO CYCLE;";

        Assert.Equal(
            "CREATE SEQUENCE public.order_seq\n    AS bigint\n    START 1\n    INCREMENT 1\n    MINVALUE 1\n    MAXVALUE 9223372036854775807\n    NO CYCLE;",
            DefinitionFormatter.Format(text, DbObjectType.Sequence));
    }

    [Fact]
    public void Formatting_twice_gives_the_same_text()
    {
        var text = "create procedure p @a int, @b int as select a from t where b = @b";
        var once = DefinitionFormatter.Format(text, DbObjectType.Procedure);
        Assert.Equal(once, DefinitionFormatter.Format(once, DbObjectType.Procedure));
    }
}
