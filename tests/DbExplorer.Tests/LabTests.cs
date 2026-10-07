using DbExplorer.Application;
using DbExplorer.Application.Copy;
using DbExplorer.Application.Diagnostics;
using DbExplorer.Application.Lab;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.Query;
using DbExplorer.Core.Models;

namespace DbExplorer.Tests;

public class ChangeRecorderTests
{
    private static TableChangeCounter C(string table, long i, long u, long d) =>
        new() { Schema = "dbo", Table = table, Inserts = i, Updates = u, Deletes = d };

    [Fact]
    public void Diff_lists_tables_whose_counters_moved_busiest_first()
    {
        var before = new[] { C("A", 10, 5, 0), C("B", 1, 1, 1), C("C", 7, 0, 0) };
        var after = new[] { C("A", 12, 5, 0), C("B", 1, 9, 3), C("C", 7, 0, 0), C("New", 3, 0, 0) };

        var diff = ChangeRecorder.Diff(before, after);

        Assert.Equal(["B", "New", "A"], diff.Select(d => d.Table));
        Assert.Equal((0, 8, 2), (diff[0].Inserts, diff[0].Updates, diff[0].Deletes));
    }

    [Fact]
    public void Diff_treats_a_counter_that_went_down_as_reset()
    {
        var diff = ChangeRecorder.Diff([C("A", 100, 0, 0)], [C("A", 4, 0, 0)]);
        Assert.Equal(4, Assert.Single(diff).Inserts);
    }

    [Fact]
    public void DiffRows_finds_inserts_updates_and_deletes_by_key()
    {
        var table = new DbObject { Schema = "dbo", Name = "T", Type = DbObjectType.Table };
        string[] cols = ["Id", "Name", "Blob"];
        var before = new TableRows(table, cols, [
            [1, "a", new byte[] { 1 }],
            [2, "b", new byte[] { 2 }],
            [3, "c", null]
        ], true);
        var after = new TableRows(table, cols, [
            [1, "a", new byte[] { 1 }],
            [2, "B", new byte[] { 2 }],
            [4, "d", null]
        ], true);

        var changes = ChangeRecorder.DiffRows(table, before, after, ["Id"]);

        Assert.Equal(3, changes.Count);
        var updated = Assert.Single(changes, c => c.Kind == RowChangeKind.Updated);
        Assert.Equal("Id=2", updated.Key);
        Assert.Equal("Name", updated.ChangedColumns);
        Assert.Equal("Id=4", Assert.Single(changes, c => c.Kind == RowChangeKind.Inserted).Key);
        Assert.Equal("Id=3", Assert.Single(changes, c => c.Kind == RowChangeKind.Deleted).Key);
    }
}

public class SchemaHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbx-history-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static MetadataSnapshot WithModule(string definition, DateTimeOffset at, bool extraTable = false)
    {
        var shop = TestSnapshots.Shop();
        return new MetadataSnapshot
        {
            Objects = extraTable ? [.. shop.Objects, new DbObject { Schema = "dbo", Name = "Extra", Type = DbObjectType.Table }] : shop.Objects,
            Columns = shop.Columns,
            Modules = [new DbModule { Schema = "dbo", Name = "usp_GetCustomer", Type = DbObjectType.Procedure, Definition = definition }],
            ForeignKeys = shop.ForeignKeys,
            Indexes = shop.Indexes,
            RefreshedAt = at
        };
    }

    [Fact]
    public async Task Records_only_versions_that_differ_and_compares_them()
    {
        var store = new SchemaHistoryStore(new AppPaths(_root));
        var t0 = DateTimeOffset.Now.AddDays(-2);

        var v1 = await store.RecordAsync("k", WithModule("CREATE PROC p AS SELECT 1", t0));
        // Same catalog, different line endings / trailing blanks: no new version.
        Assert.Null(await store.RecordAsync("k", WithModule("CREATE PROC p AS SELECT 1  \r\n", t0.AddHours(1))));
        var v2 = await store.RecordAsync("k", WithModule("CREATE PROC p AS SELECT 2", t0.AddDays(1), extraTable: true));

        Assert.NotNull(v1);
        Assert.NotNull(v2);
        var versions = await store.GetVersionsAsync("k");
        Assert.Equal([v2!.Id, v1!.Id], versions.Select(v => v.Id));

        var changes = await store.CompareAsync("k", v1.Id, v2.Id);
        Assert.Contains(changes, c => c.Kind == SchemaChangeKind.Added && c.Name == "Extra");
        var changed = Assert.Single(changes, c => c.Kind == SchemaChangeKind.Changed);
        Assert.Equal("usp_GetCustomer", changed.Name);
        Assert.Equal("CREATE PROC p AS SELECT 1", await store.GetTextAsync("k", changed.BeforeHash));
        Assert.Equal("CREATE PROC p AS SELECT 2", await store.GetTextAsync("k", changed.AfterHash));

        var history = await store.GetObjectHistoryAsync("k", "", "dbo", "usp_getcustomer");
        Assert.Equal([SchemaChangeKind.Changed, SchemaChangeKind.Added], history.Select(h => h.Kind));
    }

    [Fact]
    public void Table_text_covers_columns_keys_and_ignores_virtual_foreign_keys()
    {
        var shop = TestSnapshots.Shop();
        var orders = shop.Objects.Single(o => o.Name == "Orders");
        var text = SchemaHistoryStore.DescribeTable(shop, orders);
        Assert.Contains("OrderId int NOT NULL PRIMARY KEY", text);
        Assert.Contains("FOREIGN KEY FK_Orders_Customers (CustomerId) REFERENCES dbo.Customers (CustomerId)", text);

        var withVirtual = shop.WithVirtualForeignKeys([new DbForeignKey
        {
            Name = "inferred", Schema = "sales", Table = "Orders", Columns = "OrderDate", ReferencedSchema = "dbo", ReferencedTable = "Audit", ReferencedColumns = "Id"
        }]);
        Assert.Equal(text, SchemaHistoryStore.DescribeTable(withVirtual, orders));
    }
}

public class RelationshipInferenceTests
{
    private static DbColumn Col(string table, string name, string type = "int", bool pk = false) =>
        new() { Schema = "dbo", Table = table, Name = name, DataType = type, BaseType = type, IsPrimaryKey = pk };

    private static DbObject T(string name) => new() { Schema = "dbo", Name = name, Type = DbObjectType.Table };

    [Theory]
    [InlineData("CustomerId", "Customers", "Id", true)]
    [InlineData("customer_id", "customers", "id", true)]
    [InlineData("CategoryId", "Categories", "Id", true)]
    [InlineData("CustomerId", "Customers", "CustomerId", true)]
    [InlineData("BillingCustomerId", "Customers", "Id", true)]
    [InlineData("Id", "Customers", "Id", false)]
    [InlineData("Status", "Customers", "Id", false)]
    [InlineData("OrderId", "Customers", "Id", false)]
    public void Name_heuristics(string column, string table, string key, bool match) =>
        Assert.Equal(match, RelationshipInference.NameScore(column, table, key, primaryKey: true).Score > 0);

    [Fact]
    public void Infers_undeclared_keys_with_compatible_types_only()
    {
        var snapshot = new MetadataSnapshot
        {
            Objects = [T("Customers"), T("Orders"), T("Tags")],
            Columns =
            [
                Col("Customers", "Id", pk: true),
                Col("Orders", "Id", pk: true),
                Col("Orders", "CustomerId"),
                Col("Orders", "TagId", "uniqueidentifier"),
                Col("Tags", "Id", pk: true)
            ],
            Modules = [],
            ForeignKeys = [],
            Indexes = [],
            RefreshedAt = DateTimeOffset.Now
        };

        var inferred = RelationshipInference.Infer(snapshot);

        var r = Assert.Single(inferred);
        Assert.Equal(("Orders", "CustomerId", "Customers", "Id"), (r.ChildTable, r.ChildColumn, r.ParentTable, r.ParentColumn));
        Assert.Equal("high", r.Confidence);
    }

    [Fact]
    public void Skips_declared_foreign_keys()
    {
        Assert.DoesNotContain(RelationshipInference.Infer(TestSnapshots.Shop()),
            r => r.ChildTable == "Orders" && r.ChildColumn == "CustomerId");
    }

    [Fact]
    public void Containment_sql_samples_and_probes_the_parent()
    {
        var r = new InferredRelationship("", "dbo", "Orders", "CustomerId", "dbo", "Customers", "Id", 90, "");
        var sql = RelationshipInference.ContainmentSql("SqlServer", r, 1000);
        Assert.Contains("SELECT TOP (1000) [CustomerId] AS v FROM [dbo].[Orders] WHERE [CustomerId] IS NOT NULL", sql);
        Assert.Contains("EXISTS (SELECT 1 FROM [dbo].[Customers] x WHERE x.[Id] = s.v)", sql);
        Assert.Contains("LIMIT 1000", RelationshipInference.ContainmentSql("Postgres", r, 1000));
    }
}

public class BlockingRecorderTests
{
    private static DbLock Waiting(int session, int blocker) =>
        new() { SessionId = session, BlockedBy = blocker, Status = "WAIT", LockMode = "X", ResourceType = "KEY", ObjectName = "dbo.Orders" };

    private static DbLock Held(int session) => new() { SessionId = session, Status = "GRANT", LockMode = "X", ResourceType = "KEY", ObjectName = "dbo.Orders" };

    [Fact]
    public void Groups_consecutive_blocked_samples_into_incidents()
    {
        var t = DateTimeOffset.Now;
        var recorder = new BlockingRecorder(capacity: 100);
        recorder.Add(t, [Held(1)]);
        recorder.Add(t.AddSeconds(5), [Held(1), Waiting(2, 1)]);
        recorder.Add(t.AddSeconds(10), [Held(1), Waiting(2, 1), Waiting(3, 1)]);
        recorder.Add(t.AddSeconds(15), [Held(1)]);
        recorder.Add(t.AddSeconds(20), [Held(7), Waiting(8, 7)]);
        // A gap (recording paused) ends the second incident.
        recorder.Add(t.AddSeconds(200), [Held(7), Waiting(8, 7)]);

        var incidents = recorder.Incidents(TimeSpan.FromSeconds(15));

        Assert.Equal(3, incidents.Count);
        var first = incidents[^1];
        Assert.Equal(t.AddSeconds(5), first.Start);
        Assert.Equal(t.AddSeconds(15), first.End);
        Assert.Equal(2, first.MaxWaitingSessions);
        Assert.Equal([1], first.HeadBlockers);
        Assert.Contains("dbo.Orders", first.Objects);
        Assert.Contains("Blocking tree at", BlockingRecorder.Report(first));
    }

    [Fact]
    public void Keeps_only_locks_of_involved_sessions_and_caps_samples()
    {
        Assert.Empty(BlockingRecorder.Relevant([Held(1), Held(2)]));
        Assert.Equal([1, 2], BlockingRecorder.Relevant([Held(1), Held(5), Waiting(2, 1)]).Select(l => l.SessionId).Order());

        var recorder = new BlockingRecorder(capacity: 3);
        for (var i = 0; i < 10; i++) recorder.Add(DateTimeOffset.Now.AddSeconds(i), []);
        Assert.Equal(3, recorder.Count);
    }
}

public class LockImpactTests
{
    [Theory]
    [InlineData("ALTER TABLE dbo.Orders ADD x int", "SqlServer", "dbo.Orders", "Sch-M")]
    [InlineData("TRUNCATE TABLE orders", "Postgres", "orders", "ACCESS EXCLUSIVE")]
    [InlineData("DROP TABLE IF EXISTS public.t", "Postgres", "public.t", "ACCESS EXCLUSIVE")]
    [InlineData("CREATE INDEX ix ON dbo.Orders (A)", "SqlServer", "dbo.Orders", "S table")]
    [InlineData("CREATE INDEX ix ON dbo.Orders (A) WITH (ONLINE = ON)", "SqlServer", "dbo.Orders", "row locks")]
    [InlineData("CREATE INDEX CONCURRENTLY ix ON orders (a)", "Postgres", "orders", "SHARE UPDATE EXCLUSIVE")]
    [InlineData("CREATE UNIQUE INDEX ix ON orders (a)", "Postgres", "orders", "SHARE")]
    public void Classifies_ddl(string sql, string provider, string table, string lockKind)
    {
        var t = LockImpactAnalyzer.ClassifyDdl(sql, provider)!;
        Assert.Equal((table, lockKind), (t.Table, t.LockKind));
    }

    [Fact]
    public void Dml_is_not_ddl() => Assert.Null(LockImpactAnalyzer.ClassifyDdl("UPDATE t SET a = 1", "Postgres"));

    [Fact]
    public void Escalation_rule_kicks_in_at_the_threshold()
    {
        var small = LockImpactAnalyzer.Rules(new LockTarget("dbo.T", "UPDATE", 100, "row locks"), "SqlServer").Single();
        var large = LockImpactAnalyzer.Rules(new LockTarget("dbo.T", "UPDATE", 20_000, "row locks"), "SqlServer").Single();
        Assert.Equal(ImpactSeverity.Info, small.Severity);
        Assert.Equal(ImpactSeverity.Danger, large.Severity);
        Assert.Contains("escalates", large.Message);
    }

    [Fact]
    public void Parses_postgres_modify_table_plans()
    {
        const string json = """
            [{"Plan": {"Node Type": "ModifyTable", "Operation": "Update", "Relation Name": "orders", "Schema": "public", "Plan Rows": 0,
                       "Plans": [{"Node Type": "Seq Scan", "Relation Name": "orders", "Plan Rows": 1234}]}}]
            """;
        var t = Assert.Single(LockImpactAnalyzer.ParsePostgresPlan(json));
        Assert.Equal(("public.orders", "UPDATE", 1234d), (t.Table, t.Operation, t.EstimatedRows));
    }

    [Fact]
    public void Parses_sql_server_showplan_write_operators()
    {
        string[] columns = ["StmtText", "PhysicalOp", "Argument", "EstimateRows"];
        IReadOnlyList<object?>[] rows =
        [
            ["UPDATE ...", null, null, 1d],
            ["|--Clustered Index Update", "Clustered Index Update", "OBJECT:([Shop].[dbo].[Orders].[PK_Orders]), SET:(...)", 8000d],
            ["|--Index Seek", "Index Seek", "OBJECT:([Shop].[dbo].[Orders].[IX_Status])", 8000d]
        ];
        var t = Assert.Single(LockImpactAnalyzer.ParseShowPlan(columns, rows));
        Assert.Equal(("dbo.Orders", "UPDATE", 8000d), (t.Table, t.Operation, t.EstimatedRows));
    }

    [Theory]
    [InlineData("dbo.Orders", "dbo.Orders", true)]
    [InlineData("orders", "public.orders", true)]
    [InlineData("[dbo].[Orders]", "dbo.Orders", true)]
    [InlineData("sales.Orders", "dbo.Orders", false)]
    [InlineData("dbo.OrderLines", "dbo.Orders", false)]
    public void Matches_lock_objects_to_tables(string lockObject, string target, bool same) =>
        Assert.Equal(same, LockImpactAnalyzer.SameTable(lockObject, target));

    [Fact]
    public void Detects_locking_reads()
    {
        Assert.True(LockImpactAnalyzer.LocksOnRead("SELECT * FROM t WHERE id = 1 FOR UPDATE", "Postgres"));
        Assert.True(LockImpactAnalyzer.LocksOnRead("SELECT * FROM t WITH (UPDLOCK) WHERE id = 1", "SqlServer"));
        Assert.False(LockImpactAnalyzer.LocksOnRead("SELECT * FROM t", "Postgres"));
    }
}

public class DryRunTests
{
    [Fact]
    public void Before_select_reuses_target_from_and_where()
    {
        var ss = SqlAnatomy.ParseDml("UPDATE o SET Status = 1 FROM sales.Orders o JOIN dbo.Customers c ON c.CustomerId = o.CustomerId WHERE c.Name = 'x'")!;
        Assert.Equal("SELECT TOP (501) o.* FROM sales.Orders o JOIN dbo.Customers c ON c.CustomerId = o.CustomerId WHERE c.Name = 'x'",
            DryRunService.BeforeSelect(ss, SqlDialect.SqlServer, 500));

        var pg = SqlAnatomy.ParseDml("DELETE FROM orders o USING customers c WHERE c.id = o.customer_id")!;
        Assert.Equal("SELECT o.* FROM orders AS o, customers c WHERE c.id = o.customer_id LIMIT 501",
            DryRunService.BeforeSelect(pg, SqlDialect.Postgres, 500));

        var plain = SqlAnatomy.ParseDml("UPDATE TOP (5) dbo.Customers SET Name = 'x' WHERE Email IS NULL")!;
        Assert.Equal("SELECT TOP (5) * FROM dbo.Customers WHERE Email IS NULL", DryRunService.BeforeSelect(plain, SqlDialect.SqlServer, 500));
    }

    [Fact]
    public void Resolves_the_target_through_an_alias()
    {
        var dml = SqlAnatomy.ParseDml("UPDATE o SET OrderDate = NULL FROM sales.Orders o WHERE o.OrderId = 1")!;
        var table = DryRunService.ResolveTarget(TestSnapshots.Shop(), dml);
        Assert.Equal("sales.Orders", table?.FullName);
    }

    [Fact]
    public void After_select_reads_rows_back_by_key()
    {
        var table = new DbObject { Schema = "sales", Name = "OrderLines", Type = DbObjectType.Table };
        var before = new QueryResultSet { Columns = ["OrderId", "LineNo", "Qty"], Rows = [[1, 2, 5], [1, 3, 7]] };
        Assert.Equal("SELECT * FROM [sales].[OrderLines] WHERE ([OrderId] = 1 AND [LineNo] = 2) OR ([OrderId] = 1 AND [LineNo] = 3)",
            DryRunService.AfterSelect(SqlDialect.SqlServer, table, ["OrderId", "LineNo"], before));
    }

    [Fact]
    public void After_select_writes_a_key_in_its_column_type()
    {
        var table = new DbObject { Schema = "dbo", Name = "Log", Type = DbObjectType.Table };
        var before = new QueryResultSet { Columns = ["At"], Rows = [[new DateTime(2025, 3, 4, 10, 11, 12, 997)]] };
        Assert.Equal("SELECT * FROM [dbo].[Log] WHERE ([At] = '2025-03-04T10:11:12.997')",
            DryRunService.AfterSelect(SqlDialect.SqlServer, table, ["At"], before, ["datetime"]));
    }

    [Theory]
    [InlineData("BEGIN; UPDATE t SET a = 1; COMMIT;", "BEGIN")]
    [InlineData("UPDATE t SET a = 1;\nCOMMIT", "COMMIT")]
    [InlineData("UPDATE t SET a = 1\nGO\nROLLBACK TRANSACTION", "ROLLBACK TRANSACTION")]
    [InlineData("START TRANSACTION; DELETE FROM t", "START TRANSACTION")]
    public void Refuses_scripts_with_transaction_statements(string script, string keyword)
    {
        var refused = DryRunService.RefuseTransactionControl(DryRunService.SplitScript(script));
        Assert.NotNull(refused);
        Assert.Contains($"can't include {keyword} ", refused.Error);
        Assert.Contains("rolls back", refused.Error);
    }

    [Theory]
    [InlineData("UPDATE t SET a = 1; DELETE FROM t WHERE note = 'commit'")]
    [InlineData("IF 1 = 1\nBEGIN\n  UPDATE t SET a = 1\nEND")]
    [InlineData("SAVEPOINT a; UPDATE t SET a = 1")]
    public void Runs_scripts_without_transaction_statements(string script) =>
        Assert.Null(DryRunService.RefuseTransactionControl(DryRunService.SplitScript(script)));
}

public class WhyNotTests
{
    [Fact]
    public void Anchor_is_the_source_the_expected_row_condition_names()
    {
        var q = SqlAnatomy.ParseSelect("SELECT * FROM sales.Orders o JOIN dbo.Customers c ON c.CustomerId = o.CustomerId")!;
        Assert.Equal(1, WhyNotDebugger.FindAnchor(q, "c.CustomerId = 5"));
        Assert.Equal(0, WhyNotDebugger.FindAnchor(q, "o.OrderId = 1"));
        Assert.Equal(0, WhyNotDebugger.FindAnchor(q, "OrderId = 1"));
    }
}

public class ValueCompletionTests
{
    private static SqlCompletionEngine Engine() => new(TestSnapshots.Shop(), id => "[" + id + "]", "SqlServer")
    {
        ValueSource = (table, column) => column.Name switch
        {
            "Name" => [new ValueFrequency { Value = "Acme", Count = 40 }, new ValueFrequency { Value = "O'Brien", Count = 3 }],
            "CustomerId" => [new ValueFrequency { Value = "42", Count = 9 }],
            _ => null
        }
    };

    [Fact]
    public void Suggests_values_after_an_equals()
    {
        const string sql = "SELECT * FROM dbo.Customers c WHERE c.Name = ";
        var result = Engine().Complete(sql, sql.Length);
        Assert.Equal(["'Acme'", "'O''Brien'"], result.Items.Select(i => i.Label));
        Assert.All(result.Items, i => Assert.Equal(CompletionKind.Value, i.Kind));
    }

    [Fact]
    public void Replaces_a_half_typed_string_and_filters_on_it()
    {
        const string sql = "SELECT * FROM dbo.Customers WHERE Name = 'O'";
        var text = sql[..^1]; // "… = 'O"
        var result = Engine().Complete(text, text.Length);
        var item = Assert.Single(result.Items);
        Assert.Equal("'O''Brien'", item.InsertText);
        Assert.Equal(text.Length - 2, result.ReplaceStart);
    }

    [Fact]
    public void Numbers_are_not_quoted_and_in_lists_work()
    {
        const string sql = "SELECT * FROM sales.Orders o WHERE o.CustomerId IN (1, ";
        var item = Assert.Single(Engine().Complete(sql, sql.Length).Items);
        Assert.Equal("42", item.InsertText);
    }

    [Fact]
    public void Falls_back_to_normal_completion_without_values()
    {
        const string sql = "SELECT * FROM sales.Orders o WHERE o.OrderDate = o.";
        Assert.All(Engine().Complete(sql, sql.Length).Items, i => Assert.Equal(CompletionKind.Column, i.Kind));
    }
}

public class VirtualForeignKeyDiagramTests
{
    [Fact]
    public void Accepted_relationships_are_dotted_in_mermaid()
    {
        var snapshot = TestSnapshots.Shop().WithVirtualForeignKeys([new DbForeignKey
        {
            Name = "inferred_Audit_Id", Schema = "dbo", Table = "Audit", Columns = "Id",
            ReferencedSchema = "dbo", ReferencedTable = "Customers", ReferencedColumns = "CustomerId"
        }]);
        var customers = snapshot.Objects.Single(o => o.Name == "Customers");
        var mermaid = DbExplorer.Application.Diagram.ErDiagramBuilder.ToMermaid(
            DbExplorer.Application.Diagram.ErDiagramBuilder.AroundTable(snapshot, customers, 1, DbExplorer.Application.Diagram.ErColumnMode.KeysOnly));
        Assert.Contains("dbo_Customers ||..o{ dbo_Audit", mermaid);
        Assert.Contains("dbo_Customers ||--o{ sales_Orders", mermaid);
    }
}
