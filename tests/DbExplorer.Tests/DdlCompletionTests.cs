using DbExplorer.Application.Query;

namespace DbExplorer.Tests;

public class DdlCompletionTests
{
    private static SqlCompletionEngine Engine(string provider) => new(TestSnapshots.Shop(), id => "[" + id + "]", provider);

    private static CompletionResult Complete(string provider, string sql, bool explicitRequest = false) =>
        Engine(provider).Complete(sql, sql.Length, explicitRequest);

    [Theory]
    [InlineData("SqlServer")]
    [InlineData("Postgres")]
    [InlineData("MySql")]
    public void Cre_offers_create_database_and_the_old_five_first(string provider)
    {
        var labels = Complete(provider, "CRE").Items.Where(i => i.Kind == CompletionKind.Ddl).Select(i => i.Label).ToList();

        Assert.Equal(["CREATE TABLE", "CREATE VIEW", "CREATE PROCEDURE", "CREATE FUNCTION", "CREATE INDEX"], labels.Take(5));
        Assert.Contains("CREATE DATABASE", labels);
        Assert.Contains("CREATE SCHEMA", labels);
        Assert.Contains("CREATE TRIGGER", labels);
        Assert.Equal(labels.Count, labels.Distinct().Count());
    }

    [Fact]
    public void Ddl_is_filtered_per_engine()
    {
        var sqlServer = Complete("SqlServer", "CRE").Items.Select(i => i.Label).ToList();
        var postgres = Complete("Postgres", "CRE").Items.Select(i => i.Label).ToList();
        var mySql = Complete("MySql", "CRE").Items.Select(i => i.Label).ToList();

        Assert.DoesNotContain("CREATE EXTENSION", sqlServer);
        Assert.DoesNotContain("CREATE MATERIALIZED VIEW", sqlServer);
        Assert.Contains("CREATE LOGIN", sqlServer);
        Assert.Contains("CREATE EXTENSION", postgres);
        Assert.Contains("CREATE MATERIALIZED VIEW", postgres);
        Assert.DoesNotContain("CREATE LOGIN", postgres);
        Assert.DoesNotContain("CREATE SEQUENCE", mySql);
        Assert.DoesNotContain("CREATE EXTENSION", mySql);
        Assert.Contains("CREATE EVENT", mySql);
    }

    [Fact]
    public void Template_selects_its_first_placeholder()
    {
        var item = Complete("Postgres", "CRE").Items.Single(i => i.Label == "CREATE DATABASE");

        Assert.Equal("CREATE DATABASE database_name;", item.InsertText);
        Assert.Equal("CREATE DATABASE ".Length, item.CaretOffset);
        Assert.Equal("database_name".Length, item.SelectionLength);
        Assert.DoesNotContain('|', item.InsertText);
    }

    [Fact]
    public void Templates_use_each_engines_syntax()
    {
        string Text(string provider, string label) => Complete(provider, label[..3]).Items.Single(i => i.Label == label).InsertText;

        Assert.Contains("IDENTITY(1,1)", Text("SqlServer", "CREATE TABLE"));
        Assert.Contains("GENERATED ALWAYS AS IDENTITY", Text("Postgres", "CREATE TABLE"));
        Assert.Contains("AUTO_INCREMENT", Text("MySql", "CREATE TABLE"));
        Assert.Contains("sp_rename", Text("SqlServer", "ALTER TABLE RENAME COLUMN"));
        Assert.Contains("MODIFY COLUMN", Text("MySql", "ALTER TABLE MODIFY COLUMN"));
        Assert.Equal("DROP DATABASE IF EXISTS database_name;", Text("MySql", "DROP DATABASE"));
    }

    [Fact]
    public void Every_template_has_one_valid_placeholder()
    {
        foreach (var provider in new[] { "SqlServer", "Postgres", "MySql" })
        foreach (var item in DdlTemplates.For(provider))
        {
            Assert.DoesNotContain('|', item.InsertText);
            Assert.NotNull(item.CaretOffset);
            Assert.True(item.SelectionLength > 0, item.Label);
            Assert.True(item.CaretOffset + item.SelectionLength <= item.InsertText.Length, item.Label);
        }
    }

    [Fact]
    public void After_create_and_a_space_the_list_continues_the_phrase()
    {
        var result = Complete("SqlServer", "CREATE ", explicitRequest: true);

        Assert.Equal(0, result.ReplaceStart);
        Assert.Contains(result.Items, i => i.Label == "CREATE DATABASE");
        Assert.All(result.Items, i => Assert.StartsWith("CREATE ", i.Label));
    }

    [Fact]
    public void Typing_after_the_lead_word_filters_by_the_whole_phrase()
    {
        var sql = "SELECT 1;\ncreate da";
        var result = Complete("Postgres", sql);

        Assert.Equal(["CREATE DATABASE"], result.Items.Select(i => i.Label));
        Assert.Equal(sql.IndexOf("create", StringComparison.Ordinal), result.ReplaceStart);
    }

    [Fact]
    public void Modifiers_continue_the_phrase()
    {
        Assert.Equal(["CREATE OR REPLACE VIEW", "CREATE OR REPLACE FUNCTION", "CREATE OR REPLACE PROCEDURE"],
            Complete("Postgres", "CREATE OR ", explicitRequest: true).Items.Select(i => i.Label));
        Assert.Equal(["DROP MATERIALIZED VIEW"], Complete("Postgres", "DROP MAT").Items.Select(i => i.Label));
        Assert.Contains("ALTER TABLE ADD COLUMN", Complete("MySql", "ALTER ", explicitRequest: true).Items.Select(i => i.Label));
    }

    [Fact]
    public void Alter_table_followed_by_a_space_still_lists_tables()
    {
        var items = Complete("SqlServer", "ALTER TABLE ", explicitRequest: true).Items;

        Assert.Contains(items, i => i.Kind == CompletionKind.Table && i.Label == "Orders");
        Assert.DoesNotContain(items, i => i.Kind == CompletionKind.Ddl);
    }

    [Fact]
    public void Bare_ddl_keywords_are_not_listed_twice()
    {
        var items = Complete("SqlServer", "CREATE").Items;

        Assert.Single(items, i => i.Label == "CREATE TABLE");
        Assert.Equal(CompletionKind.Ddl, items.Single(i => i.Label == "CREATE TABLE").Kind);
    }
}
