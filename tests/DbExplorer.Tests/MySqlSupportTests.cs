using DbExplorer.Application.Copy;
using DbExplorer.Application.Design;
using DbExplorer.Application.Lab;
using DbExplorer.Application.Query;
using DbExplorer.Application.Query.Plans;
using DbExplorer.Core.Models;
using DbExplorer.Tests.Plans;

namespace DbExplorer.Tests;

/// <summary>The MySQL / MariaDB pieces of the engine-neutral features that need no server.</summary>
public class MySqlSupportTests
{
    private const string Key = SqlDialect.MySqlKey;

    // ----- Plans -----

    [Fact]
    public void Mysql_analyze_tree_builds_the_operator_tree_with_measured_rows()
    {
        var plan = MySqlPlanParser.ParseTree(PlanSamples.Read("mysql-analyze.txt"), "SELECT …");

        Assert.True(plan.IsActual);
        Assert.Equal(["Table scan", "Aggregate using temporary table", "Nested loop inner join", "Filter", "Table scan", "Single-row index lookup"],
            plan.Nodes.Select(n => n.Operator));
        Assert.Equal("<temporary>", plan.Nodes[0].Object);
        Assert.Equal("o", plan.Nodes[4].Object);
        Assert.Equal("c using PRIMARY", plan.Nodes[5].Object);
        Assert.Equal(plan.Nodes[2], plan.Nodes[5].Parent);
        Assert.Equal(3, plan.Nodes[4].EstimatedRows);
        // Rows per loop × loops: the lookup ran twice and found one row each time.
        Assert.Equal(2, plan.Nodes[5].ActualRows);
    }

    [Fact]
    public void Mariadb_json_plan_names_tables_by_access_type()
    {
        var plan = MySqlPlanParser.ParseMariaDbJson(PlanSamples.Read("mariadb-explain.json"), "SELECT …");

        Assert.False(plan.IsActual);
        Assert.Equal("MySql", plan.Provider);
        var tables = plan.Nodes.Where(n => n.Object is "c" or "o").ToList();
        Assert.Equal(["c", "o"], tables.Select(n => n.Object));
        Assert.All(tables, t => Assert.Equal(t.Object == "c" ? 2 : 3, t.EstimatedRows));
        Assert.Contains(plan.Nodes, n => n.Operator.Contains("sort", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("MariaDB 10.11.6", true)]
    [InlineData("MySQL 8.0.36", false)]
    [InlineData(null, false)]
    public void Server_flavor_comes_from_the_version_string(string? version, bool mariaDb) =>
        Assert.Equal(mariaDb, PlanReader.IsMariaDb(version));

    [Fact]
    public void Plan_scripts_follow_the_server_flavor()
    {
        Assert.Contains("EXPLAIN FORMAT=TREE", PlanReader.BuildScript("SELECT 1", Key, analyze: false, "MySQL 8.0.36"));
        Assert.Contains("EXPLAIN ANALYZE", PlanReader.BuildScript("SELECT 1", Key, analyze: true, "MySQL 8.0.36"));
        Assert.Contains("EXPLAIN FORMAT=JSON", PlanReader.BuildScript("SELECT 1", Key, analyze: false, "MariaDB 10.11.6"));
        Assert.Contains("ANALYZE FORMAT=JSON", PlanReader.BuildScript("SELECT 1", Key, analyze: true, "MariaDB 10.11.6"));
    }

    // ----- Dialect, types, defaults -----

    [Fact]
    public void Dialect_quotes_with_backticks_and_escapes_backslashes()
    {
        var d = SqlDialect.For(Key);
        Assert.Same(SqlDialect.MySql, d);
        Assert.Equal("`we``ird`", d.Quote("we`ird"));
        Assert.Equal("`shop`.`orders`", d.Table("shop", "orders"));
        Assert.Equal(@"'it''s a \\ path'", d.Literal(@"it's a \ path"));
        Assert.Equal("X'0AFF'", d.Literal(new byte[] { 0x0A, 0xFF }));
        Assert.Contains("LIMIT 5", d.SelectTop("*", "`t`", null, null, 5));
        Assert.Equal(" AUTO_INCREMENT", d.IdentityClause);
    }

    [Fact]
    public void Routines_are_replaced_by_drop_and_create()
    {
        var d = SqlDialect.MySql;
        var script = d.ToReplaceDefinition("CREATE DEFINER=`root`@`%` PROCEDURE `shop`.`p`() BEGIN SELECT 1; END", DbObjectType.Procedure);
        Assert.StartsWith("DROP PROCEDURE IF EXISTS `shop`.`p`", script);
        Assert.StartsWith("CREATE OR REPLACE", d.ToReplaceDefinition("CREATE VIEW `v` AS SELECT 1", DbObjectType.View));
    }

    [Theory]
    [InlineData("SqlServer", "nvarchar(100)", "nvarchar", "varchar(100)")]
    [InlineData("SqlServer", "bit", "bit", "tinyint(1)")]
    [InlineData("SqlServer", "datetime2(7)", "datetime2", "datetime(6)")]
    [InlineData("SqlServer", "uniqueidentifier", "uniqueidentifier", "char(36)")]
    [InlineData("Postgres", "text", "text", "longtext")]
    [InlineData("Postgres", "boolean", "bool", "tinyint(1)")]
    [InlineData("Postgres", "jsonb", "jsonb", "json")]
    public void Types_map_into_mysql(string from, string dataType, string baseType, string expected) =>
        Assert.Equal(expected, ColumnTypeMapper.Map(new DbColumn { Name = "c", DataType = dataType, BaseType = baseType }, from, Key).Type);

    [Theory]
    [InlineData("Postgres", "tinyint(1)", "tinyint", "boolean")]
    [InlineData("Postgres", "int unsigned", "int", "bigint")]
    [InlineData("Postgres", "datetime(6)", "datetime", "timestamp(6)")]
    [InlineData("SqlServer", "tinyint(1)", "tinyint", "bit")]
    [InlineData("SqlServer", "longtext", "longtext", "nvarchar(max)")]
    public void Types_map_out_of_mysql(string to, string dataType, string baseType, string expected) =>
        Assert.Equal(expected, ColumnTypeMapper.Map(new DbColumn { Name = "c", DataType = dataType, BaseType = baseType }, Key, to).Type);

    [Theory]
    [InlineData("SqlServer", "getdate()", "datetime", "CURRENT_TIMESTAMP")]
    [InlineData("Postgres", "now()", "datetime", "CURRENT_TIMESTAMP")]
    [InlineData("Postgres", "gen_random_uuid()", "char", "(UUID())")]
    public void Defaults_translate_into_mysql(string from, string expression, string targetType, string expected) =>
        Assert.Equal(expected, DefaultTranslator.Translate(expression, from, Key, targetType));

    // ----- Editor -----

    [Fact]
    public void Delimiter_blocks_split_on_the_custom_delimiter()
    {
        const string script = "DELIMITER $$\nCREATE PROCEDURE p() BEGIN SELECT 1; SELECT 2; END$$\nDELIMITER ;\nSELECT 3;";
        var statements = SqlScriptTools.SplitStatements(script, splitOnBlankLines: false).Select(r => r.Of(script).Trim()).ToList();
        // A DELIMITER line is a comment leading the statement after it; the custom delimiter itself is left out.
        Assert.Equal(2, statements.Count);
        Assert.EndsWith("CREATE PROCEDURE p() BEGIN SELECT 1; SELECT 2; END", statements[0]);
        Assert.EndsWith("SELECT 3;", statements[1]);
    }

    [Fact]
    public void Hash_comments_and_delimiter_lines_are_comments()
    {
        var tokens = SqlLexer.Tokenize("# note\nDELIMITER //\nSELECT 1//");
        Assert.Equal(SqlTokenKind.Comment, tokens[0].Kind);
        Assert.Contains(tokens, t => t.Kind == SqlTokenKind.Comment && t.Text.StartsWith("DELIMITER", StringComparison.Ordinal));
        Assert.Equal(SqlTokenKind.Semicolon, tokens.Last(t => !t.IsTrivia).Kind);
    }

    // ----- Lab -----

    [Theory]
    [InlineData("ALTER TABLE orders ADD COLUMN x int", ImpactSeverity.Warning)]
    [InlineData("RENAME TABLE orders TO orders_old", ImpactSeverity.Danger)]
    [InlineData("LOCK TABLES orders WRITE", ImpactSeverity.Danger)]
    public void Ddl_lock_impact(string statement, ImpactSeverity severity)
    {
        var target = LockImpactAnalyzer.ClassifyDdl(statement, Key);
        Assert.NotNull(target);
        Assert.Equal("orders", target.Table);
        Assert.Equal(severity, Assert.Single(LockImpactAnalyzer.Rules(target, Key)).Severity);
    }

    [Fact]
    public void An_update_without_an_index_locks_every_scanned_row()
    {
        var targets = LockImpactAnalyzer.ParseMySqlExplain(["id", "select_type", "table", "type", "rows"],
            [[1L, "UPDATE", "orders", "ALL", 5000L]], "UPDATE orders SET status = 'x' WHERE note = 'y'");
        var target = Assert.Single(targets);
        Assert.Equal(5000, target.EstimatedRows);
        Assert.Contains("without an index", Assert.Single(LockImpactAnalyzer.Rules(target, Key)).Message);
        Assert.True(LockImpactAnalyzer.SameTable("shop.orders", "`orders`"));
    }

    // ----- Table designer -----

    private static TableDesign Customers() => new()
    {
        Database = "shop",
        Name = "customers",
        Columns =
        [
            new ColumnDesign { Name = "id", OriginalName = "id", Type = "int", IsPrimaryKey = true, IsIdentity = true, IsNullable = false },
            new ColumnDesign { Name = "name", OriginalName = "name", Type = "varchar", Size = "100", IsNullable = false }
        ]
    };

    [Fact]
    public void Create_script_lands_in_the_database_with_auto_increment()
    {
        var script = TableScriptBuilder.Build(Customers(), Key);
        Assert.StartsWith("CREATE TABLE `shop`.`customers` (", script);
        Assert.Contains("`id` int AUTO_INCREMENT NOT NULL", script);
    }

    [Fact]
    public void Alter_script_uses_modify_drop_foreign_key_and_drop_primary_key()
    {
        var original = Customers() with
        {
            Schema = "shop",
            PrimaryKeyName = "PRIMARY",
            ForeignKeys = [new ForeignKeyDesign { Name = "fk_x", Column = "name", ReferencedSchema = "shop", ReferencedTable = "names", ReferencedColumn = "n" }],
            Indexes = [new IndexDesign { Name = "ix_name", Columns = ["name"] }]
        };
        var edited = original with { ForeignKeys = [], Indexes = [] };
        edited = edited.WithColumn("name", c => c with { Size = "200", Default = "''" })
            .WithColumn("id", c => c with { IsIdentity = false, IsPrimaryKey = false })
            .WithColumn("name", c => c with { IsPrimaryKey = true });

        var steps = TableAlterScriptBuilder.Steps(original, edited, Key);
        Assert.Contains("ALTER TABLE `shop`.`customers` DROP FOREIGN KEY `fk_x`;", steps);
        Assert.Contains("DROP INDEX `ix_name` ON `shop`.`customers`;", steps);
        Assert.Contains("ALTER TABLE `shop`.`customers` MODIFY COLUMN `name` varchar(200) NOT NULL DEFAULT '';", steps);
        // AUTO_INCREMENT goes before the key it depends on.
        var dropAuto = steps.ToList().IndexOf("ALTER TABLE `shop`.`customers` MODIFY COLUMN `id` int NOT NULL;");
        var dropKey = steps.ToList().IndexOf("ALTER TABLE `shop`.`customers` DROP PRIMARY KEY;");
        Assert.True(dropAuto >= 0 && dropKey > dropAuto);
    }

    [Theory]
    [InlineData("int(11)", "int")]
    [InlineData("integer", "int")]
    [InlineData("boolean", "tinyint(1)")]
    [InlineData("decimal(10, 0)", "decimal(10)")]
    [InlineData("int unsigned", "int unsigned")]
    public void Respellings_are_not_type_changes(string type, string canonical) =>
        Assert.Equal(canonical, ColumnTypes.Canonical(Key, type));

    [Fact]
    public void Designer_rules_know_mysql()
    {
        var context = DesignContext.Empty(Key);
        var design = new TableDesign
        {
            Database = "shop",
            Name = "t",
            Columns =
            [
                new ColumnDesign { Name = "id", Type = "int", IsIdentity = true, IsNullable = false },
                new ColumnDesign { Name = "code", Type = "varchar" },
                new ColumnDesign { Name = "is_active", Type = "tinyint", Size = "1" }
            ]
        };
        var review = TableDesignAdvisor.Review(design, context);
        Assert.Contains(review, s => s.Key == "length:code" && s.Severity == DesignSeverity.Error);
        Assert.Contains(review, s => s.Key == "identity-key:id" && s.Severity == DesignSeverity.Error);
        Assert.DoesNotContain(review, s => s.Key == "flag:is_active");
        Assert.Equal(TypeFamily.Boolean, ColumnTypes.Family("tinyint", "1"));
        Assert.Equal("shop", TableScriptBuilder.SchemaOf(design, Key));
    }
}
