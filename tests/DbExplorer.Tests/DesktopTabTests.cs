using System.Reflection;
using DbExplorer.Application.Metadata;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Abstractions;
using DbExplorer.Core.Connections;
using DbExplorer.Core.Models;
using DbExplorer.Desktop.Services;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Tests;

/// <summary>Desktop tabs that read from the session: a reconnect replaces the session, and whatever the old one still
/// returns afterwards must not land in the tab (nor keep the new session from loading).</summary>
public class DesktopTabTests
{
    [Fact]
    public async Task Indexes_drop_a_load_of_the_previous_session_and_do_not_block_the_new_one()
    {
        var slow = new TaskCompletionSource<IReadOnlyList<DbIndex>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = Session(ScriptedProvider.Create(new() { [nameof(IDatabaseProvider.GetIndexesAsync)] = _ => slow.Task }));
        var fresh = Session(ScriptedProvider.Create(new()
        {
            [nameof(IDatabaseProvider.GetIndexesAsync)] = _ => Task.FromResult<IReadOnlyList<DbIndex>>([Index("fresh")])
        }));
        var vm = new IndexesViewModel();

        vm.Attach(old);
        var oldLoad = vm.LoadCommand.ExecuteAsync(null);
        Assert.True(vm.IsLoading);

        vm.Attach(fresh);
        Assert.False(vm.IsLoading);
        Assert.True(vm.LoadCommand.CanExecute(null));
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal(["fresh"], vm.Indexes.Select(i => i.Name));

        slow.SetResult([Index("old")]);
        await oldLoad;
        Assert.Equal(["fresh"], vm.Indexes.Select(i => i.Name));
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task Activity_keeps_the_newest_top_query_load_when_an_older_one_finishes_last()
    {
        var pending = new Queue<TaskCompletionSource<IReadOnlyList<DbQueryStat>>>();
        var session = Session(ScriptedProvider.Create(new()
        {
            [nameof(IDatabaseProvider.GetTopQueriesAsync)] = _ =>
            {
                var tcs = new TaskCompletionSource<IReadOnlyList<DbQueryStat>>(TaskCreationOptions.RunContinuationsAsynchronously);
                pending.Enqueue(tcs);
                return tcs.Task;
            }
        }));
        var vm = new ActivityViewModel();
        vm.Attach(session);

        var first = vm.LoadTopQueriesCommand.ExecuteAsync(null);
        var second = vm.LoadTopQueriesCommand.ExecuteAsync(null);
        var (older, newer) = (pending.Dequeue(), pending.Dequeue());
        newer.SetResult([new DbQueryStat { SqlText = "newer" }]);
        await second;
        older.SetResult([new DbQueryStat { SqlText = "older" }]);
        await first;

        Assert.Equal(["newer"], vm.TopQueries.Select(q => q.SqlText));
        Assert.False(vm.IsLoadingTopQueries);
    }

    [Fact]
    public async Task Security_apply_takes_out_only_the_changes_it_ran()
    {
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = new BlockingScriptSession(running, release);
        var (vm, dialogs) = await SecurityTab(script);

        dialogs.Names.Enqueue("first");
        await vm.NewRoleCommand.ExecuteAsync(null);
        var apply = vm.ApplyCommand.ExecuteAsync(null);
        await running.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(vm.IsBusy);

        dialogs.Names.Enqueue("second");
        await vm.NewRoleCommand.ExecuteAsync(null);
        release.SetResult();
        await apply.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains(script.Executed, s => s.Contains("first") && !s.Contains("second"));
        Assert.Equal(["Create role second"], vm.Pending.Select(p => p.Summary));
        Assert.StartsWith("Applied 1 change(s).", vm.Status);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Security_drops_database_level_changes_when_another_database_is_picked()
    {
        var (vm, dialogs) = await SecurityTab(new BlockingScriptSession(null, null));
        dialogs.Names.Enqueue("reporting");
        await vm.NewRoleCommand.ExecuteAsync(null);
        vm.SelectedPrincipal = vm.Principals.Single(p => p.Name == "reporting");
        vm.GrantTarget = "DATABASE";
        vm.GrantPermission = "CONNECT";
        vm.GrantCommand.Execute(null);
        Assert.Equal(2, vm.Pending.Count);

        vm.SelectedDatabase = "other";
        await vm.RefreshCommand.ExecuteAsync(null);

        // On PostgreSQL a role belongs to the server; a privilege on the database belongs to that database.
        Assert.Equal(["Create role reporting"], vm.Pending.Select(p => p.Summary));
        Assert.Contains("inside the previous database were dropped", vm.Status);
    }

    [Fact]
    public async Task Security_on_MySQL_does_not_leave_the_spinner_of_a_previous_load_running()
    {
        var hang = new TaskCompletionSource<QueryResultSet>();
        var postgres = Session(ScriptedProvider.Create(new()
        {
            [nameof(IDatabaseProvider.QueryReadOnlyAsync)] = _ => hang.Task
        }, Application.Copy.SqlDialect.PostgresKey), Application.Copy.SqlDialect.PostgresKey);
        var mySql = Session(ScriptedProvider.Create(new(), Application.Copy.SqlDialect.MySqlKey), Application.Copy.SqlDialect.MySqlKey);
        var vm = new SecurityViewModel(new SessionService(null!, null!, null!), DialogFake.Create(out _));

        vm.Attach(postgres);
        Assert.True(vm.IsBusy);
        vm.Attach(mySql);

        Assert.False(vm.IsBusy);
        Assert.Equal(Application.Security.SecurityCatalogLoader.MySqlNotSupported, vm.Status);
    }

    /// <summary>A PostgreSQL Security tab on database "main" (the server also has "other") with an empty catalog.</summary>
    private static async Task<(SecurityViewModel Vm, DialogFake Dialogs)> SecurityTab(IScriptSession script)
    {
        const string postgres = Application.Copy.SqlDialect.PostgresKey;
        var provider = ScriptedProvider.Create(new()
        {
            [nameof(IDatabaseProvider.QueryReadOnlyAsync)] = _ => Task.FromException<QueryResultSet>(new InvalidOperationException("no catalog here")),
            [nameof(IDatabaseProvider.BeginScriptSessionAsync)] = _ => Task.FromResult(script)
        }, postgres);
        var session = Session(provider, postgres, "main",
            new DbObject { Database = "main", Schema = "public", Name = "a", Type = DbObjectType.Table },
            new DbObject { Database = "other", Schema = "public", Name = "b", Type = DbObjectType.Table });
        var vm = new SecurityViewModel(new SessionService(null!, null!, null!), DialogFake.Create(out var dialogs));
        vm.Attach(session);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("main", vm.SelectedDatabase);
        Assert.False(vm.IsBusy);
        return (vm, dialogs);
    }

    [Fact]
    public async Task Table_designer_keeps_an_edited_new_table_across_a_reconnect()
    {
        var profile = new ConnectionProfile { ProviderKey = SqlServer, Name = "fake", Database = "main" };
        var vm = new TableDesignerViewModel(new SessionService(null!, null!, null!), DialogFake.Create(out _));
        vm.Attach(DesignerSession(profile, Constraints()));
        await Until(() => vm.Status.Contains("existing table(s)"));

        vm.TableName = "Invoices";
        vm.Attach(null);
        vm.Attach(DesignerSession(profile, Constraints()));
        await Until(() => vm.Status.Contains("existing table(s)"));

        Assert.Equal("Invoices", vm.TableName);
        Assert.Equal("main", vm.SelectedDatabase);
    }

    [Fact]
    public async Task Table_designer_keeps_an_open_table_to_alter_across_a_reconnect()
    {
        var profile = new ConnectionProfile { ProviderKey = SqlServer, Name = "fake", Database = "main" };
        var vm = new TableDesignerViewModel(new SessionService(null!, null!, null!), DialogFake.Create(out _));
        vm.Attach(DesignerSession(profile, Constraints()));
        await vm.OpenTableAsync(new DbObject { Database = "main", Schema = "dbo", Name = "Orders", Type = DbObjectType.Table });
        Assert.True(vm.IsAltering);

        vm.Attach(null);
        Assert.True(vm.IsAltering);
        vm.Attach(DesignerSession(profile, Constraints()));
        await Task.Delay(300); // the context read the reconnect starts (it would reset the design if anything did)

        Assert.True(vm.IsAltering);
        Assert.Equal("Changing dbo.Orders", vm.ModeText);
        Assert.Equal("main", vm.SelectedDatabase);
    }

    [Fact]
    public async Task Table_designer_shows_the_table_opened_last_when_an_earlier_open_finishes_later()
    {
        var slow = Constraints();
        var calls = new Queue<TaskCompletionSource<DbTableConstraints>>([slow, Constraints()]);
        var vm = new TableDesignerViewModel(new SessionService(null!, null!, null!), DialogFake.Create(out _));
        vm.Attach(DesignerSession(new ConnectionProfile { ProviderKey = SqlServer, Name = "fake", Database = "main" }, calls.Dequeue));

        var first = vm.OpenTableAsync(new DbObject { Database = "main", Schema = "dbo", Name = "Orders", Type = DbObjectType.Table });
        await Until(() => calls.Count == 1);
        var second = vm.OpenTableAsync(new DbObject { Database = "main", Schema = "dbo", Name = "Customers", Type = DbObjectType.Table });
        calls.Peek().SetResult(DbTableConstraints.None);
        await second;
        slow.SetResult(DbTableConstraints.None);
        await first;

        Assert.Equal("Changing dbo.Customers", vm.ModeText);
        Assert.Equal("Customers", vm.TableName);
    }

    [Fact]
    public void Er_model_flush_writes_the_pending_autosave_at_once()
    {
        var root = Path.Combine(Path.GetTempPath(), "dbx-er-" + Guid.NewGuid().ToString("N"));
        try
        {
            var vm = new ErModelViewModel(new SessionService(null!, null!, null!), DialogFake.Create(out _), new Application.AppPaths(root));
            vm.AddTableCommand.Execute(null);
            vm.FlushAutosave();

            var saved = Application.Modeling.ErModelFile.Read(File.ReadAllText(Path.Combine(root, "er-model." + Application.Modeling.ErModelFile.Extension)));
            Assert.Equal(vm.Model.Tables.Count, saved.Tables.Count);
            Assert.NotEmpty(saved.Tables);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Query_builder_stops_offering_the_previous_databases_tables_when_another_is_picked()
    {
        var never = new TaskCompletionSource<DbTableConstraints>();
        var vm = new QueryBuilderViewModel(new SessionService(null!, null!, null!));
        vm.Attach(DesignerSession(new ConnectionProfile { ProviderKey = SqlServer, Name = "fake", Database = "main" }, never));
        await Until(() => vm.AvailableTables.Count == 2);

        vm.SelectedDatabase = "elsewhere";

        Assert.Empty(vm.AvailableTables);
        Assert.Null(vm.AddTable(new DbObject { Database = "main", Schema = "dbo", Name = "Orders", Type = DbObjectType.Table }, null));
    }

    private const string SqlServer = Application.Copy.SqlDialect.SqlServerKey;

    private static TaskCompletionSource<DbTableConstraints> Constraints() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>A SQL Server session on "main" with tables dbo.Orders and dbo.Customers; each read of a table's
    /// constraints waits on what <paramref name="constraints"/> hands out (a fresh, already finished one by default).</summary>
    private static DatabaseSession DesignerSession(ConnectionProfile profile, TaskCompletionSource<DbTableConstraints> first) =>
        DesignerSession(profile, () =>
        {
            first.TrySetResult(DbTableConstraints.None);
            return first;
        });

    private static DatabaseSession DesignerSession(ConnectionProfile profile, Func<TaskCompletionSource<DbTableConstraints>> constraints)
    {
        var provider = ScriptedProvider.Create(new()
        {
            [nameof(IDatabaseProvider.GetTableConstraintsAsync)] = _ => constraints().Task
        }, SqlServer);
        DbColumn Id(string table) => new() { Database = "main", Schema = "dbo", Table = table, Name = table + "Id", DataType = "int", BaseType = "int", Ordinal = 1 };
        return new DatabaseSession(profile, null!, provider, "1", new MetadataSnapshot
        {
            Objects =
            [
                new DbObject { Database = "main", Schema = "dbo", Name = "Orders", Type = DbObjectType.Table },
                new DbObject { Database = "main", Schema = "dbo", Name = "Customers", Type = DbObjectType.Table }
            ],
            Columns = [Id("Orders"), Id("Customers")], Modules = [], ForeignKeys = [], Indexes = [], RefreshedAt = DateTimeOffset.Now
        });
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition did not come true.");
            await Task.Delay(10);
        }
    }

    private static DbIndex Index(string name) => new() { Schema = "dbo", Table = "t", Name = name };

    internal static DatabaseSession Session(IDatabaseProvider provider, string providerKey = "Fake", string database = "", params DbObject[] objects) =>
        new(new ConnectionProfile { ProviderKey = providerKey, Name = "fake", Database = database }, null!, provider, "1", new MetadataSnapshot
        {
            Objects = objects, Columns = [], Modules = [], ForeignKeys = [], Indexes = [], RefreshedAt = DateTimeOffset.Now
        });

    /// <summary>Runs scripts, waiting for <c>release</c> (when given) after telling <c>running</c>.</summary>
    private sealed class BlockingScriptSession(TaskCompletionSource? running, TaskCompletionSource? release) : IScriptSession
    {
        public List<string> Executed { get; } = [];

        public async Task<int> ExecuteAsync(string sql, int timeoutSeconds, CancellationToken ct = default)
        {
            Executed.Add(sql);
            running?.TrySetResult();
            if (release is not null) await release.Task;
            return 0;
        }

        public Task<QueryExecutionResult> QueryAsync(string sql, int timeoutSeconds, int maxRows = int.MaxValue,
            CancellationToken ct = default, ReadOnlyScript? readOnly = null) => throw new NotSupportedException();

        public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task RollbackAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>An <see cref="IDialogService"/> that confirms everything and answers text prompts from a queue.</summary>
    public class DialogFake : DispatchProxy
    {
        public Queue<string> Names { get; } = new();

        public static IDialogService Create(out DialogFake fake)
        {
            var proxy = Create<IDialogService, DialogFake>();
            fake = (DialogFake)(object)proxy;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            nameof(IDialogService.ConfirmAsync) => Task.FromResult(true),
            nameof(IDialogService.PromptTextAsync) => Task.FromResult<string?>(Names.Dequeue()),
            var name => throw new NotSupportedException(name)
        };
    }

    /// <summary>An <see cref="IDatabaseProvider"/> that answers each method from a table of handlers.</summary>
    public class ScriptedProvider : DispatchProxy
    {
        private Dictionary<string, Func<object?[], object?>> _handlers = [];
        private string _key = "Fake";

        public static IDatabaseProvider Create(Dictionary<string, Func<object?[], object?>> handlers, string providerKey = "Fake")
        {
            var provider = Create<IDatabaseProvider, ScriptedProvider>();
            var fake = (ScriptedProvider)(object)provider;
            fake._handlers = handlers;
            fake._key = providerKey;
            return provider;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_ProviderKey" => _key,
            nameof(IAsyncDisposable.DisposeAsync) => ValueTask.CompletedTask,
            var name when _handlers.TryGetValue(name, out var handler) => handler(args ?? []),
            var name => throw new NotSupportedException(name)
        };
    }
}
