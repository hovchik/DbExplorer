using DbExplorer.Application.Copy;
using DbExplorer.Application.TestData;
using DbExplorer.Core.Models;

namespace DbExplorer.Tests;

public sealed class TestDataTests
{
    private static readonly DateTime Today = new(2026, 11, 2);

    private static DbObject Table(string name, string schema = "public") => new() { Schema = schema, Name = name, Type = DbObjectType.Table };

    private static DbColumn Col(string name, string dataType, string baseType, int ordinal, bool nullable = false,
        bool pk = false, bool identity = false, bool computed = false, string table = "customers", string schema = "public") => new()
    {
        Schema = schema, Table = table, Name = name, DataType = dataType, BaseType = baseType, Ordinal = ordinal,
        IsNullable = nullable, IsPrimaryKey = pk, IsIdentity = identity, IsComputed = computed
    };

    private static DbTableConstraints Checks(params (string Name, string Expression)[] checks) =>
        new() { Checks = checks.Select(c => new DbCheckConstraint(c.Name, c.Expression)).ToList() };

    private static TestDataPlan Customers(string provider = SqlDialect.PostgresKey, DbTableConstraints? constraints = null) =>
        TestDataPlanner.Plan(provider, Table("customers"),
        [
            Col("id", "integer", "int4", 1, pk: true, identity: true),
            Col("first_name", "character varying(40)", "varchar", 2),
            Col("last_name", "character varying(40)", "varchar", 3),
            Col("email", "character varying(120)", "varchar", 4),
            Col("phone", "character varying(30)", "varchar", 5, nullable: true),
            Col("status", "character varying(20)", "varchar", 6),
            Col("balance", "numeric(10,2)", "numeric", 7),
            Col("created_at", "timestamp with time zone", "timestamptz", 8),
            Col("is_active", "boolean", "bool", 9),
            Col("notes", "text", "text", 10, nullable: true),
            Col("code", "character(6)", "bpchar", 11),
        ],
        [],
        [new DbIndex { Schema = "public", Table = "customers", Name = "customers_email_key", IsUnique = true, Columns = "email" }],
        constraints ?? DbTableConstraints.None);

    private static TestColumnPlan ColumnOf(TestDataPlan plan, string name) => plan.Columns.Single(c => c.Name == name);

    [Fact]
    public void Columns_are_filled_by_what_their_name_and_type_suggest()
    {
        var plan = Customers();
        Assert.Equal(TestValueKind.ServerDefault, ColumnOf(plan, "id").Kind);
        Assert.Equal(TestValueKind.FirstName, ColumnOf(plan, "first_name").Kind);
        Assert.Equal(TestValueKind.LastName, ColumnOf(plan, "last_name").Kind);
        Assert.Equal(TestValueKind.Email, ColumnOf(plan, "email").Kind);
        Assert.True(ColumnOf(plan, "email").Unique);
        Assert.Equal(TestValueKind.Phone, ColumnOf(plan, "phone").Kind);
        Assert.Equal(TestValueKind.OneOf, ColumnOf(plan, "status").Kind);
        Assert.Equal(TestValueKind.Price, ColumnOf(plan, "balance").Kind);
        Assert.Equal(TestValueKind.DateTime, ColumnOf(plan, "created_at").Kind);
        Assert.Equal(TestValueKind.Boolean, ColumnOf(plan, "is_active").Kind);
        Assert.Equal(TestValueKind.Paragraph, ColumnOf(plan, "notes").Kind);
        Assert.Equal(TestValueKind.Code, ColumnOf(plan, "code").Kind);
        Assert.Equal(6, ColumnOf(plan, "code").MaxLength);
        Assert.Equal(10, ColumnOf(plan, "balance").Precision);
        Assert.Equal(2, ColumnOf(plan, "balance").Scale);
    }

    [Fact]
    public void The_name_column_of_a_people_table_gets_full_names_and_of_other_tables_product_names()
    {
        var people = TestDataPlanner.Plan(SqlDialect.SqlServerKey, Table("Employees", "dbo"),
            [Col("Name", "nvarchar(100)", "nvarchar", 1, table: "Employees", schema: "dbo")], [], [], DbTableConstraints.None);
        var things = TestDataPlanner.Plan(SqlDialect.SqlServerKey, Table("Products", "dbo"),
            [Col("Name", "nvarchar(100)", "nvarchar", 1, table: "Products", schema: "dbo")], [], [], DbTableConstraints.None);
        Assert.Equal(TestValueKind.FullName, people.Columns[0].Kind);
        Assert.Equal(TestValueKind.ProductName, things.Columns[0].Kind);
    }

    [Fact]
    public void A_check_with_a_list_of_values_limits_the_column_to_that_list()
    {
        // PostgreSQL stores IN (...) as = ANY (ARRAY[...]) with casts.
        var plan = Customers(constraints: Checks(("customers_status_check",
            "((status)::text = ANY ((ARRAY['new'::character varying, 'vip'::character varying, 'closed'::character varying])::text[]))")));
        var status = ColumnOf(plan, "status");
        Assert.Equal(TestValueKind.OneOf, status.Kind);
        Assert.Equal(["new", "vip", "closed"], status.Choices);

        var rows = TestDataGenerator.Generate(plan, 200, seed: 7, today: Today);
        var index = rows.Columns.ToList().FindIndex(c => c.Name == "status");
        Assert.All(rows.Rows, r => Assert.Contains((string)r[index]!, status.Choices));
        Assert.Empty(plan.Warnings);
    }

    [Theory]
    [InlineData("([Qty]>(0))", 0, true, null, false)]
    [InlineData("([Qty]>=(1) AND [Qty]<=(50))", 1, false, 50, false)]
    [InlineData("(qty BETWEEN 5 AND 9)", 5, false, 9, false)]
    [InlineData("((qty >= (10)::numeric))", 10, false, null, false)]
    [InlineData("(100 > qty)", null, false, 100, true)]
    public void Numeric_checks_become_bounds(string expression, int? min, bool minExclusive, int? max, bool maxExclusive)
    {
        var plan = TestDataPlanner.Plan(SqlDialect.SqlServerKey, Table("Lines", "dbo"),
            [Col("Qty", "int", "int", 1, table: "Lines", schema: "dbo")], [], [], Checks(("ck", expression)));
        var qty = plan.Columns[0];
        Assert.Empty(plan.Warnings);
        if (min is not null) { Assert.Equal(min, (int?)qty.Min); Assert.Equal(minExclusive, qty.MinExclusive); }
        if (max is not null) { Assert.Equal(max, (int?)qty.Max); Assert.Equal(maxExclusive, qty.MaxExclusive); }

        var rows = TestDataGenerator.Generate(plan, 300, seed: 3, today: Today);
        foreach (var row in rows.Rows)
        {
            var value = (long)row[0]!;
            if (min is not null) Assert.True(minExclusive ? value > min : value >= min, $"{value} vs min {min}");
            if (max is not null) Assert.True(maxExclusive ? value < max : value <= max, $"{value} vs max {max}");
        }
    }

    [Fact]
    public void Checks_it_cannot_read_are_listed_as_warnings()
    {
        var plan = Customers(constraints: Checks(("email_shape", "((email)::text ~~ '%@%'::text)"), ("phone_len", "(length((phone)::text) > 5)")));
        Assert.Contains(plan.Warnings, w => w.Contains("email_shape"));
        Assert.Contains(plan.Warnings, w => w.Contains("phone_len"));
    }

    [Fact]
    public void Checks_that_only_ask_for_non_empty_text_need_no_warning()
    {
        var plan = Customers(constraints: Checks(("first_name_not_blank", "(btrim((first_name)::text) <> ''::text)")));
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Two_column_ordering_checks_are_kept_in_every_row()
    {
        var plan = TestDataPlanner.Plan(SqlDialect.PostgresKey, Table("bookings"),
        [
            Col("starts_on", "date", "date", 1, table: "bookings"),
            Col("ends_on", "date", "date", 2, table: "bookings"),
        ], [], [], Checks(("bookings_check", "((ends_on > starts_on))")));
        Assert.Single(plan.Orders);
        Assert.Empty(plan.Warnings);
        var rows = TestDataGenerator.Generate(plan, 300, seed: 11, today: Today);
        Assert.All(rows.Rows, r => Assert.True((DateOnly)r[1]! > (DateOnly)r[0]!));
    }

    [Fact]
    public void The_same_seed_gives_the_same_rows_and_a_preview_is_the_start_of_the_full_run()
    {
        var plan = Customers();
        var a = TestDataGenerator.Generate(plan, 500, seed: 42, today: Today);
        var b = TestDataGenerator.Generate(plan, 500, seed: 42, today: Today);
        var preview = TestDataGenerator.Generate(plan, 20, seed: 42, today: Today);
        Assert.Equal(Flatten(a), Flatten(b));
        Assert.Equal(Flatten(preview), Flatten(a).Take(20));
        Assert.NotEqual(Flatten(a), Flatten(TestDataGenerator.Generate(plan, 500, seed: 43, today: Today)));

        static IEnumerable<string> Flatten(TestDataRows rows) =>
            rows.Rows.Select(r => string.Join("|", r.Select(TestDataGenerator.KeyText)));
    }

    [Fact]
    public void Values_fit_their_columns_and_unique_ones_never_repeat()
    {
        var plan = Customers();
        var rows = TestDataGenerator.Generate(plan, 2000, seed: 5, nullPercent: 0, today: Today);
        Assert.Equal(2000, rows.Rows.Count);
        Assert.DoesNotContain(rows.Columns, c => c.Name == "id"); // identity: left to the server

        int At(string name) => rows.Columns.ToList().FindIndex(c => c.Name == name);
        var emails = rows.Rows.Select(r => ((string)r[At("email")]!).ToLowerInvariant()).ToList();
        Assert.Equal(emails.Count, emails.Distinct().Count());
        Assert.All(emails, e => Assert.Contains("@", e));
        Assert.All(rows.Rows, r => Assert.True(((string)r[At("code")]!).Length <= 6));
        Assert.All(rows.Rows, r => Assert.True(((string)r[At("first_name")]!).Length <= 40));
        Assert.All(rows.Rows, r => Assert.InRange((decimal)r[At("balance")]!, 1m, 1000m));
        Assert.All(rows.Rows, r => Assert.InRange((DateTime)r[At("created_at")]!, Today.AddDays(-731), Today.AddDays(1)));
        Assert.All(rows.Rows, r => Assert.IsType<bool>(r[At("is_active")]));
    }

    [Fact]
    public void Nullable_columns_are_sometimes_null_and_never_when_the_share_is_zero()
    {
        var plan = Customers();
        int Phone(TestDataRows r) => r.Columns.ToList().FindIndex(c => c.Name == "phone");
        var some = TestDataGenerator.Generate(plan, 1000, seed: 9, nullPercent: 30, today: Today);
        var none = TestDataGenerator.Generate(plan, 1000, seed: 9, nullPercent: 0, today: Today);
        Assert.InRange(some.Rows.Count(r => r[Phone(some)] is null), 200, 400);
        Assert.DoesNotContain(none.Rows, r => r[Phone(none)] is null);
        // Not-null columns never get one.
        var email = some.Columns.ToList().FindIndex(c => c.Name == "email");
        Assert.DoesNotContain(some.Rows, r => r[email] is null);
    }

    private static TestDataPlan Orders(IReadOnlyList<object?[]> customers, bool customerNullable = false, bool oneToOne = false)
    {
        var fk = new DbForeignKey
        {
            Name = "orders_customer_fk", Schema = "public", Table = "orders", Columns = "customer_id",
            ReferencedSchema = "public", ReferencedTable = "customers", ReferencedColumns = "id"
        };
        var plan = TestDataPlanner.Plan(SqlDialect.PostgresKey, Table("orders"),
        [
            Col("id", "bigint", "int8", 1, pk: true, table: "orders"),
            Col("customer_id", "integer", "int4", 2, nullable: customerNullable, table: "orders"),
            Col("quantity", "integer", "int4", 3, table: "orders"),
        ], [fk],
        oneToOne ? [new DbIndex { Schema = "public", Table = "orders", Name = "one_order_each", IsUnique = true, Columns = "customer_id" }] : [],
        DbTableConstraints.None);
        return plan with
        {
            References = [plan.References[0] with { Rows = customers }],
            Columns = plan.Columns.Select(c => c.Name == "id" ? c with { SequenceStart = 501 } : c).ToList()
        };
    }

    [Fact]
    public void Foreign_keys_point_at_sampled_parent_rows_and_a_non_identity_key_continues_after_the_largest()
    {
        var plan = Orders([[1], [2], [3]]);
        Assert.Equal(TestValueKind.Reference, ColumnOf(plan, "customer_id").Kind);
        Assert.Equal(TestValueKind.Sequence, ColumnOf(plan, "id").Kind);
        Assert.Equal(TestValueKind.Quantity, ColumnOf(plan, "quantity").Kind);

        var rows = TestDataGenerator.Generate(plan, 50, seed: 1, today: Today);
        Assert.Equal(Enumerable.Range(501, 50).Select(i => (long)i), rows.Rows.Select(r => (long)r[0]!));
        Assert.All(rows.Rows, r => Assert.Contains((int)r[1]!, new[] { 1, 2, 3 }));
    }

    [Fact]
    public void An_empty_parent_stops_a_required_key_and_leaves_a_nullable_one_null()
    {
        var required = TestDataGenerator.Generate(Orders([]), 10, seed: 1, today: Today);
        Assert.Empty(required.Rows);
        Assert.Contains(required.Notes, n => n.Contains("public.customers has no rows"));

        var optional = TestDataGenerator.Generate(Orders([], customerNullable: true), 10, seed: 1, today: Today);
        Assert.Equal(10, optional.Rows.Count);
        Assert.All(optional.Rows, r => Assert.Null(r[1]));
    }

    [Fact]
    public void A_one_to_one_key_uses_each_parent_once_and_reports_the_shortfall()
    {
        var plan = Orders([[1], [2], [3], [4]], oneToOne: true);
        var rows = TestDataGenerator.Generate(plan, 10, seed: 2, today: Today);
        Assert.Equal(4, rows.Rows.Count);
        Assert.Equal(6, rows.Shortfall);
        Assert.Equal(4, rows.Rows.Select(r => r[1]).Distinct().Count());
        Assert.Contains(rows.Notes, n => n.Contains("Only 4"));
    }

    [Fact]
    public void A_junction_table_never_repeats_a_pair_nor_one_already_in_the_table()
    {
        DbForeignKey Fk(string column, string parent) => new()
        {
            Name = column + "_fk", Schema = "public", Table = "order_items", Columns = column,
            ReferencedSchema = "public", ReferencedTable = parent, ReferencedColumns = "id"
        };
        var plan = TestDataPlanner.Plan(SqlDialect.PostgresKey, Table("order_items"),
        [
            Col("order_id", "integer", "int4", 1, pk: true, table: "order_items"),
            Col("product_id", "integer", "int4", 2, pk: true, table: "order_items"),
        ], [Fk("order_id", "orders"), Fk("product_id", "products")], [], DbTableConstraints.None);
        Assert.Single(plan.UniqueKeys);
        plan = plan with
        {
            References = [plan.References[0] with { Rows = [[1], [2], [3]] }, plan.References[1] with { Rows = [[10], [20]] }],
            UniqueKeys = [plan.UniqueKeys[0] with { Existing = new HashSet<string> { "1\u001f10\u001f" } }]
        };

        var rows = TestDataGenerator.Generate(plan, 20, seed: 4, today: Today);
        var pairs = rows.Rows.Select(r => $"{r[0]}-{r[1]}").ToList();
        Assert.Equal(5, pairs.Count); // 3 × 2 pairs, one already taken
        Assert.Equal(pairs.Count, pairs.Distinct().Count());
        Assert.DoesNotContain("1-10", pairs);
        Assert.Equal(15, rows.Shortfall);
    }

    [Fact]
    public void Inserts_are_batched_in_the_engines_syntax()
    {
        var plan = Customers(SqlDialect.SqlServerKey) with { Table = Table("Customers", "dbo") };
        var rows = TestDataGenerator.Generate(plan, 250, seed: 8, today: Today);
        var statements = TestDataScript.Statements(SqlDialect.SqlServerKey, plan.Table, rows);
        Assert.Equal(3, statements.Count);
        Assert.StartsWith("INSERT INTO [dbo].[Customers] ([first_name], [last_name], [email]", statements[0]);
        Assert.Contains("N'", statements[0]);
        Assert.EndsWith(");", statements[2]);
        Assert.Equal(50, statements[2].Split('\n').Length - 1);

        var script = TestDataScript.Script(SqlDialect.SqlServerKey, plan.Table, rows, 8);
        Assert.Contains("BEGIN TRANSACTION;", script);
        Assert.Contains("COMMIT TRANSACTION;", script);
        Assert.StartsWith("-- 250 generated test row(s) for [dbo].[Customers] (seed 8).", script);
    }

    [Fact]
    public void A_table_where_the_server_fills_every_column_gets_default_rows()
    {
        var plan = TestDataPlanner.Plan(SqlDialect.MySqlKey, Table("ticks", "shop"),
            [Col("id", "int", "int", 1, pk: true, identity: true, table: "ticks", schema: "shop")], [], [], DbTableConstraints.None);
        var rows = TestDataGenerator.Generate(plan, 3, seed: 1, today: Today);
        Assert.Equal("INSERT INTO `shop`.`ticks` () VALUES\n  (),\n  (),\n  ();",
            Assert.Single(TestDataScript.Statements(SqlDialect.MySqlKey, plan.Table, rows)));
        var pg = TestDataScript.Statements(SqlDialect.PostgresKey, Table("ticks"), rows);
        Assert.Equal(3, pg.Count);
        Assert.All(pg, s => Assert.Equal("INSERT INTO \"public\".\"ticks\" DEFAULT VALUES;", s));
    }

    [Fact]
    public void MySql_enums_and_unsigned_tinyints_are_respected()
    {
        var plan = TestDataPlanner.Plan(SqlDialect.MySqlKey, Table("items", "shop"),
        [
            Col("size", "enum('S','M','L')", "enum", 1, table: "items", schema: "shop"),
            Col("rank", "tinyint unsigned", "tinyint", 2, table: "items", schema: "shop"),
            Col("is_new", "tinyint(1)", "tinyint", 3, table: "items", schema: "shop"),
        ], [], [], DbTableConstraints.None);
        Assert.Equal(TestValueKind.OneOf, plan.Columns[0].Kind);
        Assert.Equal(["S", "M", "L"], plan.Columns[0].Choices);
        Assert.Equal(TestValueKind.Boolean, plan.Columns[2].Kind);
        var rows = TestDataGenerator.Generate(plan, 200, seed: 6, today: Today);
        Assert.All(rows.Rows, r => Assert.InRange((long)r[1]!, 0, 255));
    }

    [Fact]
    public void A_unique_column_with_a_default_is_left_to_the_server_so_its_sequence_stays_ahead()
    {
        var plan = TestDataPlanner.Plan(SqlDialect.PostgresKey, Table("products"),
        [
            Col("id", "integer", "int4", 1, pk: true, table: "products"),
            Col("price", "numeric(10,2)", "numeric", 2, table: "products"),
        ], [], [], new DbTableConstraints
        {
            Defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["id"] = "nextval('products_id_seq'::regclass)" }
        });
        Assert.Equal(TestValueKind.ServerDefault, plan.Columns[0].Kind);
        Assert.Contains(TestValueKind.Sequence, plan.Columns[0].Options);
        var rows = TestDataGenerator.Generate(plan, 3, seed: 1, today: Today);
        Assert.Equal(["price"], rows.Columns.Select(c => c.Name));
    }

    [Fact]
    public void Columns_with_no_generator_must_be_chosen_before_the_insert()
    {
        var plan = TestDataPlanner.Plan(SqlDialect.PostgresKey, Table("shapes"),
            [Col("area", "geometry", "geometry", 1, table: "shapes")], [], [], DbTableConstraints.None);
        var area = plan.Columns[0];
        Assert.Equal(TestValueKind.ServerDefault, area.Kind);
        Assert.Contains("pick one or type a fixed value", area.Note);
        Assert.Contains(TestValueKind.Fixed, area.Options);

        var fixedPlan = plan.WithColumn(area with { Kind = TestValueKind.Fixed, FixedValue = "POINT(1 1)" });
        var rows = TestDataGenerator.Generate(fixedPlan, 2, seed: 1, today: Today);
        Assert.Equal("INSERT INTO \"public\".\"shapes\" (\"area\") VALUES\n  ('POINT(1 1)'),\n  ('POINT(1 1)');",
            Assert.Single(TestDataScript.Statements(SqlDialect.PostgresKey, fixedPlan.Table, rows)));
    }
}
